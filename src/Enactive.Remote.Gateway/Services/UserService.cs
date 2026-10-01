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
/// <para><b>Nothing a person wrote is readable here.</b> A task, and the authorization of every
/// command, arrive sealed by the browser. This checks that each is an envelope of a sensible size and
/// passes it through; only the computer can open it.</para>
/// </summary>
public sealed class UserService(Database db, Limits limits, TimeProvider clock)
{
    // A request body is at most 64 KB, so no sealed field can be larger; each bound below is what is
    // left for that field once the rest of its request is accounted for. A task carries the
    // person's title and prompt and may take nearly all of it.
    private const int MaxSealedTask = 64_000;

    // A command's seal holds a few ids, a decision and a timestamp: a few hundred characters. The
    // request limit is 64 KB; 2 000 leaves room and still refuses a prompt sent where an
    // authorization belongs, which the computer would otherwise refuse a day later with nobody
    // watching.
    private const int MaxSealedCommand = 2_000;

    // A device command's seal holds a device id and at most one public key. The request limit is
    // 64 KB; 2 000 is the same room as any other command's.
    private const int MaxSealedDevice = 2_000;

    // The width of hosts.label. Longer is refused rather than cut: the label is what the person
    // will recognise the computer by, and a silently shortened one may not be.
    private const int MaxLabel = 80;

    // Taken now so the signature does not change again when per-account limits are enforced; until
    // then every caller passes Limits.Unlimited and nothing reads it.
    private readonly Limits _limits = limits;

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

        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(null,
            """
            INSERT INTO hosts (id, owner_id, label, token_hash, revoked, created_at)
            VALUES (@id, @owner, @label, @hash, 0, @now)
            """,
            ("@id", id), ("@owner", user.UserId), ("@label", name), ("@hash", Ids.Hash(token)),
            ("@now", clock.GetUtcNow()));

