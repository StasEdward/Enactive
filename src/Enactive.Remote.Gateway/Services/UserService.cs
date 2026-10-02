namespace Enactive.Remote.Gateway.Services;

using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>
/// Everything a signed-in person is allowed to do, and only to what is theirs.
///
/// <para>Nothing here executes anything. Every action that reaches the computer becomes a COMMAND
/// in a queue the Host drains, and the Host is free to refuse it when it arrives - it may already
/// have been answered on the desktop, or expired, or the run may be gone. That asymmetry is the
/// design: the person asks, the Host decides.</para>
///
/// <para><b>Every lookup names the owner.</b> Each query that finds a computer, task, run, request
/// or used command id has <c>owner_id = @owner</c> in its WHERE, and another person's id is refused
/// in exactly the words used for an id that does not exist. Reading the row first and checking its
/// owner afterwards leaves its existence - and on the idempotency path, its whole payload - one
/// forgotten check away from somebody else.</para>
///
/// <para><b>A locking lookup goes through a key that starts with the owner.</b> hosts and runs have a
/// global primary key on id, approvals one on (host_id, id), and MySQL answers
/// <c>WHERE owner_id = @owner AND id = @id FOR UPDATE</c> through it: it locks the row with that id
/// first and applies the owner filter afterwards. So Bob asking for Alice's id locked Alice's row -
/// he waited for any transaction of hers that held it, which told him the id exists, and his own
/// transaction held up hers. Through the owner's unique key, Bob's lookup finds no entry under his
/// owner id and locks nothing of Alice's. Tables whose primary key starts with owner_id (tasks,
/// commands) need no hint.</para>
///
/// <para><b>Lock order.</b> The same as <see cref="HostService"/>'s, so no two paths wait for each other
/// in a cycle: the account first, FOR UPDATE, in every call that creates something counted against a
/// limit (see <see cref="Quota"/>); then the computer, FOR SHARE; then the target - the task, the run
/// or the request; then the command row; and a revocation locks the computer and then its commands. A
/// command whose target names its computer reads that one column without a lock to find it. Locking the target
/// first was a three-way deadlock: an answer held its request and waited to read the computer
/// behind a revocation queued for it exclusively, the revocation waited for the computer's own
/// report holding it shared, and the report waited for the request. A deadlock the order cannot
/// prevent is run again whole by <see cref="Database.InTransactionAsync{T}"/>.</para>
///
/// <para><b>Nothing a person wrote is readable here.</b> A task, and the authorization of every
/// command, arrive sealed by the browser. This checks that each is an envelope of a sensible size and
/// passes it through; only the computer can open it.</para>
/// </summary>
/// <param name="retentionDays">
/// How long ended runs and unused tasks are kept (see <see cref="Retention"/>). Read only to tell a person whose
/// storage is full when it frees itself; the gateway passes the configured value.
/// </param>
public sealed class UserService(
    Database db, Limits limits, TimeProvider clock, int retentionDays = Retention.DefaultDays)
{
    // A request body is at most 64 KB, so no sealed field can be larger; each bound below is what is
    // left for that field once the rest of its request is accounted for. A task carries the
    // person's title and prompt and may take nearly all of it.
    internal const int MaxSealedTask = 64_000;

    // A command's seal holds a few ids, a decision and a timestamp: a few hundred characters. The
    // request limit is 64 KB; 2 000 leaves room and still refuses a prompt sent where an
    // authorization belongs, which the computer would otherwise refuse a day later with nobody
    // watching.
    internal const int MaxSealedCommand = 2_000;

    // A device command's seal holds a device id and at most one public key. The request limit is
    // 64 KB; 2 000 is the same room as any other command's.
    internal const int MaxSealedDevice = 2_000;

    // The width of hosts.label. Longer is refused rather than cut: the label is what the person
    // will recognise the computer by, and a silently shortened one may not be.
    private const int MaxLabel = 80;

    // ── computers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a computer to this person and returns its credential ONCE. Nothing stores the
    /// token, only its hash. The owner is the caller, never a field of the request.
    /// </summary>
    public async Task<(string Id, string Label, string Token)> RegisterHostAsync(
        UserAccess user, string? label, CancellationToken ct)
    {
        var name = Required(label, MaxLabel, "label");
        var id = Ids.New();
        var token = Ids.NewToken();

        await db.InTransactionAsync(async (connection, transaction) =>
        {
            await Quota.LockAccountAsync(connection, transaction, user.UserId);

            // A revoked computer does not count: revoking one is how the person makes room for another.
            var held = await connection.ReadOneAsync(transaction,
                "SELECT COUNT(*) FROM hosts WHERE owner_id = @owner AND revoked = 0",
                reader => reader.GetInt64(0), ("@owner", user.UserId));

            if (held >= limits.HostsPerUser)
            {
                throw GatewayFault.QuotaExceeded("computers", limits.HostsPerUser, "revoke one to add another");
            }

            var now = clock.GetUtcNow();

            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO hosts (id, owner_id, label, token_hash, revoked, created_at)
                VALUES (@id, @owner, @label, @hash, 0, @now)
                """,
                ("@id", id), ("@owner", user.UserId), ("@label", name), ("@hash", Ids.Hash(token)),
                ("@now", now));

            await Audit.WriteAsync(
                connection, transaction, now, user.UserId, Audit.User(user.UserId), Audit.HostRegistered, id);
        }, ct);

        return (id, name, token);
    }

    /// <summary>
    /// Withdraws a credential. Commands nobody has accepted yet are withdrawn with it; commands the
    /// Host already ACCEPTED are left alone, because it owns them now and may well be carrying one
    /// out - pretending otherwise would make the panel claim a stop that never happened.
    ///
    /// <para>The computer's runs that have not ended are marked Interrupted, with no summary - the
    /// gateway has no key to write one - and their open requests are withdrawn. A revoked computer can
    /// never report again and its runs cannot be stopped from here, so left as they were they stayed
    /// "running" on the panel for ever and each held one of the account's active-run places: three of
    /// them and the account could start nothing, with no remedy (controller ruling I1 of Task 8.1).
    /// Revoking a computer that died is how the person gets those places back.</para>
    /// </summary>
    public Task RevokeHostAsync(UserAccess user, string hostId, CancellationToken ct)
        => db.InTransactionAsync(async (connection, transaction) =>
        {
            // The account first, as every path that writes runs takes it: retention deletes ended runs
            // under it, and taking runs here without it could meet that pass in the other order.
            await Quota.LockAccountAsync(connection, transaction, user.UserId);

            // FORCE INDEX (see the class comment): through the primary key this locked another person's row.
            var revoked = await connection.ReadOneAsync(transaction,
                """
                SELECT revoked FROM hosts FORCE INDEX (ux_hosts_owner)
                WHERE owner_id = @owner AND id = @host
                FOR UPDATE
                """,
                reader => (bool?)reader.GetBoolean(0), ("@owner", user.UserId), ("@host", hostId));

            if (revoked is null)
            {
                throw NoSuchComputer();
            }

            var now = clock.GetUtcNow();

            // Only the first revocation is the person's log's: a repeat changes nothing, and a second row
            // would say the computer was removed twice.
            if (!revoked.Value)
            {
                await Audit.WriteAsync(
                    connection, transaction, now, user.UserId, Audit.User(user.UserId), Audit.HostRevoked, hostId);
            }

            await connection.ExecuteAsync(transaction,
                "UPDATE hosts SET revoked = 1, last_seen_at = NULL WHERE owner_id = @owner AND id = @host",
                ("@owner", user.UserId), ("@host", hostId));

            // The runs, then their requests, then the commands: the lock order of the class comment.
            await connection.ExecuteAsync(transaction,
                """
                UPDATE runs FORCE INDEX (ux_runs_owner_host)
                SET status = @interrupted, ended_at = @now
                WHERE owner_id = @owner AND host_id = @host
                  AND status NOT IN ('Completed', 'Failed', 'Incomplete', 'Cancelled', 'Interrupted')
                """,
                ("@interrupted", RemoteRunStatus.Interrupted), ("@now", now),
                ("@owner", user.UserId), ("@host", hostId));

            await connection.ExecuteAsync(transaction,
                """
                UPDATE approvals SET status = @invalidated
                WHERE owner_id = @owner AND host_id = @host AND status IN ('Pending', 'DecisionQueued')
                """,
                ("@invalidated", ApprovalStatus.Invalidated), ("@owner", user.UserId), ("@host", hostId));

            await connection.ExecuteAsync(transaction,
                """
                UPDATE commands SET status = @rejected
                WHERE owner_id = @owner AND host_id = @host AND status = 'PendingDelivery'
                """,
                ("@rejected", CommandStatus.Rejected), ("@owner", user.UserId), ("@host", hostId));
        }, ct);

    // ── tasks ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a task down without starting it. The workspace has to be one the Host has actually
    /// published: the person names a workspace, never a folder, and this is where that is enforced.
    ///
    /// <para>The id is the browser's, because the browser seals the task under it before the
    /// gateway has seen anything. So a retried create arrives with the same id, and is a no-op when
    /// the content is the same; the same id with different content is a conflict, since keeping the
    /// first silently would lose the second.</para>
    /// </summary>
    public async Task CreateTaskAsync(
        UserAccess user, string taskId, string hostId, string workspaceId, string sealedTask,
        CancellationToken ct)
    {
        RequireUuid(taskId, "taskId");
        RequireSealed(sealedTask, MaxSealedTask, "sealedTask");

        var fingerprint = Ids.Fingerprint($"{hostId}|{workspaceId}|{sealedTask}");

        await db.InTransactionAsync(async (connection, transaction) =>
        {
            await Quota.LockAccountAsync(connection, transaction, user.UserId);
            await EnsureLiveHostAsync(connection, transaction, user.UserId, hostId);

            var previous = await connection.ReadOneAsync(transaction,
                "SELECT fingerprint FROM tasks WHERE owner_id = @owner AND id = @id FOR UPDATE",
                reader => reader.GetString("fingerprint"),
                ("@owner", user.UserId), ("@id", taskId));

            if (previous is not null)
            {
                if (!string.Equals(previous, fingerprint, StringComparison.Ordinal))
                {
                    throw GatewayFault.Conflict("That task id was already used for a different task.");
                }

                return;
            }

            var known = await connection.ExistsAsync(transaction,
                """
                SELECT 1 FROM host_workspaces
                WHERE owner_id = @owner AND host_id = @host AND workspace_id = @workspace
                """,
                ("@owner", user.UserId), ("@host", hostId), ("@workspace", workspaceId));

            if (!known)
            {
                throw GatewayFault.BadRequest(
                    "Choose a workspace this computer has published. It publishes them when it connects.");
            }

            var now = clock.GetUtcNow();

            // After the retry above, so a repeated create of a task the person already has is answered at
            // the limit too. Nothing deletes a task, so a day's count is what keeps a script from writing
            // them as fast as the request limit allows, each one up to the size of a request.
            var today = await connection.ReadOneAsync(transaction,
                "SELECT COUNT(*) FROM tasks WHERE owner_id = @owner AND created_at > @since",
                reader => reader.GetInt64(0), ("@owner", user.UserId), ("@since", now.AddDays(-1)));

            if (today >= limits.TasksPerDay)
            {
                throw GatewayFault.QuotaExceeded(
                    "tasks made in the last day", limits.TasksPerDay, "make more tomorrow");
            }

            await Quota.ChargeSealedAsync(
                connection, transaction, user.UserId, Quota.SizeOf(sealedTask), limits, retentionDays);

            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO tasks (owner_id, id, host_id, workspace_id, sealed, fingerprint, created_at)
                VALUES (@owner, @id, @host, @workspace, @sealed, @fingerprint, @now)
                """,
                ("@owner", user.UserId), ("@id", taskId), ("@host", hostId), ("@workspace", workspaceId),
                ("@sealed", sealedTask), ("@fingerprint", fingerprint), ("@now", now));
        }, ct);
    }

    // ── commands ────────────────────────────────────────────────────────────
    //
    // Each command method follows one order: check the target's computer is live, then lock the
    // target in the caller's account, then look for a retry of this command id, then check the
    // target's state. The computer comes first because that is the lock order (see the class
    // comment). A retry is looked for AFTER the target and its computer, so a repeated request is
    // answered only while the caller still owns the thing and the computer can still receive it; and
    // BEFORE the state checks, because the first attempt changed that state - a retried cancel would
    // otherwise be refused by the very request it repeats.

    /// <summary>
    /// Queues a run of an existing task. The command carries the task exactly as the browser sealed
    /// it, with the person's sealed authorization beside it; the gateway adds the run id it made.
    /// </summary>
    public async Task<HostCommand> StartAsync(
        UserAccess user, string taskId, string commandId, string sealedStart, CancellationToken ct)
    {
        RequireUuid(commandId, "commandId");
        RequireSealed(sealedStart, MaxSealedCommand, "sealedStart");

        // The seal is part of the action: a retry resends the same bytes, and the same id with a
        // different authorization is a different request.
        var fingerprint = Ids.Fingerprint($"StartTask:{taskId}:{sealedStart}");

        return await db.InTransactionAsync(async (connection, transaction) =>
        {
            await Quota.LockAccountAsync(connection, transaction, user.UserId);

            // The computer is locked before the task, so the task's computer is read first and
            // without a lock. A task never moves to another computer: what this finds is still true
            // once both are locked.
            var hostId = await connection.ReadOneAsync(transaction,
                "SELECT host_id FROM tasks WHERE owner_id = @owner AND id = @id",
                reader => reader.GetString("host_id"),
                ("@owner", user.UserId), ("@id", taskId))
                ?? throw NoSuchTask();

            await EnsureLiveHostAsync(connection, transaction, user.UserId, hostId);

            // Locked, so two starts of one task take turns: each sees the other's run when it checks
            // for an active one below.
            var task = await connection.ReadOneAsync(transaction,
                """
                SELECT host_id, workspace_id, sealed FROM tasks
                WHERE owner_id = @owner AND id = @id
                FOR UPDATE
                """,
                reader => new TaskTarget(
                    reader.GetString("host_id"), reader.GetString("workspace_id"), reader.GetString("sealed")),
                ("@owner", user.UserId), ("@id", taskId))
                ?? throw NoSuchTask();

            if (await ExistingCommandAsync(connection, transaction, user.UserId, commandId, fingerprint)
                is { } repeated)
            {
                return repeated;
            }

            // One at a time. Two runs of one task would race each other over the same files, and the
            // panel would have no way to say which timeline belonged to which.
            var active = await connection.ExistsAsync(transaction,
                """
                SELECT 1 FROM runs
                WHERE owner_id = @owner AND task_id = @task
                  AND status NOT IN ('Completed', 'Failed', 'Incomplete', 'Cancelled', 'Interrupted')
                FOR UPDATE
                """,
                ("@owner", user.UserId), ("@task", taskId));

            if (active)
            {
                throw GatewayFault.Conflict("This task is already running. Wait for it, or stop it first.");
            }

            // Every run of the account's that has not ended, on any of its live computers: each holds a
            // computer's model and the account's share of the queue until it does. A revoked computer's
            // runs are ended when it is revoked; they are left out here as well, so a run that slipped past
            // that can still never hold a place it cannot give back (controller ruling I1 of Task 8.1).
            var running = await connection.ReadOneAsync(transaction,
                """
                SELECT COUNT(*) FROM runs r
                JOIN hosts h ON h.owner_id = r.owner_id AND h.id = r.host_id
                WHERE r.owner_id = @owner AND h.revoked = 0
                  AND r.status NOT IN ('Completed', 'Failed', 'Incomplete', 'Cancelled', 'Interrupted')
                """,
                reader => reader.GetInt64(0), ("@owner", user.UserId));

            if (running >= limits.ActiveRunsPerUser)
            {
                throw GatewayFault.QuotaExceeded(
                    "runs in progress", limits.ActiveRunsPerUser, "wait for one to end, or stop one, to start another");
            }

            var runId = Ids.New();
            var now = clock.GetUtcNow();

            var payload = RemoteJson.Serialize(new StartTaskPayload(
                runId, taskId, task.WorkspaceId, task.Sealed, sealedStart));

            // The command carries a copy of the sealed task, so every start stores the task again; uncounted,
            // a script restarting one large task stored it without limit. This is where the byte limit is
            // checked for a run: what the computer then reports about it is always taken (see HostService),
            // and retention gives this back with the run.
            await Quota.ChargeSealedAsync(
                connection, transaction, user.UserId, payload.Length, limits, retentionDays);

            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO runs (id, owner_id, task_id, host_id, status, applied_sequence, created_at)
                VALUES (@id, @owner, @task, @host, @queued, 0, @now)
                """,
                ("@id", runId), ("@owner", user.UserId), ("@task", taskId), ("@host", task.HostId),
                ("@queued", RemoteRunStatus.Queued), ("@now", now));

            return await QueueAsync(connection, transaction, user.UserId, commandId, task.HostId, runId,
                CommandKind.StartTask, payload, fingerprint, now);
        }, ct);
    }

    /// <summary>
    /// Asks a run to stop. Asking is all this does: the run is marked as having been asked, and
    /// only the Host's own Cancelled event says it actually stopped. External effects may already
    /// have happened, and a panel that said "cancelled" the moment the button was pressed would be
    /// making that up.
    /// </summary>
    public async Task<HostCommand> CancelAsync(
        UserAccess user, string runId, string commandId, string sealedCancel, CancellationToken ct)
    {
        RequireUuid(commandId, "commandId");
        RequireSealed(sealedCancel, MaxSealedCommand, "sealedCancel");

        var fingerprint = Ids.Fingerprint($"CancelRun:{runId}:{sealedCancel}");

        return await db.InTransactionAsync(async (connection, transaction) =>
        {
            await Quota.LockAccountAsync(connection, transaction, user.UserId);

            // The computer is locked before the run, so the run's computer is read first and without
            // a lock. A run never moves to another computer: what this finds is still true once both
            // are locked.
            var hostId = await connection.ReadOneAsync(transaction,
                "SELECT host_id FROM runs WHERE owner_id = @owner AND id = @run",
                reader => reader.GetString("host_id"),
                ("@owner", user.UserId), ("@run", runId))
                ?? throw NoSuchRun();

            await EnsureLiveHostAsync(connection, transaction, user.UserId, hostId);

            // FORCE INDEX (see the class comment): through the primary key this locked another person's run.
            var run = await connection.ReadOneAsync(transaction,
                """
                SELECT host_id, status FROM runs FORCE INDEX (ux_runs_owner)
                WHERE owner_id = @owner AND id = @run
                FOR UPDATE
                """,
                reader => new RunTarget(reader.GetString("host_id"), reader.Enum<RemoteRunStatus>("status")),
                ("@owner", user.UserId), ("@run", runId))
                ?? throw NoSuchRun();

            if (await ExistingCommandAsync(connection, transaction, user.UserId, commandId, fingerprint)
                is { } repeated)
            {
                return repeated;
            }

            if (RunLifecycle.IsTerminal(run.Status))
            {
                throw GatewayFault.Conflict("That run has already ended.");
            }

            await connection.ExecuteAsync(transaction,
                "UPDATE runs SET status = @requested WHERE owner_id = @owner AND id = @run",
                ("@requested", RemoteRunStatus.CancelRequested), ("@owner", user.UserId), ("@run", runId));

            return await QueueAsync(connection, transaction, user.UserId, commandId, run.HostId, runId,
                CommandKind.CancelRun, RemoteJson.Serialize(new CancelRunPayload(runId, sealedCancel)),
                fingerprint, clock.GetUtcNow());
        }, ct);
    }

    /// <summary>
    /// The person's answer to a permission request. It moves the request to
    /// <see cref="ApprovalStatus.DecisionQueued"/> and NOT to Allowed: an answer is a command that
    /// still has to reach the Host and may lose there to a decision already taken on the desktop.
    ///
    /// <para>A request the Host marked as not remotely decidable - a shell - is refused here. That
    /// refusal is the boundary; whether the panel drew a button is a detail of the panel.</para>
    ///
    /// <para><paramref name="decision"/> is kept in the clear only so the panel can show "Sent:
    /// Allow". The Host acts on the sealed copy, the only one a gateway cannot write.</para>
    /// </summary>
    public async Task<HostCommand> DecideAsync(
        UserAccess user, string approvalId, string hostId, string commandId, RemoteDecision decision,
        string actionHash, string sealedDecision, CancellationToken ct)
    {
        RequireUuid(commandId, "commandId");
        RequireSealed(sealedDecision, MaxSealedCommand, "sealedDecision");

        var fingerprint = Ids.Fingerprint(
            $"ResolveApproval:{hostId}:{approvalId}:{decision}:{actionHash}:{sealedDecision}");

        return await db.InTransactionAsync(async (connection, transaction) =>
        {
            // The account, then the computer before the request: see the lock order in the class comment.
            await Quota.LockAccountAsync(connection, transaction, user.UserId);
            await EnsureLiveHostAsync(connection, transaction, user.UserId, hostId);

            // Addressed by its computer as well: approval ids are made by the Host and unique only
            // there. FORCE INDEX (see the class comment): through the primary key, (host_id, id), this
            // locked another person's request.
            var approval = await connection.ReadOneAsync(transaction,
                """
                SELECT run_id, action_hash, remote_decidable, status, expires_at
                FROM approvals FORCE INDEX (ux_approvals_owner_host)
                WHERE owner_id = @owner AND host_id = @host AND id = @id
                FOR UPDATE
                """,
                reader => new ApprovalTarget(
                    reader.GetString("run_id"),
                    reader.GetString("action_hash"),
                    reader.GetBoolean("remote_decidable"),
                    reader.Enum<ApprovalStatus>("status"),
                    reader.Utc("expires_at")),
                ("@owner", user.UserId), ("@host", hostId), ("@id", approvalId))
                ?? throw NoSuchRequest();

            if (await ExistingCommandAsync(connection, transaction, user.UserId, commandId, fingerprint)
                is { } repeated)
            {
                return repeated;
            }

            if (!approval.RemoteDecidable)
            {
                throw GatewayFault.ApprovalNotRemotelyDecidable(approvalId);
            }

            if (approval.Status != ApprovalStatus.Pending)
            {
                throw GatewayFault.ApprovalAlreadyResolved(approvalId, approval.Status);
            }

            if (approval.ExpiresAt <= clock.GetUtcNow())
            {
                throw GatewayFault.Conflict("That request has expired. The computer will report how it ended.");
            }

            if (!string.Equals(approval.ActionHash, actionHash, StringComparison.Ordinal))
            {
                throw GatewayFault.ActionHashMismatch(approvalId);
            }

            var runStatus = await connection.ReadOneAsync(transaction,
                "SELECT status FROM runs WHERE owner_id = @owner AND id = @run",
                reader => (RemoteRunStatus?)reader.Enum<RemoteRunStatus>("status"),
                ("@owner", user.UserId), ("@run", approval.RunId));

            if (runStatus is null || RunLifecycle.IsTerminal(runStatus.Value))
            {
                throw GatewayFault.Conflict("That run has ended, so this permission no longer applies.");
            }

            await connection.ExecuteAsync(transaction,
                """
                UPDATE approvals SET status = @queued, requested_decision = @decision
                WHERE owner_id = @owner AND host_id = @host AND id = @id
                """,
                ("@queued", ApprovalStatus.DecisionQueued), ("@decision", decision),
                ("@owner", user.UserId), ("@host", hostId), ("@id", approvalId));

            var payload = RemoteJson.Serialize(new ResolveApprovalPayload(
                approvalId, approval.RunId, approval.ActionHash, sealedDecision));

            return await QueueAsync(connection, transaction, user.UserId, commandId, hostId, approval.RunId,
                CommandKind.ResolveApproval, payload, fingerprint, clock.GetUtcNow());
        }, ct);
    }

    /// <summary>
    /// Revoke or endorse a browser device, on one computer. Which device, and its key, are inside the
    /// seal: which browsers a computer trusts is the computer's decision, made on what only a trusted
    /// device could seal, and the gateway only carries it.
    /// </summary>
    public async Task<HostCommand> SendDeviceCommandAsync(
        UserAccess user, string hostId, CommandKind kind, string commandId, string sealedPayload,
        CancellationToken ct)
    {
        // Nothing here checks a run, a task or a request, so the commands that need those checks
        // must not be able to come this way.
        if (kind is not (CommandKind.RevokeDevice or CommandKind.EndorseDevice))
        {
            throw GatewayFault.BadRequest($"{kind} is not a device command.");
        }

        RequireUuid(commandId, "commandId");
        RequireSealed(sealedPayload, MaxSealedDevice, "sealed");

        var fingerprint = Ids.Fingerprint($"{kind}:{hostId}:{sealedPayload}");

        return await db.InTransactionAsync(async (connection, transaction) =>
        {
            await Quota.LockAccountAsync(connection, transaction, user.UserId);
            await EnsureLiveHostAsync(connection, transaction, user.UserId, hostId);

            if (await ExistingCommandAsync(connection, transaction, user.UserId, commandId, fingerprint)
                is { } repeated)
            {
                return repeated;
            }

            return await QueueAsync(connection, transaction, user.UserId, commandId, hostId, runId: null, kind,
                RemoteJson.Serialize(new DevicePayload(sealedPayload)), fingerprint, clock.GetUtcNow());
        }, ct);
    }

    // ── notices ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Marks read what the person was shown: their notices up to the ordinal of the snapshot on their
    /// screen. Without the bound, a notice committed a second before the click - a permission request
    /// - would be marked read without ever having been displayed.
    ///
    /// <para>The bound is a position on the person's line, so it is refused unless it is one: of the
    /// line's current <paramref name="epoch"/>, and not past the line's end. After the line is reset,
    /// an ordinal of the old epoch counts something else entirely, and taken as it came it would mark
    /// notices read that no screen ever showed. Refused rather than ignored, so a panel that sends a
    /// stale cursor learns it, by the same rules that answer its poll with a full snapshot.</para>
    /// </summary>
    public Task MarkNoticesReadAsync(
        UserAccess user, int epoch, long throughOrdinal, CancellationToken ct)
        => db.InTransactionAsync(async (connection, transaction) =>
        {
            // Read as it stands now, and held until the marking commits. A plain read is the snapshot of
            // the transaction's first read: a reset of the line committing between it and the update
            // below went unseen, the old epoch matched, and ordinals that now count other notices were
            // marked read. Shared, so two clicks do not wait on each other; a reset waits for this one,
            // or this waits for the reset and refuses. A computer's call takes the counter before it
            // writes a notice, so the counter and then the notices, as here, is the order it takes too.
            var line = await connection.ReadOneAsync(transaction,
                "SELECT value, epoch FROM user_streams WHERE owner_id = @owner FOR SHARE",
                reader => ((long Value, int Epoch)?)(reader.GetInt64("value"), reader.GetInt32("epoch")),
                ("@owner", user.UserId));

            if (line is not { } current || current.Epoch != epoch || throughOrdinal > current.Value)
            {
                throw GatewayFault.BadRequest(
                    "That cursor is not one of this account's current line. Refresh, and mark them read again.");
            }

            await connection.ExecuteAsync(transaction,
                """
                UPDATE notices SET is_read = 1
                WHERE owner_id = @owner AND ordinal <= @through AND is_read = 0
                """,
                ("@owner", user.UserId), ("@through", throughOrdinal));
        }, ct);

    // ── shared ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A command id this person has used before.
    ///
    /// <para>Same id and same action: this is a retry, and the caller gets the command it already
    /// queued. Same id, different action: a conflict, loudly - silently returning the first one's
    /// result would mean an action the person asked for never happened and nothing said so.</para>
    ///
    /// <para>Looked up within the caller's account. Keyed on the id alone, Bob sending Alice's id
    /// with a matching action would be handed Alice's command - her run id and her sealed task - as
    /// "the one you already queued".</para>
    /// </summary>
    private static async Task<HostCommand?> ExistingCommandAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId, string commandId,
        string fingerprint)
    {
        var previous = await connection.ReadOneAsync(transaction,
            """
            SELECT id, host_id, kind, payload, fingerprint, status, created_at, expires_at
            FROM commands
            WHERE owner_id = @owner AND id = @id
            FOR UPDATE
            """,
            reader => (
                Command: new HostCommand(
                    reader.GetString("id"), reader.GetString("host_id"),
                    reader.Enum<CommandKind>("kind"), reader.GetString("payload"),
                    reader.Enum<CommandStatus>("status"),
                    reader.Utc("created_at"), reader.Utc("expires_at")),
                Fingerprint: reader.GetString("fingerprint")),
            ("@owner", ownerId), ("@id", commandId));

        if (previous.Command is null)
        {
            return null;
        }

        return string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal)
            ? previous.Command
            : throw GatewayFault.Conflict("That request id was already used for a different action.");
    }

    /// <summary>
    /// Queues a command for the computer, within its allowance. The caller holds the account's lock, which
    /// is what the count is taken under, and has already answered a retry of this id, which takes no place.
    /// </summary>
    /// <param name="runId">
    /// The run the command is about, or null for one about no run. Kept so retention removes the command
    /// with its run: a start's command holds a copy of the task, and nothing else ever deleted one.
    /// </param>
    private async Task<HostCommand> QueueAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId,
        string commandId, string hostId, string? runId, CommandKind kind, string payload, string fingerprint,
        DateTimeOffset now)
    {
        // Commands the computer has not collected. One that expired no longer counts, whether or not a
        // sync has written it off yet: a computer that was off for a day would otherwise come back to an
        // account that could not ask it anything until it had synced.
        var waiting = await connection.ReadOneAsync(transaction,
            """
            SELECT COUNT(*) FROM commands
            WHERE host_id = @host AND status = 'PendingDelivery' AND expires_at > @now AND owner_id = @owner
            """,
            reader => reader.GetInt64(0), ("@host", hostId), ("@now", now), ("@owner", ownerId));

        if (waiting >= limits.QueuedCommandsPerHost)
        {
            throw GatewayFault.QuotaExceeded(
                "requests waiting for this computer", limits.QueuedCommandsPerHost,
                "wait for the computer to collect them");
        }

        // The protocol's lifetime, not one of the gateway's own: the computer refuses a sealed
        // command issued longer ago than this, so a gateway that kept it longer would only be
        // delivering a refusal.
        var expires = now.Add(RemoteProtocol.CommandLifetime);

        await connection.ExecuteAsync(transaction,
            """
            INSERT INTO commands (owner_id, id, host_id, run_id, kind, payload, fingerprint, status, created_at,
                                  expires_at)
            VALUES (@owner, @id, @host, @run, @kind, @payload, @fingerprint, @pending, @now, @expires)
            """,
            ("@owner", ownerId), ("@id", commandId), ("@host", hostId),
            ("@run", (object?)runId ?? DBNull.Value), ("@kind", kind),
            ("@payload", payload), ("@fingerprint", fingerprint),
            ("@pending", CommandStatus.PendingDelivery), ("@now", now), ("@expires", expires));

        return new HostCommand(
            commandId, hostId, kind, payload, CommandStatus.PendingDelivery, now, expires);
    }

    /// <summary>
    /// The computer is this person's and not revoked.
    ///
    /// <para>A locking read. A revocation holds this row until it commits; read without a lock, the
    /// computer looked live to a start already in flight, whose command was then queued after the
    /// revocation had withdrawn the undelivered ones - a command for a revoked computer, left
    /// pending for a day. Shared, so commands for one computer do not wait on each other. Taken
    /// before anything on the computer is locked: see the lock order in the class comment.</para>
    /// </summary>
    private static async Task EnsureLiveHostAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId, string hostId)
    {
        // FORCE INDEX (see the class comment): through the primary key this locked another person's
        // computer, and Bob waited on any transaction of Alice's that held it.
        var revoked = await connection.ReadOneAsync(transaction,
            """
            SELECT revoked FROM hosts FORCE INDEX (ux_hosts_owner)
            WHERE owner_id = @owner AND id = @host
            FOR SHARE
            """,
            reader => (bool?)reader.GetBoolean("revoked"),
            ("@owner", ownerId), ("@host", hostId));

        if (revoked is null)
        {
            throw NoSuchComputer();
        }

        if (revoked.Value)
        {
            throw GatewayFault.NotFound("That computer has been revoked.");
        }
    }

    // One sentence per kind of thing, the same for a foreign id as for a missing one: a refusal that
    // differed would answer "does somebody else have one by that id?".
    private static GatewayFault NoSuchComputer() => GatewayFault.NotFound("That computer is not registered.");

    private static GatewayFault NoSuchTask() => GatewayFault.NotFound("That task does not exist.");

    private static GatewayFault NoSuchRun() => GatewayFault.NotFound("That run does not exist.");

    private static GatewayFault NoSuchRequest() => GatewayFault.NotFound("That permission request does not exist.");

    /// <summary>
    /// The canonical 36-character form and nothing else. <see cref="Guid.TryParse(string?, out Guid)"/>
    /// also takes the 32-digit and braced forms; a braced id is 38 characters, which the 36-character
    /// columns refused as a database error instead of a 400. And a task id is part of the associated
    /// data the browser sealed under, so it has to be kept exactly as sent, not reformatted.
    /// </summary>
    private static void RequireUuid(string? value, string field)
    {
        if (!Guid.TryParseExact(value, "D", out _))
        {
            throw GatewayFault.BadRequest($"'{field}' must be a UUID.");
        }
    }

    private static void RequireSealed(string? value, int maxChars, string field)
    {
        if (!Envelope.LooksSealed(value, maxChars))
        {
            throw GatewayFault.EnvelopeMalformed(field, maxChars);
        }
    }

    private static string Required(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max)
        {
            throw GatewayFault.BadRequest($"'{field}' must be 1 to {max:N0} characters.");
        }

        return value.Trim();
    }

    private sealed record TaskTarget(string HostId, string WorkspaceId, string Sealed);

    private sealed record RunTarget(string HostId, RemoteRunStatus Status);

    private sealed record ApprovalTarget(
        string RunId, string ActionHash, bool RemoteDecidable, ApprovalStatus Status, DateTimeOffset ExpiresAt);
}
