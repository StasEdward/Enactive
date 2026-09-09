namespace Enactive.Remote.Gateway.Services;

using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>
/// Everything the owner is allowed to do.
///
/// <para>Nothing here executes anything. Every action that reaches the computer becomes a COMMAND
/// in a queue the Host drains, and the Host is free to refuse it when it arrives - it may already
/// have been answered on the desktop, or expired, or the run may be gone. That asymmetry is the
/// design: the owner asks, the Host decides.</para>
/// </summary>
public sealed class OwnerService(Database database)
{
    /// <summary>Registers a computer and returns its credential ONCE. Nothing stores the token.</summary>
    public async Task<(string Id, string Name, string Token)> RegisterAsync(
        string? name, CancellationToken ct = default)
    {
        var deviceName = Required(name, 80, "name");
        var id = Ids.New();
        var token = Ids.NewToken();

        await using var connection = await database.OpenAsync(ct);
        await connection.ExecuteAsync(null,
            """
            INSERT INTO hosts (id, name, token_hash, revoked, created_at)
            VALUES (@id, @name, @hash, 0, @now)
            """,
            ("@id", id), ("@name", deviceName), ("@hash", Ids.Hash(token)),
            ("@now", DateTimeOffset.UtcNow));

        return (id, deviceName, token);
    }

