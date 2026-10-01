namespace Enactive.Remote.Gateway.Services;

using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>
/// Everything a Host is allowed to do. Three methods, and the third is the whole state machine.
///
/// <para>The Host is authoritative about what happened on the machine; this class maintains a
/// PROJECTION of that and never a second opinion. So its job is narrow and unglamorous: refuse what
/// cannot be true, apply what can, and say precisely why when it refuses - because a Host with a
/// durable outbox has to decide from that answer whether to keep the item or drop it.</para>
/// </summary>
public sealed class HostService(Database database)
{
    private const int MaxWorkspaces = 100;
    private const int MaxDetail = 16_000;
    private const int MaxArguments = 24_000;

    /// <summary>How long the Host has to accept a command, and an owner to answer a request.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

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
        string hostId, IReadOnlyList<WorkspaceRef> workspaces, CancellationToken ct = default)
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
            Required(workspace.Id, 100, "workspace id");
            Required(workspace.SealedName, 100, "workspace name"); // Task 3.4 rewrites this
        }

        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        await EnsureHostAsync(connection, transaction, hostId);

        await connection.ExecuteAsync(transaction,
            "UPDATE hosts SET last_seen_at = @now WHERE id = @host",
            ("@now", DateTimeOffset.UtcNow), ("@host", hostId));

        await connection.ExecuteAsync(transaction,
            "DELETE FROM host_workspaces WHERE host_id = @host", ("@host", hostId));

        foreach (var workspace in workspaces)
        {
            await connection.ExecuteAsync(transaction,
                """
                INSERT INTO host_workspaces (host_id, workspace_id, name)
                VALUES (@host, @id, @name)
                """,
                ("@host", hostId), ("@id", workspace.Id), ("@name", workspace.SealedName));
        }

        await ExpireCommandsAsync(connection, transaction, hostId);

        var pending = await connection.ReadAllAsync(transaction,
            """
            SELECT id, host_id, kind, payload, status, created_at, expires_at
            FROM commands
            WHERE host_id = @host AND status = 'PendingDelivery'
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
            ("@host", hostId));

        await transaction.CommitAsync(ct);
        return pending;
    }

    /// <summary>
    /// The Host has written the command down. NOT that it has carried it out - those are different
    /// moments and a crash can land between them, which is the entire reason this is a separate
    /// call rather than something Sync infers from having handed the command over.
    /// </summary>
    public async Task AcknowledgeAsync(string hostId, string commandId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        await EnsureHostAsync(connection, transaction, hostId);
        await ExpireCommandsAsync(connection, transaction, hostId);

        var status = await connection.ReadOneAsync(transaction,
            "SELECT status FROM commands WHERE id = @id AND host_id = @host FOR UPDATE",
            reader => (CommandStatus?)reader.Enum<CommandStatus>("status"),
            ("@id", commandId), ("@host", hostId));

        switch (status)
        {
            case null:
                throw GatewayFault.NotFound($"Command {commandId} does not belong to this Host.");

            // Already accepted. Saying so again is not an error - the Host retries after a lost
            // reply, and refusing here would make it retry forever.
            case CommandStatus.AcceptedByHost:
                await transaction.CommitAsync(ct);
                return;

            case CommandStatus.Expired:
                throw GatewayFault.CommandExpired(commandId);

            case CommandStatus.Rejected:
                throw GatewayFault.Conflict($"Command {commandId} was withdrawn.");
        }

        await connection.ExecuteAsync(transaction,
            "UPDATE commands SET status = @accepted WHERE id = @id",
            ("@accepted", CommandStatus.AcceptedByHost), ("@id", commandId));

        await transaction.CommitAsync(ct);
    }

    // ── Publish ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One thing the Host is telling us. The run row is locked for the whole decision, so two
    /// events arriving together are serialised by the database rather than by hope.
    /// </summary>
    public async Task PublishAsync(string hostId, HostEvent published, CancellationToken ct = default)
    {
        Required(published.EventId, 100, "event id");
        Required(published.RunId, 32, "run id");

        if ((published.SealedDetail?.Length ?? 0) > MaxDetail)
        {
            throw GatewayFault.MalformedEvent($"Event detail is longer than {MaxDetail:N0} characters.");
        }

        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        await EnsureHostAsync(connection, transaction, hostId);

        var run = await connection.ReadOneAsync(transaction,
            """
            SELECT id, task_id, host_id, status, applied_sequence, created_at, ended_at, summary
            FROM runs WHERE id = @run AND host_id = @host
            FOR UPDATE
            """,
            ReadRun, ("@run", published.RunId), ("@host", hostId))
            ?? throw GatewayFault.UnknownRun(published.RunId);

        // Deduplication first, and BEFORE the terminal check: a Host retrying the very event that
        // ended the run must get an acknowledgement, not "that run has ended".
        if (await connection.ExistsAsync(transaction,
                "SELECT 1 FROM events WHERE host_id = @host AND id = @event",
                ("@host", hostId), ("@event", published.EventId)))
        {
            await transaction.CommitAsync(ct);
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

        var now = DateTimeOffset.UtcNow;
        var status = await ApplyAsync(connection, transaction, run, published, now);

        await connection.ExecuteAsync(transaction,
            """
            INSERT INTO events (id, host_id, run_id, sequence, kind, detail, at, ordinal)
            VALUES (@id, @host, @run, @sequence, @kind, @detail, @at, @ordinal)
            """,
            ("@id", published.EventId), ("@host", hostId), ("@run", run.Id),
            ("@sequence", published.Sequence), ("@kind", published.Kind),
            ("@detail", published.SealedDetail), ("@at", now),
            // Allocated inside this transaction, which is what makes a refused publish give the
            // number back: the counter's increment rolls back with everything else. Allocating
            // outside it would leave a hole in the panel's number line for every rejection.
            ("@ordinal", await StreamCursor.NextAsync(connection, transaction)));

        await connection.ExecuteAsync(transaction,
            """
            UPDATE runs
            SET status = @status,
                applied_sequence = @sequence,
                ended_at = CASE WHEN @ended = 1 THEN @at ELSE ended_at END,
                summary = CASE WHEN @ended = 1 THEN @summary ELSE summary END
            WHERE id = @run
            """,
            ("@status", status), ("@sequence", published.Sequence),
            ("@ended", RunLifecycle.IsTerminal(status)), ("@at", now),
            ("@summary", published.SealedDetail), ("@run", run.Id));

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// The transition itself: what this kind of event does to a run in this state, and to the
    /// approvals hanging off it. Returns the status the run is left in.
    /// </summary>
    private static async Task<RemoteRunStatus> ApplyAsync(
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
                WHERE run_id = @run AND status IN ('Pending', 'DecisionQueued')
                """,
                ("@invalidated", ApprovalStatus.Invalidated), ("@run", run.Id));

            await NoticeAsync(connection, transaction, run.Id,
                published.Kind.ToString(), published.SealedDetail ?? "", now);

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

    private static async Task<RemoteRunStatus> RequestApprovalAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        RunRow run, HostEvent published, DateTimeOffset now)
    {
        var request = published.Approval
            ?? throw GatewayFault.MalformedEvent("An ApprovalRequested event carries no approval.");

        if (run.Status is not (RemoteRunStatus.Running or RemoteRunStatus.WaitingForUser))
        {
            throw GatewayFault.InvalidTransition(published.Kind, run.Status);
        }

        Required(request.ApprovalId, 100, "approval id");
        Required(request.ToolCallId, 100, "tool call id");
        Required(request.SealedAction, MaxArguments, "sealed action"); // Task 3.4 rewrites this
        Required(request.ActionHash, 128, "action hash");

        if (await connection.ExistsAsync(transaction,
                "SELECT 1 FROM approvals WHERE id = @id", ("@id", request.ApprovalId)))
        {
            throw GatewayFault.Conflict($"Approval {request.ApprovalId} already exists.");
        }

        await connection.ExecuteAsync(transaction,
            """
            INSERT INTO approvals (id, host_id, run_id, tool_call_id, tool, arguments,
                                   working_directory, reason, action_hash, remote_decidable,
                                   status, created_at, expires_at)
            VALUES (@id, @host, @run, @call, @tool, @arguments, @directory, @reason, @hash,
                    @decidable, @pending, @now, @expires)
            """,
            ("@id", request.ApprovalId), ("@host", run.HostId), ("@run", run.Id),
            ("@call", request.ToolCallId), ("@tool", ""),
            // Task 3.4 rewrites this: the sealed action is stored opaquely in the old plaintext column.
            ("@arguments", request.SealedAction), ("@directory", ""),
            ("@reason", published.SealedDetail ?? ""), ("@hash", request.ActionHash),
            ("@decidable", request.RemoteDecidable), ("@pending", ApprovalStatus.Pending),
            ("@now", now), ("@expires", now.Add(Lifetime)));

        await NoticeAsync(connection, transaction, run.Id,
            request.RemoteDecidable ? "Permission required" : "Permission required on the computer",
            published.SealedDetail ?? "", now);

        return RemoteRunStatus.WaitingForUser;
    }

    private static async Task<RemoteRunStatus> ResolveApprovalAsync(
        MySqlConnection connection, MySqlTransaction transaction, RunRow run, HostEvent published)
    {
        var resolution = published.Resolution
            ?? throw GatewayFault.MalformedEvent("An ApprovalResolved event carries no resolution.");

        var approval = await connection.ReadOneAsync(transaction,
            """
            SELECT id, status, action_hash FROM approvals
            WHERE id = @id AND run_id = @run AND host_id = @host
            FOR UPDATE
            """,
            reader => (
                Status: (ApprovalStatus?)reader.Enum<ApprovalStatus>("status"),
                Hash: reader.GetString("action_hash")),
            ("@id", resolution.ApprovalId), ("@run", run.Id), ("@host", run.HostId));

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
            "UPDATE approvals SET status = @status WHERE id = @id",
            ("@status", RunLifecycle.StatusOf(resolution.Outcome)), ("@id", resolution.ApprovalId));

        // A stop that was asked for outlives an answered permission.
        if (run.Status == RemoteRunStatus.CancelRequested)
        {
            return RemoteRunStatus.CancelRequested;
        }

        var stillWaiting = await connection.ExistsAsync(transaction,
            """
            SELECT 1 FROM approvals
            WHERE run_id = @run AND status IN ('Pending', 'DecisionQueued')
            """,
            ("@run", run.Id));

        return stillWaiting ? RemoteRunStatus.WaitingForUser : RemoteRunStatus.Running;
    }

    // ── shared ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Checked on every call and not only when the connection was made. A credential revoked while
    /// a Host was connected has to stop working at the next thing it does, not at the next
    /// reconnect.
    /// </summary>
    private static async Task EnsureHostAsync(
        MySqlConnection connection, MySqlTransaction transaction, string hostId)
    {
        var revoked = await connection.ReadOneAsync(transaction,
            "SELECT revoked FROM hosts WHERE id = @host",
            reader => (bool?)reader.GetBoolean("revoked"), ("@host", hostId));

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
    /// </summary>
    private static async Task ExpireCommandsAsync(
        MySqlConnection connection, MySqlTransaction transaction, string hostId)
    {
        var now = DateTimeOffset.UtcNow;

        var expiring = await connection.ReadAllAsync(transaction,
            """
            SELECT id, kind, payload FROM commands
            WHERE host_id = @host AND status = 'PendingDelivery' AND expires_at <= @now
            FOR UPDATE
            """,
            reader => (
                Id: reader.GetString("id"),
                Kind: reader.Enum<CommandKind>("kind"),
                Payload: reader.GetString("payload")),
            ("@host", hostId), ("@now", now));

        foreach (var command in expiring)
        {
            await connection.ExecuteAsync(transaction,
                "UPDATE commands SET status = @expired WHERE id = @id",
                ("@expired", CommandStatus.Expired), ("@id", command.Id));

            if (command.Kind != CommandKind.StartTask)
            {
                continue;
            }

            var runId = RemoteJson.Deserialize<StartTaskPayload>(command.Payload).RunId;
            const string reason = "The start command expired before the computer accepted it.";

            var affected = await connection.ExecuteAsync(transaction,
                """
                UPDATE runs SET status = @incomplete, ended_at = @now, summary = @reason
                WHERE id = @run AND status = 'Queued'
                """,
                ("@incomplete", RemoteRunStatus.Incomplete), ("@now", now),
                ("@reason", reason), ("@run", runId));

            if (affected > 0)
            {
                await NoticeAsync(connection, transaction, runId, "Incomplete", reason, now);
            }
        }
    }

    private static async Task NoticeAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        string runId, string title, string detail, DateTimeOffset at)
        => await connection.ExecuteAsync(transaction,
            """
            INSERT INTO notices (id, run_id, title, detail, at, is_read, ordinal)
            VALUES (@id, @run, @title, @detail, @at, 0, @ordinal)
            """,
            ("@id", Guid.NewGuid().ToString("N")), ("@run", runId),
            ("@title", title), ("@detail", detail), ("@at", at),
            ("@ordinal", await StreamCursor.NextAsync(connection, transaction)));

    private static RunRow ReadRun(MySqlDataReader reader) => new(
        reader.GetString("id"),
        reader.GetString("task_id"),
        reader.GetString("host_id"),
        reader.Enum<RemoteRunStatus>("status"),
        reader.GetInt64("applied_sequence"),
        reader.Utc("created_at"),
        reader.UtcOrNull("ended_at"),
        reader.StringOrNull("summary"));

    private static void Required(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
        {
            throw GatewayFault.MalformedEvent($"'{field}' must be 1 to {max:N0} characters.");
        }
    }
}