        return (id, name, token);
    }

    /// <summary>
    /// Withdraws a credential. Commands nobody has accepted yet are withdrawn with it; commands the
    /// Host already ACCEPTED are left alone, because it owns them now and may well be carrying one
    /// out - pretending otherwise would make the panel claim a stop that never happened.
    /// </summary>
    public async Task RevokeHostAsync(UserAccess user, string hostId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        var exists = await connection.ExistsAsync(transaction,
            "SELECT 1 FROM hosts WHERE owner_id = @owner AND id = @host FOR UPDATE",
            ("@owner", user.UserId), ("@host", hostId));

        if (!exists)
        {
            throw NoSuchComputer();
        }

        await connection.ExecuteAsync(transaction,
            "UPDATE hosts SET revoked = 1, last_seen_at = NULL WHERE owner_id = @owner AND id = @host",
            ("@owner", user.UserId), ("@host", hostId));

        await connection.ExecuteAsync(transaction,
            """
            UPDATE commands SET status = @rejected
            WHERE owner_id = @owner AND host_id = @host AND status = 'PendingDelivery'
            """,
            ("@rejected", CommandStatus.Rejected), ("@owner", user.UserId), ("@host", hostId));

        await transaction.CommitAsync(ct);
    }

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

        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

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

            await transaction.CommitAsync(ct);
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

        await connection.ExecuteAsync(transaction,
            """
            INSERT INTO tasks (owner_id, id, host_id, workspace_id, sealed, fingerprint, created_at)
            VALUES (@owner, @id, @host, @workspace, @sealed, @fingerprint, @now)
            """,
            ("@owner", user.UserId), ("@id", taskId), ("@host", hostId), ("@workspace", workspaceId),
            ("@sealed", sealedTask), ("@fingerprint", fingerprint), ("@now", clock.GetUtcNow()));

        await transaction.CommitAsync(ct);
    }

    // ── commands ────────────────────────────────────────────────────────────
    //
    // Each command method follows one order: find the target in the caller's account, check its
    // computer is live, then look for a retry of this command id, then check the target's state. A
    // retry is looked for AFTER the target and its computer, so a repeated request is answered only
    // while the caller still owns the thing and the computer can still receive it; and BEFORE the
    // state checks, because the first attempt changed that state - a retried cancel would otherwise
    // be refused by the very request it repeats.

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

        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

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

        await EnsureLiveHostAsync(connection, transaction, user.UserId, task.HostId);

        if (await ExistingCommandAsync(connection, transaction, user.UserId, commandId, fingerprint)
            is { } repeated)
        {
            await transaction.CommitAsync(ct);
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

        var runId = Ids.New();
        var now = clock.GetUtcNow();

        await connection.ExecuteAsync(transaction,
            """
            INSERT INTO runs (id, owner_id, task_id, host_id, status, applied_sequence, created_at)
            VALUES (@id, @owner, @task, @host, @queued, 0, @now)
            """,
            ("@id", runId), ("@owner", user.UserId), ("@task", taskId), ("@host", task.HostId),
            ("@queued", RemoteRunStatus.Queued), ("@now", now));

        var payload = RemoteJson.Serialize(new StartTaskPayload(
            runId, taskId, task.WorkspaceId, task.Sealed, sealedStart));

        var command = await QueueAsync(connection, transaction, user.UserId, commandId, task.HostId,
            CommandKind.StartTask, payload, fingerprint, now);

        await transaction.CommitAsync(ct);
        return command;
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

        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        var run = await connection.ReadOneAsync(transaction,
            "SELECT host_id, status FROM runs WHERE owner_id = @owner AND id = @run FOR UPDATE",
            reader => new RunTarget(reader.GetString("host_id"), reader.Enum<RemoteRunStatus>("status")),
            ("@owner", user.UserId), ("@run", runId))
            ?? throw NoSuchRun();

        await EnsureLiveHostAsync(connection, transaction, user.UserId, run.HostId);

        if (await ExistingCommandAsync(connection, transaction, user.UserId, commandId, fingerprint)
            is { } repeated)
        {
            await transaction.CommitAsync(ct);
            return repeated;
        }

        if (RunLifecycle.IsTerminal(run.Status))
        {
            throw GatewayFault.Conflict("That run has already ended.");
        }

        await connection.ExecuteAsync(transaction,
            "UPDATE runs SET status = @requested WHERE owner_id = @owner AND id = @run",
            ("@requested", RemoteRunStatus.CancelRequested), ("@owner", user.UserId), ("@run", runId));

        var command = await QueueAsync(connection, transaction, user.UserId, commandId, run.HostId,
            CommandKind.CancelRun, RemoteJson.Serialize(new CancelRunPayload(runId, sealedCancel)),
            fingerprint, clock.GetUtcNow());

        await transaction.CommitAsync(ct);
        return command;
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

        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        // Addressed by its computer as well: approval ids are made by the Host and unique only there.
        var approval = await connection.ReadOneAsync(transaction,
            """
            SELECT run_id, action_hash, remote_decidable, status, expires_at
            FROM approvals
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

        await EnsureLiveHostAsync(connection, transaction, user.UserId, hostId);

        if (await ExistingCommandAsync(connection, transaction, user.UserId, commandId, fingerprint)
            is { } repeated)
        {
            await transaction.CommitAsync(ct);
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

        var command = await QueueAsync(connection, transaction, user.UserId, commandId, hostId,
            CommandKind.ResolveApproval, payload, fingerprint, clock.GetUtcNow());

        await transaction.CommitAsync(ct);
        return command;
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

        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        await EnsureLiveHostAsync(connection, transaction, user.UserId, hostId);

        if (await ExistingCommandAsync(connection, transaction, user.UserId, commandId, fingerprint)
            is { } repeated)
        {
            await transaction.CommitAsync(ct);
            return repeated;
        }

        var command = await QueueAsync(connection, transaction, user.UserId, commandId, hostId, kind,
            RemoteJson.Serialize(new DevicePayload(sealedPayload)), fingerprint, clock.GetUtcNow());

        await transaction.CommitAsync(ct);
        return command;
    }

    // ── notices ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Marks read what the person was shown: their notices up to the ordinal of the snapshot on their
    /// screen. Without the bound, a notice committed a second before the click - a permission request
    /// - would be marked read without ever having been displayed.
    /// </summary>
    public async Task MarkNoticesReadAsync(UserAccess user, long throughOrdinal, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(null,
            """
            UPDATE notices SET is_read = 1
            WHERE owner_id = @owner AND ordinal <= @through AND is_read = 0
            """,
            ("@owner", user.UserId), ("@through", throughOrdinal));
    }

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

    private static async Task<HostCommand> QueueAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId,
        string commandId, string hostId, CommandKind kind, string payload, string fingerprint,
        DateTimeOffset now)
    {
        // The protocol's lifetime, not one of the gateway's own: the computer refuses a sealed
        // command issued longer ago than this, so a gateway that kept it longer would only be
        // delivering a refusal.
        var expires = now.Add(RemoteProtocol.CommandLifetime);

        await connection.ExecuteAsync(transaction,
            """
            INSERT INTO commands (owner_id, id, host_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES (@owner, @id, @host, @kind, @payload, @fingerprint, @pending, @now, @expires)
            """,
            ("@owner", ownerId), ("@id", commandId), ("@host", hostId), ("@kind", kind),
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
    /// pending for a day. Shared, so commands for one computer do not wait on each other.</para>
    /// </summary>
    private static async Task EnsureLiveHostAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId, string hostId)
    {
        var revoked = await connection.ReadOneAsync(transaction,
            "SELECT revoked FROM hosts WHERE owner_id = @owner AND id = @host FOR SHARE",
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
