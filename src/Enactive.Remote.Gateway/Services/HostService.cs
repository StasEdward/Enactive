namespace Enactive.Remote.Gateway.Services;

using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>
/// Everything a Host is allowed to do. Three methods, and the third is the whole state machine.
///
/// <para>The Host is authoritative about what happened on the machine; this class maintains a
/// PROJECTION of that and never a second opinion. So its job is narrow and unglamorous: refuse what
/// cannot be true, apply what can, and say precisely why when it refuses - because a Host with a
/// durable outbox has to decide from that answer whether to keep the item or drop it.</para>
///
/// <para><b>A computer acts within its own account, on its own rows.</b> Every call takes a
/// <see cref="HostAccess"/> built from the authenticated token, and every lookup names both the owner
/// and the computer. A run or a request of another person's, or of another of the same person's
/// computers, is refused exactly like one that does not exist. Each computer's outbox speaks only for
/// what that computer is running.</para>
///
/// <para><b>Authority is re-read inside every transaction.</b> The connection was authenticated
/// when it opened, possibly hours ago. The account's status and the computer's revocation are read
/// again, locked for share, at the start of each call, so a revocation or a disablement that
/// commits mid-connection stops the very next call, and one in flight waits for the call that is
/// already running instead of slipping past it.</para>
///
/// <para><b>Lock order.</b> One order for every path, the computer's and the person's (see
/// <see cref="UserService"/>): the account - FOR UPDATE when the call adds to its byte total, FOR SHARE
/// otherwise - then the computer, FOR SHARE; then the run and its requests; then the command rows,
/// each by its own key and never a range of them (a person's command is inserted into that range
/// while its run is locked); then the owner's stream counter.
/// Transactions that take their locks in one order wait for each other but never in a cycle. The
/// order holds for shared reads too: a revocation waiting to lock the computer exclusively queues
/// ahead of every later shared read of it, so a person's answer that held its request and then read
/// the computer waited behind the revocation, which waited behind this class's report holding the
/// computer shared and reaching for that request - a deadlock, and one of the three rolled back.
/// The computer is only ever shared here, and marking it seen happens after the commit: writing it
/// inside would upgrade the shared lock, and two calls of one computer each holding it shared and
/// each wanting it exclusively deadlock. What no order of rows covers - gaps, index entries - is
/// retried whole by <see cref="Database.InTransactionAsync{T}"/>.</para>
///
/// <para><b>Nothing the computer sends is readable here.</b> Workspace names, event details and
/// permission requests arrive sealed. This checks that each is an envelope of a sensible size and
/// stores it as it came; only a trusted browser can open it.</para>
/// </summary>
/// <param name="clock">
/// The time every expiry and every stamp here is read from - the system's unless a test passes another.
/// Read from the system directly, expiry could be tested only by writing rows by hand, and the lifetime a
/// removal was given went untested until it lapsed.
/// </param>
public sealed class HostService(Database database, TimeProvider? clock = null, Limits? limits = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // Unlimited where none are given: a test about something else builds this without any.
    private readonly Limits _limits = limits ?? Limits.Unlimited;

    internal Task<Limits> EffectiveLimitsAsync(HostAccess host, CancellationToken ct)
        => database.InTransactionAsync(async (connection, transaction) =>
        {
            await AuthorizeAsync(connection, transaction, host, lockAccount: true);
            return await QuotaSettings.ResolveAsync(connection, transaction, host.OwnerId, _limits);
        }, ct);

    private const int MaxWorkspaces = 100;

    // A Sync carries every workspace in one hub message, and the hub refuses a message over 64 KB.
    // A workspace name is a folder's name, a few dozen characters; sealed it is about 4/3 of its
    // UTF-8 bytes plus 47. 500 characters of envelope hold a name of over 300 bytes, and 100 of them
    // with their ids still fit one message - a larger bound would let a Host build a Sync the hub
    // refuses outright, which it can only retry for ever.
    private const int MaxSealedName = 500;

    // An event's sentence: up to 16 000 bytes of text, which sealed is about 21 400 characters.
    internal const int MaxSealedDetail = 22_000;

    // A permission request carries what the card shows and the arguments the hash covers, often
    // both long. Together with a detail at its maximum and the ids around them, 40 000 still fits the
    // hub's 64 KB message, so a request the hub would accept is never refused here for its size.
    private const int MaxSealedAction = 40_000;

    // The widths of the columns these ids are stored in. Checked here so a longer one is a refusal
    // the Host can classify, not a database error it would retry.
    private const int MaxId = 100;
    private const int RunIdLength = 32;
    private const int ActionHashLength = 64;

    /// <summary>
    /// How long an owner has to answer a request. The protocol's command lifetime: the computer
    /// refuses a sealed answer issued longer ago than that, so keeping a request open longer here
    /// would only collect answers it will refuse.
    /// </summary>
    public static readonly TimeSpan Lifetime = RemoteProtocol.CommandLifetime;

    // ── Sync ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called on connection and every 15 seconds after. Publishes the Host's workspaces, marks it
    /// seen, and returns the commands it has not yet accepted.
    ///
    /// <para>The workspace list is complete each time and REPLACES what was stored. A workspace the
    /// Host no longer offers has to disappear, or the panel keeps offering to start tasks in a
    /// folder that was removed months ago.</para>
    /// </summary>
    public async Task<IReadOnlyList<HostCommand>> SyncAsync(
        HostAccess host, IReadOnlyList<WorkspaceRef> workspaces, CancellationToken ct = default)
    {
        if (workspaces.Count > MaxWorkspaces)
        {
            throw GatewayFault.MalformedEvent($"A Host may publish at most {MaxWorkspaces} workspaces.");
        }

        if (workspaces.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != workspaces.Count)
        {
            throw GatewayFault.MalformedEvent("Two workspaces share an id, so one of them cannot be named.");
        }

        foreach (var workspace in workspaces)
        {
            Required(workspace.Id, MaxId, "workspace id");
            RequireSealed(workspace.SealedName, MaxSealedName, "workspace name");
        }

        await database.InTransactionAsync(async (connection, transaction) =>
        {
            await AuthorizeAsync(connection, transaction, host);

            await connection.ExecuteAsync(transaction,
                "DELETE FROM host_workspaces WHERE owner_id = @owner AND host_id = @host",
                ("@owner", host.OwnerId), ("@host", host.HostId));

            foreach (var workspace in workspaces)
            {
                await connection.ExecuteAsync(transaction,
                    """
                    INSERT INTO host_workspaces (owner_id, host_id, workspace_id, sealed_name)
                    VALUES (@owner, @host, @id, @name)
                    """,
                    ("@owner", host.OwnerId), ("@host", host.HostId),
                    ("@id", workspace.Id), ("@name", workspace.SealedName));
            }

            await ExpireCommandsAsync(connection, transaction, host);
        }, ct);

        await using var connection = await database.OpenAsync(ct);

        // What to hand over is read AFTER the commit, by a statement of its own, so it sees what is
        // committed now. Inside the transaction it read the snapshot taken before the expiry waited
        // for a run: a second sync of this computer, waiting while the first wrote off an expired
        // start, then handed that start over as undelivered - and could as well hand over a command
        // accepted or withdrawn while it waited. No lock is taken, so this cannot wait on a person's
        // cancel either. A revocation committed since the checks above withdraws the undelivered
        // commands in its own transaction, so they are not read as undelivered here.
        //
        // And never an expired command, written off or not: one that became visible after this sync
        // looked for expired ones is still undelivered here, and the computer would refuse it as too
        // old once it had been given it.
        var pending = await connection.ReadAllAsync(null,
            """
            SELECT id, host_id, kind, payload, status, created_at, expires_at
            FROM commands
            WHERE owner_id = @owner AND host_id = @host AND status = 'PendingDelivery'
              AND expires_at > @now
            ORDER BY created_at
            """,
            reader => new HostCommand(
                reader.GetString("id"),
                reader.GetString("host_id"),
                reader.Enum<CommandKind>("kind"),
                reader.GetString("payload"),
                reader.Enum<CommandStatus>("status"),
                reader.Utc("created_at"),
                reader.Utc("expires_at")),
            ("@owner", host.OwnerId), ("@host", host.HostId), ("@now", _clock.GetUtcNow()));

        // After the commit, on its own: writing last_seen_at locks the hosts row EXCLUSIVELY, which
        // inside the transaction above, already holding it shared, would be an upgrade - see the lock
        // order in the class comment. The revoked filter keeps a revocation that committed in between
        // from being undone: it clears last_seen_at, and this must not set it again.
        await connection.ExecuteAsync(null,
            """
            UPDATE hosts SET last_seen_at = @now
            WHERE owner_id = @owner AND id = @host AND revoked = 0
            """,
            ("@now", _clock.GetUtcNow()), ("@owner", host.OwnerId), ("@host", host.HostId));

        return pending;
    }

    /// <summary>
    /// The Host has written the command down. NOT that it has carried it out - those are different
    /// moments and a crash can land between them, which is the entire reason this is a separate
    /// call rather than something Sync infers from having handed the command over.
    /// </summary>
    public Task AcknowledgeAsync(HostAccess host, string commandId, CancellationToken ct = default)
        => database.InTransactionAsync(async (connection, transaction) =>
        {
            await AuthorizeAsync(connection, transaction, host);
            await ExpireCommandsAsync(connection, transaction, host);

            // The primary key starts with the owner, so this lock cannot reach another person's
            // command. The computer is in the filter too: a command meant for another of the owner's
            // computers is not this one's to accept, and accepting it would mark it delivered where it
            // never arrived.
            //
            // The lock is taken through the primary key and the computer is filtered afterwards, so a
            // computer naming the id of one of its owner's OTHER computers' commands locks that row
            // until this refusal rolls back. That is harmless: the row is the same person's, the
            // computer could only have the id from its own owner's data, and all it costs is that other
            // computer's acknowledgement waiting a moment - no other account's row is touched or
            // revealed.
            var status = await connection.ReadOneAsync(transaction,
                """
                SELECT status FROM commands
                WHERE owner_id = @owner AND id = @id AND host_id = @host
                FOR UPDATE
                """,
                reader => (CommandStatus?)reader.Enum<CommandStatus>("status"),
                ("@owner", host.OwnerId), ("@id", commandId), ("@host", host.HostId));

            switch (status)
            {
                case null:
                    throw GatewayFault.NotFound($"Command {commandId} does not belong to this Host.");

                // Already accepted. Saying so again is not an error - the Host retries after a lost
                // reply, and refusing here would make it retry forever.
                case CommandStatus.AcceptedByHost:
                    return;

                case CommandStatus.Expired:
                    throw GatewayFault.CommandExpired(commandId);

                case CommandStatus.Rejected:
                    throw GatewayFault.Conflict($"Command {commandId} was withdrawn.");
            }

            await connection.ExecuteAsync(transaction,
                "UPDATE commands SET status = @accepted WHERE owner_id = @owner AND id = @id",
                ("@accepted", CommandStatus.AcceptedByHost), ("@owner", host.OwnerId), ("@id", commandId));
        }, ct);

    // ── Publish ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One thing the Host is telling us. The run row is locked for the whole decision, so two
    /// events arriving together are serialised by the database rather than by hope.
    /// </summary>
    public async Task PublishAsync(HostAccess host, HostEvent published, CancellationToken ct = default)
    {
        Required(published.EventId, MaxId, "event id");
        Required(published.RunId, RunIdLength, "run id");

        if (published.SealedDetail is not null)
        {
            RequireSealed(published.SealedDetail, MaxSealedDetail, "event detail");
        }

        // Shape before state: a request that is not even well-formed is refused the same way whatever
        // the run is doing, and before anything is locked for it.
        if (published.Approval is { } request)
        {
            Required(request.ApprovalId, MaxId, "approval id");
            Required(request.ToolCallId, MaxId, "tool call id");
            Required(request.ActionHash, ActionHashLength, "action hash");
            RequireSealed(request.SealedAction, MaxSealedAction, "sealed action");
        }

        await database.InTransactionAsync(async (connection, transaction) =>
        {
            await AuthorizeAsync(connection, transaction, host, lockAccount: true);

            // The run id is the Host's to name, so this lookup goes through the key that starts with the
            // owner and the computer. Through the primary key, MySQL locked the row with that id first and
            // applied the owner filter afterwards: Alice's computer naming Bob's run waited for any
            // transaction of Bob's that held it - which told it the run exists - and held up his.
            var run = await connection.ReadOneAsync(transaction,
                """
                SELECT id, owner_id, host_id, status, applied_sequence
                FROM runs FORCE INDEX (ux_runs_owner_host)
                WHERE owner_id = @owner AND host_id = @host AND id = @run
                FOR UPDATE
                """,
                reader => new RunRow(
                    reader.GetString("id"),
                    reader.GetString("owner_id"),
                    reader.GetString("host_id"),
                    reader.Enum<RemoteRunStatus>("status"),
                    reader.GetInt64("applied_sequence")),
                ("@owner", host.OwnerId), ("@host", host.HostId), ("@run", published.RunId))
                ?? throw GatewayFault.UnknownRun(published.RunId);

            // Deduplication first, and BEFORE the terminal check: a Host retrying the very event that
            // ended the run must get an acknowledgement, not "that run has ended".
            if (await connection.ExistsAsync(transaction,
                    "SELECT 1 FROM events WHERE owner_id = @owner AND host_id = @host AND id = @event",
                    ("@owner", run.OwnerId), ("@host", run.HostId), ("@event", published.EventId)))
            {
                return;
            }

            // Strictly increasing, NOT contiguous. A gap is expected and correct: an event the Host's
            // outbox dropped on a Drop-coded refusal never arrives, and demanding the next number would
            // wedge that run's queue for good.
            if (published.Sequence <= run.AppliedSequence)
            {
                throw GatewayFault.SequenceAlreadyApplied(published.Sequence, run.AppliedSequence);
            }

            if (RunLifecycle.IsTerminal(run.Status))
            {
                throw GatewayFault.RunEnded(run.Id);
            }

            var now = _clock.GetUtcNow();
            var status = await ApplyAsync(connection, transaction, run, published, now);

            // Counted, and refused once the account is full - unless it ends the run (see Quota.AdmitFromComputerAsync).
            var ending = RunLifecycle.IsTerminal(published.Kind);
            await Quota.AdmitFromComputerAsync(connection, transaction, run.OwnerId,
                Quota.SizeOf(published.SealedDetail), _limits, ending);

            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO events (owner_id, host_id, id, run_id, sequence, kind, sealed_detail, at, ordinal)
                VALUES (@owner, @host, @id, @run, @sequence, @kind, @detail, @at, @ordinal)
                """,
                ("@owner", run.OwnerId), ("@host", run.HostId), ("@id", published.EventId),
                ("@run", run.Id), ("@sequence", published.Sequence), ("@kind", published.Kind),
                ("@detail", published.SealedDetail), ("@at", now),
                // Allocated inside this transaction, which is what makes a refused publish give the
                // number back: the counter's increment rolls back with everything else. Allocating
                // outside it would leave a hole in the panel's number line for every rejection.
                ("@ordinal", await StreamCursor.NextAsync(connection, transaction, run.OwnerId)));

            // The summary is the terminal event's envelope, copied: the gateway has no key to write one
            // of its own. It opens only under that event's associated data, so its sequence is kept
            // beside it; the kind is the run's terminal status, which is named like the event's kind.
            var ended = RunLifecycle.IsTerminal(status);

            // The summary is a second copy and is counted as one; retention gives it back with the run.
            if (ended)
            {
                await Quota.AdmitFromComputerAsync(connection, transaction, run.OwnerId,
                    Quota.SizeOf(published.SealedDetail), _limits, ending: true);
            }

            await connection.ExecuteAsync(transaction,
                """
                UPDATE runs
                SET status = @status,
                    applied_sequence = @sequence,
                    ended_at = CASE WHEN @ended = 1 THEN @at ELSE ended_at END,
                    sealed_summary = CASE WHEN @ended = 1 THEN @summary ELSE sealed_summary END,
                    summary_sequence = CASE WHEN @ended = 1 THEN @sequence ELSE summary_sequence END
                WHERE owner_id = @owner AND host_id = @host AND id = @run
                """,
                ("@status", status), ("@sequence", published.Sequence), ("@ended", ended), ("@at", now),
                ("@summary", published.SealedDetail),
                ("@owner", run.OwnerId), ("@host", run.HostId), ("@run", run.Id));
        }, ct);
    }

    /// <summary>
    /// The transition itself: what this kind of event does to a run in this state, and to the
    /// approvals hanging off it. Returns the status the run is left in.
    /// </summary>
    private async Task<RemoteRunStatus> ApplyAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        RunRow run, HostEvent published, DateTimeOffset now)
    {
        if (RunLifecycle.IsTerminal(published.Kind))
        {
            // A queued run never started, so it cannot have completed. It can perfectly well have
            // been cancelled or interrupted before it began, which is why only Completed is refused.
            if (run.Status == RemoteRunStatus.Queued && published.Kind == RemoteEventKind.Completed)
            {
                throw GatewayFault.InvalidTransition(published.Kind, run.Status);
            }

            // Whatever the owner was still being asked, the answer can no longer be applied.
            await connection.ExecuteAsync(transaction,
                """
                UPDATE approvals SET status = @invalidated
                WHERE owner_id = @owner AND host_id = @host AND run_id = @run
                  AND status IN ('Pending', 'DecisionQueued')
                """,
                ("@invalidated", ApprovalStatus.Invalidated),
                ("@owner", run.OwnerId), ("@host", run.HostId), ("@run", run.Id));

            // The terminal kinds are named like the statuses they leave, and the notice says which
            // in those same words.
            await NoticeAsync(connection, transaction, run, published.Kind.ToString(), published, now);

            return RunLifecycle.StatusOf(published.Kind);
        }

        switch (published.Kind)
        {
            case RemoteEventKind.Running:
                // Only a queued run starts. A run the owner has asked to stop may still report that
                // it started - the request and the start crossed - and that is not a contradiction,
                // so the asked-to-stop state is what survives: the panel must keep saying a stop was
                // requested until the Host says it stopped.
                if (run.Status is not (RemoteRunStatus.Queued or RemoteRunStatus.CancelRequested))
                {
                    throw GatewayFault.InvalidTransition(published.Kind, run.Status);
                }

                return run.Status == RemoteRunStatus.CancelRequested
                    ? RemoteRunStatus.CancelRequested
                    : RemoteRunStatus.Running;

            // A timeline entry and nothing else. It is the only kind that does not move the run,
            // which is why it is the only one here with no rule.
            case RemoteEventKind.Progress:
                return run.Status;

            case RemoteEventKind.ApprovalRequested:
                return await RequestApprovalAsync(connection, transaction, run, published, now);

            case RemoteEventKind.ApprovalResolved:
                return await ResolveApprovalAsync(connection, transaction, run, published);

            default:
                throw GatewayFault.UnknownEventKind(published.Kind.ToString());
        }
    }

    private async Task<RemoteRunStatus> RequestApprovalAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        RunRow run, HostEvent published, DateTimeOffset now)
    {
        var request = published.Approval
            ?? throw GatewayFault.MalformedEvent("An ApprovalRequested event carries no approval.");

        if (run.Status is not (RemoteRunStatus.Running or RemoteRunStatus.WaitingForUser))
        {
            throw GatewayFault.InvalidTransition(published.Kind, run.Status);
        }

        // Approval ids are made by the computer and unique only on it.
        if (await connection.ExistsAsync(transaction,
                "SELECT 1 FROM approvals WHERE owner_id = @owner AND host_id = @host AND id = @id",
                ("@owner", run.OwnerId), ("@host", run.HostId), ("@id", request.ApprovalId)))
        {
            throw GatewayFault.Conflict($"Approval {request.ApprovalId} already exists.");
        }

        // A permission request is a report of a run in progress: refused when the account is full. The computer
        // still asks at its own screen, which is where a full account's person has to answer it.
        await Quota.AdmitFromComputerAsync(connection, transaction, run.OwnerId,
            Quota.SizeOf(request.SealedAction), _limits, ending: false);

        // What the request is about stays sealed. The tool call, the hash an answer must carry and
        // whether it may be answered from the web are in the clear because the gateway enforces them.
        await connection.ExecuteAsync(transaction,
            """
            INSERT INTO approvals (owner_id, host_id, id, run_id, tool_call_id, action_hash,
                                   remote_decidable, sealed_action, status, created_at, expires_at)
            VALUES (@owner, @host, @id, @run, @call, @hash, @decidable, @action, @pending, @now, @expires)
            """,
            ("@owner", run.OwnerId), ("@host", run.HostId), ("@id", request.ApprovalId),
            ("@run", run.Id), ("@call", request.ToolCallId), ("@hash", request.ActionHash),
            ("@decidable", request.RemoteDecidable), ("@action", request.SealedAction),
            ("@pending", ApprovalStatus.Pending), ("@now", now), ("@expires", now.Add(Lifetime)));

        await NoticeAsync(connection, transaction, run,
            request.RemoteDecidable ? NoticeKind.PermissionRequested : NoticeKind.PermissionAtComputer,
            published, now);

        return RemoteRunStatus.WaitingForUser;
    }

    private static async Task<RemoteRunStatus> ResolveApprovalAsync(
        MySqlConnection connection, MySqlTransaction transaction, RunRow run, HostEvent published)
    {
        var resolution = published.Resolution
            ?? throw GatewayFault.MalformedEvent("An ApprovalResolved event carries no resolution.");

        // The approval id is the Host's to name, so this goes through the key that starts with the
        // owner and the computer, for the same reason as the run above.
        var approval = await connection.ReadOneAsync(transaction,
            """
            SELECT status, action_hash FROM approvals FORCE INDEX (ux_approvals_owner_host)
            WHERE owner_id = @owner AND host_id = @host AND id = @id AND run_id = @run
            FOR UPDATE
            """,
            reader => (
                Status: (ApprovalStatus?)reader.Enum<ApprovalStatus>("status"),
                Hash: reader.GetString("action_hash")),
            ("@owner", run.OwnerId), ("@host", run.HostId), ("@id", resolution.ApprovalId),
            ("@run", run.Id));

        if (approval.Status is null)
        {
            throw GatewayFault.UnknownApproval(resolution.ApprovalId);
        }

        if (approval.Status is not (ApprovalStatus.Pending or ApprovalStatus.DecisionQueued))
        {
            throw GatewayFault.ApprovalAlreadyResolved(resolution.ApprovalId, approval.Status.Value);
        }

        // The outcome must be about the action this request was raised for. Compared, never
        // recomputed: the machine that will carry the action out is the one that says what it is.
        if (!string.Equals(approval.Hash, resolution.ActionHash, StringComparison.Ordinal))
        {
            throw GatewayFault.ActionHashMismatch(resolution.ApprovalId);
        }

        await connection.ExecuteAsync(transaction,
            "UPDATE approvals SET status = @status WHERE owner_id = @owner AND host_id = @host AND id = @id",
            ("@status", RunLifecycle.StatusOf(resolution.Outcome)),
            ("@owner", run.OwnerId), ("@host", run.HostId), ("@id", resolution.ApprovalId));

        // A stop that was asked for outlives an answered permission.
        if (run.Status == RemoteRunStatus.CancelRequested)
        {
            return RemoteRunStatus.CancelRequested;
        }

        var stillWaiting = await connection.ExistsAsync(transaction,
            """
            SELECT 1 FROM approvals
            WHERE owner_id = @owner AND host_id = @host AND run_id = @run
              AND status IN ('Pending', 'DecisionQueued')
            """,
            ("@owner", run.OwnerId), ("@host", run.HostId), ("@run", run.Id));

        return stillWaiting ? RemoteRunStatus.WaitingForUser : RemoteRunStatus.Running;
    }

    // ── shared ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The account is active and the computer is live, read inside this call's transaction and held
    /// until it commits. Checked on every call and not only when the connection was made: a
    /// credential revoked, or an account disabled, while a Host was connected has to stop working at
    /// the next thing it does, not at the next reconnect.
    ///
    /// <para>Locking reads, shared. Read without a lock, a call already in flight saw the computer
    /// live in its snapshot and applied an event after the revocation had committed. Shared, so calls
    /// of one computer do not wait on each other, and so a person's command can still read the
    /// computer while this holds it (see the lock order in the class comment).</para>
    ///
    /// <para>The account's row is locked for update instead when the call adds to the account's byte
    /// total (<paramref name="lockAccount"/>): that total is a column of this row, and two publishes each
    /// holding it shared and then wanting to write it deadlock rather than queue.</para>
    /// </summary>
    private static async Task AuthorizeAsync(
        MySqlConnection connection, MySqlTransaction transaction, HostAccess host, bool lockAccount = false)
    {
        var status = await connection.ReadOneAsync(transaction,
            lockAccount
                ? "SELECT status FROM users WHERE id = @owner FOR UPDATE"
                : "SELECT status FROM users WHERE id = @owner FOR SHARE",
            reader => reader.GetString("status"), ("@owner", host.OwnerId));

        // Not there at all is a computer whose account is gone - its row went with it - so it is the
        // unknown-computer refusal below, not a disabled account.
        if (status is not null && status != "Active")
        {
            throw GatewayFault.AccountDisabled();
        }

        // The owner leads the key, so this cannot lock another person's computer even if the access
        // named one; and a computer under another owner than its token's is simply not found.
        var revoked = await connection.ReadOneAsync(transaction,
            """
            SELECT revoked FROM hosts FORCE INDEX (ux_hosts_owner)
            WHERE owner_id = @owner AND id = @host
            FOR SHARE
            """,
            reader => (bool?)reader.GetBoolean("revoked"),
            ("@owner", host.OwnerId), ("@host", host.HostId));

        if (revoked is null)
        {
            throw GatewayFault.UnknownHost();
        }

        if (revoked.Value)
        {
            throw GatewayFault.HostRevoked();
        }
    }

    /// <summary>
    /// Commands nobody accepted in time. A start that expired undelivered leaves a run that was
    /// never going to happen, and it is reported Incomplete rather than left Queued forever - an
    /// absence is not an answer.
    ///
    /// <para>A removal or an endorsement that expired undelivered is said too, as a notice naming the
    /// computer. Written off in silence, the panel's "told" stood while the computer went on trusting the
    /// removed browser, and nobody had a reason to remove it again.</para>
    ///
    /// <para><b>Runs before commands.</b> The expired commands are found WITHOUT a lock; the runs of
    /// the expired starts are locked next, in id order; and only then is each command locked, by its
    /// own primary key, and written off if it is still undelivered. Finding them with a locking scan
    /// first held this computer's whole delivery range, gaps included, while waiting for a run - and a
    /// person's cancel of that run holds the run and then inserts its command into that very range.
    /// Each waited for the other, and the database rolled one of them back.</para>
    /// </summary>
    private async Task ExpireCommandsAsync(
        MySqlConnection connection, MySqlTransaction transaction, HostAccess host)
    {
        var now = _clock.GetUtcNow();

        var expiring = await connection.ReadAllAsync(transaction,
            """
            SELECT id, kind, payload FROM commands
            WHERE owner_id = @owner AND host_id = @host AND status = 'PendingDelivery'
              AND expires_at <= @now
            ORDER BY id
            """,
            reader => (
                Id: reader.GetString("id"),
                Kind: reader.Enum<CommandKind>("kind"),
                Payload: reader.GetString("payload")),
            ("@owner", host.OwnerId), ("@host", host.HostId), ("@now", now));

        // The run id is the gateway's own, written into the payload when the person started it.
        var starts = expiring
            .Where(command => command.Kind == CommandKind.StartTask)
            .ToDictionary(
                command => command.Id,
                command => RemoteJson.Deserialize<StartTaskPayload>(command.Payload).RunId,
                StringComparer.Ordinal);

        // In id order, so two transactions locking several of these runs take them in the same order.
        foreach (var runId in starts.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            await connection.ExecuteAsync(transaction,
                """
                SELECT id FROM runs FORCE INDEX (ux_runs_owner_host)
                WHERE owner_id = @owner AND host_id = @host AND id = @run
                FOR UPDATE
                """,
                ("@owner", host.OwnerId), ("@host", host.HostId), ("@run", runId));
        }

        foreach (var command in expiring)
        {
            // Re-checked under the lock: what the unlocked read saw may have been accepted, withdrawn
            // or written off by another call since. Only the call that writes it off reports it.
            var expired = await connection.ExecuteAsync(transaction,
                """
                UPDATE commands SET status = @expired
                WHERE owner_id = @owner AND id = @id AND status = 'PendingDelivery'
                """,
                ("@expired", CommandStatus.Expired), ("@owner", host.OwnerId), ("@id", command.Id));

            if (expired == 0)
            {
                continue;
            }

            if (command.Kind is CommandKind.RevokeDevice or CommandKind.EndorseDevice)
            {
                // No detail, as for a start that never started: the gateway has no key to seal a sentence, and
                // it does not know which device the command named - that is inside the seal. The kind and the
                // computer are what the panel says it with.
                await connection.ExecuteAsync(transaction,
                    """
                    INSERT INTO notices (id, owner_id, host_id, kind, at, is_read, ordinal)
                    VALUES (@id, @owner, @host, @kind, @at, 0, @ordinal)
                    """,
                    ("@id", Ids.New()), ("@owner", host.OwnerId), ("@host", host.HostId),
                    ("@kind", command.Kind == CommandKind.RevokeDevice
                        ? NoticeKind.RemovalNotDelivered
                        : NoticeKind.EndorsementNotDelivered),
                    ("@at", now),
                    ("@ordinal", await StreamCursor.NextAsync(connection, transaction, host.OwnerId)));
                continue;
            }

            if (!starts.TryGetValue(command.Id, out var runId))
            {
                continue;
            }

            var affected = await connection.ExecuteAsync(transaction,
                """
                UPDATE runs SET status = @incomplete, ended_at = @now
                WHERE owner_id = @owner AND host_id = @host AND id = @run AND status = 'Queued'
                """,
                ("@incomplete", RemoteRunStatus.Incomplete), ("@now", now),
                ("@owner", host.OwnerId), ("@host", host.HostId), ("@run", runId));

            if (affected > 0)
            {
                // No detail and no summary: the computer never saw this run, so there is no event
                // to copy, and the gateway has no key to seal a sentence of its own. The panel says
                // what NotStarted means.
                await connection.ExecuteAsync(transaction,
                    """
                    INSERT INTO notices (id, owner_id, run_id, kind, at, is_read, ordinal)
                    VALUES (@id, @owner, @run, @kind, @at, 0, @ordinal)
                    """,
                    ("@id", Ids.New()), ("@owner", host.OwnerId), ("@run", runId),
                    ("@kind", NoticeKind.NotStarted), ("@at", now),
                    ("@ordinal", await StreamCursor.NextAsync(connection, transaction, host.OwnerId)));
            }
        }
    }

    /// <summary>
    /// A notice raised by an event. Its kind is the gateway's own word; its detail is the event's
    /// envelope copied as it came, with the event's sequence and kind, which are what the panel
    /// rebuilds the associated data from - without them the copy could never be opened.
    /// </summary>
    private async Task NoticeAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        RunRow run, string kind, HostEvent published, DateTimeOffset at)
    {
        // A copy is stored bytes like the original, and retention gives it back when it deletes the notice.
        // It is admitted as the event it copies is: out of the reserve when that event ends the run.
        await Quota.AdmitFromComputerAsync(connection, transaction, run.OwnerId,
            Quota.SizeOf(published.SealedDetail), _limits, RunLifecycle.IsTerminal(published.Kind));

        await connection.ExecuteAsync(transaction,
            """
            INSERT INTO notices (id, owner_id, run_id, kind, sealed_detail, event_sequence, event_kind,
                                 at, is_read, ordinal)
            VALUES (@id, @owner, @run, @kind, @detail, @sequence, @eventKind, @at, 0, @ordinal)
            """,
            ("@id", Ids.New()), ("@owner", run.OwnerId), ("@run", run.Id), ("@kind", kind),
            ("@detail", published.SealedDetail), ("@sequence", published.Sequence),
            ("@eventKind", published.Kind), ("@at", at),
            ("@ordinal", await StreamCursor.NextAsync(connection, transaction, run.OwnerId)));
    }

    private static void Required(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
        {
            throw GatewayFault.MalformedEvent($"'{field}' must be 1 to {max:N0} characters.");
        }
    }

    private static void RequireSealed(string? value, int maxChars, string field)
    {
        if (!Envelope.LooksSealed(value, maxChars))
        {
            throw GatewayFault.EnvelopeMalformed(field, maxChars);
        }
    }

    /// <summary>
    /// What a notice is about, in the gateway's words - the only part of a notice it can write,
    /// since it seals nothing. The terminal kinds are the run statuses' own names.
    /// </summary>
    private static class NoticeKind
    {
        public const string PermissionRequested = "PermissionRequested";
        public const string PermissionAtComputer = "PermissionAtComputer";
        public const string NotStarted = "NotStarted";
        public const string RemovalNotDelivered = "RemovalNotDelivered";
        public const string EndorsementNotDelivered = "EndorsementNotDelivered";
    }
}