    /// <summary>
    /// Withdraws a credential. Commands nobody has accepted yet are withdrawn with it; commands the
    /// Host already ACCEPTED are left alone, because it owns them now and may well be carrying one
    /// out - pretending otherwise would make the panel claim a stop that never happened.
    /// </summary>
    public async Task RevokeAsync(string hostId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        var exists = await connection.ExistsAsync(transaction,
            "SELECT 1 FROM hosts WHERE id = @host FOR UPDATE", ("@host", hostId));

        if (!exists)
        {
            throw GatewayFault.NotFound("That computer is not registered.");
        }

        await connection.ExecuteAsync(transaction,
            "UPDATE hosts SET revoked = 1, last_seen_at = NULL WHERE id = @host", ("@host", hostId));

        await connection.ExecuteAsync(transaction,
            """
            UPDATE commands SET status = @rejected
            WHERE host_id = @host AND status = 'PendingDelivery'
            """,
            ("@rejected", CommandStatus.Rejected), ("@host", hostId));

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Writes a task down without starting it. The workspace has to be one the Host has actually
    /// published: the owner names a workspace, never a folder, and this is where that is enforced.
    /// </summary>
    public async Task<string> CreateTaskAsync(
        string hostId, string workspaceId, string? title, string? prompt, CancellationToken ct = default)
    {
        var id = Ids.New();

        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        await EnsureLiveHostAsync(connection, transaction, hostId);

        var known = await connection.ExistsAsync(transaction,
            "SELECT 1 FROM host_workspaces WHERE host_id = @host AND workspace_id = @workspace",
            ("@host", hostId), ("@workspace", workspaceId));

        if (!known)
        {
            throw GatewayFault.BadRequest(
                "Choose a workspace this computer has published. It publishes them when it connects.");
        }

        await connection.ExecuteAsync(transaction,
            """
            INSERT INTO tasks (id, host_id, workspace_id, title, prompt, created_at)
            VALUES (@id, @host, @workspace, @title, @prompt, @now)
            """,
            ("@id", id), ("@host", hostId), ("@workspace", workspaceId),
            ("@title", Required(title, 140, "title")),
            ("@prompt", Required(prompt, 16_000, "prompt")),
            ("@now", DateTimeOffset.UtcNow));

        await transaction.CommitAsync(ct);
        return id;
    }

    /// <summary>Queues a run of an existing task.</summary>
    public async Task<HostCommand> StartAsync(
        string taskId, string commandId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        var fingerprint = Ids.Fingerprint($"StartTask:{taskId}");

        if (await ExistingCommandAsync(connection, transaction, commandId, fingerprint) is { } repeated)
        {
            await transaction.CommitAsync(ct);
            return repeated;
        }

        var task = await connection.ReadOneAsync(transaction,
            "SELECT id, host_id, workspace_id, title, prompt, created_at FROM tasks WHERE id = @id",
            reader => new TaskRow(
                reader.GetString("id"), reader.GetString("host_id"), reader.GetString("workspace_id"),
                reader.GetString("title"), reader.GetString("prompt"), reader.Utc("created_at")),
            ("@id", taskId))
            ?? throw GatewayFault.NotFound("That task no longer exists.");

        await EnsureLiveHostAsync(connection, transaction, task.HostId);

        // One at a time. Two runs of one task would race each other over the same files, and the
        // panel would have no way to say which timeline belonged to which.
        var active = await connection.ExistsAsync(transaction,
            """
            SELECT 1 FROM runs
            WHERE task_id = @task
              AND status NOT IN ('Completed', 'Failed', 'Incomplete', 'Cancelled', 'Interrupted')
            FOR UPDATE
            """,
            ("@task", taskId));

        if (active)
        {
            throw GatewayFault.Conflict("This task is already running. Wait for it, or stop it first.");
        }

        var runId = Ids.New();
        var now = DateTimeOffset.UtcNow;

        await connection.ExecuteAsync(transaction,
            """
            INSERT INTO runs (id, task_id, host_id, status, applied_sequence, created_at)
            VALUES (@id, @task, @host, @queued, 0, @now)
            """,
            ("@id", runId), ("@task", task.Id), ("@host", task.HostId),
            ("@queued", RemoteRunStatus.Queued), ("@now", now));

        var payload = RemoteJson.Serialize(new StartTaskPayload(
            runId, task.Id, task.WorkspaceId, task.Title, task.Prompt, task.CreatedAt));

        var command = await QueueAsync(
            connection, transaction, commandId, task.HostId, CommandKind.StartTask, payload, fingerprint, now);

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
        string runId, string commandId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        var fingerprint = Ids.Fingerprint($"CancelRun:{runId}");

        if (await ExistingCommandAsync(connection, transaction, commandId, fingerprint) is { } repeated)
        {
            await transaction.CommitAsync(ct);
            return repeated;
        }

        var run = await connection.ReadOneAsync(transaction,
            "SELECT id, host_id, status FROM runs WHERE id = @run FOR UPDATE",
            reader => (
                Id: reader.GetString("id"),
                HostId: reader.GetString("host_id"),
                Status: (RemoteRunStatus?)reader.Enum<RemoteRunStatus>("status")),
            ("@run", runId));

        if (run.Status is null)
        {
            throw GatewayFault.NotFound("That run no longer exists.");
        }

        await EnsureLiveHostAsync(connection, transaction, run.HostId);

        if (RunLifecycle.IsTerminal(run.Status.Value))
        {
            throw GatewayFault.Conflict("That run has already ended.");
        }

        await connection.ExecuteAsync(transaction,
            "UPDATE runs SET status = @requested WHERE id = @run",
            ("@requested", RemoteRunStatus.CancelRequested), ("@run", runId));

        var command = await QueueAsync(
            connection, transaction, commandId, run.HostId, CommandKind.CancelRun,
            RemoteJson.Serialize(new CancelRunPayload(runId)), fingerprint, DateTimeOffset.UtcNow);

        await transaction.CommitAsync(ct);
        return command;
    }

    /// <summary>
    /// The owner's answer to a permission request. It moves the request to
    /// <see cref="ApprovalStatus.DecisionQueued"/> and NOT to Allowed: an answer is a command that
    /// still has to reach the Host and may lose there to a decision already taken on the desktop.
    ///
    /// <para>A request the Host marked as not remotely decidable - a shell - is refused here. That
    /// refusal is the boundary; whether the panel drew a button is a detail of the panel.</para>
    /// </summary>
    public async Task<HostCommand> DecideAsync(
        string approvalId, string commandId, RemoteDecision decision, string actionHash,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);

        var fingerprint = Ids.Fingerprint($"ResolveApproval:{approvalId}:{decision}:{actionHash}");

        if (await ExistingCommandAsync(connection, transaction, commandId, fingerprint) is { } repeated)
        {
            await transaction.CommitAsync(ct);
            return repeated;
        }

        var approval = await connection.ReadOneAsync(transaction,
            """
            SELECT id, host_id, run_id, tool_call_id, action_hash, remote_decidable, status, expires_at
            FROM approvals WHERE id = @id
            FOR UPDATE
            """,
            reader => (
                HostId: reader.GetString("host_id"),
                RunId: reader.GetString("run_id"),
                ToolCallId: reader.GetString("tool_call_id"),
                ActionHash: reader.GetString("action_hash"),
                RemoteDecidable: reader.GetBoolean("remote_decidable"),
                Status: (ApprovalStatus?)reader.Enum<ApprovalStatus>("status"),
                ExpiresAt: reader.Utc("expires_at")),
            ("@id", approvalId));

        if (approval.Status is null)
        {
            throw GatewayFault.UnknownApproval(approvalId);
        }

        if (!approval.RemoteDecidable)
        {
            throw GatewayFault.ApprovalNotRemotelyDecidable(approvalId);
        }

        await EnsureLiveHostAsync(connection, transaction, approval.HostId);

        if (approval.Status != ApprovalStatus.Pending)
        {
            throw GatewayFault.ApprovalAlreadyResolved(approvalId, approval.Status.Value);
        }

        if (approval.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw GatewayFault.Conflict("That request has expired. The computer will report how it ended.");
        }

        if (!string.Equals(approval.ActionHash, actionHash, StringComparison.Ordinal))
        {
            throw GatewayFault.ActionHashMismatch(approvalId);
        }

        var runStatus = await connection.ReadOneAsync(transaction,
            "SELECT status FROM runs WHERE id = @run",
            reader => (RemoteRunStatus?)reader.Enum<RemoteRunStatus>("status"),
            ("@run", approval.RunId));

        if (runStatus is null || RunLifecycle.IsTerminal(runStatus.Value))
        {
            throw GatewayFault.Conflict("That run has ended, so this permission no longer applies.");
        }

        await connection.ExecuteAsync(transaction,
            "UPDATE approvals SET status = @queued, requested_decision = @decision WHERE id = @id",
            ("@queued", ApprovalStatus.DecisionQueued), ("@decision", decision), ("@id", approvalId));

        var payload = RemoteJson.Serialize(new ResolveApprovalPayload(
            approvalId, approval.RunId, approval.ToolCallId, approval.ActionHash, decision));

        var command = await QueueAsync(
            connection, transaction, commandId, approval.HostId, CommandKind.ResolveApproval,
            payload, fingerprint, DateTimeOffset.UtcNow);

        await transaction.CommitAsync(ct);
        return command;
    }

    public async Task MarkNoticesReadAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await connection.ExecuteAsync(null, "UPDATE notices SET is_read = 1 WHERE is_read = 0");
    }

    // ── shared ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A CommandId that has been used before.
    ///
    /// <para>Same id and same action: this is a retry, and the caller gets the command it already
    /// queued. Same id, different action: a conflict, loudly - silently returning the first one's
    /// result would mean an action the owner asked for never happened and nothing said so.</para>
    /// </summary>
    private static async Task<HostCommand?> ExistingCommandAsync(
        MySqlConnection connection, MySqlTransaction transaction, string commandId, string fingerprint)
    {
        if (!Guid.TryParse(commandId, out _))
        {
            throw GatewayFault.BadRequest("CommandId must be a UUID.");
        }

        var previous = await connection.ReadOneAsync(transaction,
            """
            SELECT id, host_id, kind, payload, fingerprint, status, created_at, expires_at
            FROM commands WHERE id = @id
            FOR UPDATE
            """,
            reader => (
                Command: new HostCommand(
                    reader.GetString("id"), reader.GetString("host_id"),
                    reader.Enum<CommandKind>("kind"), reader.GetString("payload"),
                    reader.Enum<CommandStatus>("status"),
                    reader.Utc("created_at"), reader.Utc("expires_at")),
                Fingerprint: reader.GetString("fingerprint")),
            ("@id", commandId));

        if (previous.Command is null)
        {
            return null;
        }

        return string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal)
            ? previous.Command
            : throw GatewayFault.Conflict("That request id was already used for a different action.");
    }

    private static async Task<HostCommand> QueueAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        string commandId, string hostId, CommandKind kind, string payload, string fingerprint,
        DateTimeOffset now)
    {
        var expires = now.Add(HostService.Lifetime);

        await connection.ExecuteAsync(transaction,
            """
            INSERT INTO commands (id, host_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES (@id, @host, @kind, @payload, @fingerprint, @pending, @now, @expires)
            """,
            ("@id", commandId), ("@host", hostId), ("@kind", kind), ("@payload", payload),
            ("@fingerprint", fingerprint), ("@pending", CommandStatus.PendingDelivery),
            ("@now", now), ("@expires", expires));

        return new HostCommand(
            commandId, hostId, kind, payload, CommandStatus.PendingDelivery, now, expires);
    }

    private static async Task EnsureLiveHostAsync(
        MySqlConnection connection, MySqlTransaction transaction, string hostId)
    {
        var revoked = await connection.ReadOneAsync(transaction,
            "SELECT revoked FROM hosts WHERE id = @host",
            reader => (bool?)reader.GetBoolean("revoked"), ("@host", hostId));

        if (revoked is null || revoked.Value)
        {
            throw GatewayFault.NotFound("That computer is not available.");
        }
    }

    private static string Required(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
        {
            throw GatewayFault.BadRequest($"'{field}' must be 1 to {max:N0} characters.");
        }

        return value.Trim();
    }
}
