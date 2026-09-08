namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Xunit;

/// <summary>
/// What the owner is allowed to ask for. Stage 2 of <c>Docs/REMOTE_DESIGN.md</c>.
///
/// <para>Nothing here executes anything: every action becomes a command in a queue the Host drains
/// and may still refuse. So what these test is the ASKING - that a retried request does not queue
/// the work twice, that a reused id for a different action is a conflict rather than a silent
/// no-op, and that a shell cannot be authorised from here at all.</para>
/// </summary>
public sealed class OwnerServiceTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private Database Db => new(database.ConnectionString);

    private OwnerService Owner => new(Db);

    private HostService Host => new(Db);

    private Projection Panel => new(Db);

    private static string Uuid() => Guid.NewGuid().ToString();

    /// <summary>A registered, connected computer offering one workspace.</summary>
    private async Task<string> ConnectedHostAsync(string name = "Studio PC")
    {
        var (id, _, _) = await Owner.RegisterAsync(name);
        await Host.SyncAsync(id, [new WorkspaceRef("workspace-1", "Enactive")]);
        return id;
    }

    private async Task<(string HostId, string TaskId)> TaskAsync()
    {
        var hostId = await ConnectedHostAsync();
        return (hostId, await Owner.CreateTaskAsync(hostId, "workspace-1", "Run the tests", "Please run them."));
    }

    private static async Task<GatewayFault> RefusedAsync(Func<Task> action)
        => await Assert.ThrowsAsync<GatewayFault>(action);

    // ── the credential ──────────────────────────────────────────────────────

    /// <summary>
    /// The token exists in one place afterwards: the device it was given to. What is stored is its
    /// hash, so a copy of this database is not a set of working credentials.
    /// </summary>
    [Fact]
    public async Task A_device_token_is_returned_once_and_never_stored()
    {
        var (id, _, token) = await Owner.RegisterAsync("Laptop");

        var stored = Assert.Single(
            await database.StringsAsync($"SELECT token_hash FROM hosts WHERE id = '{id}'"));

        Assert.NotEqual(token, stored);
        Assert.Equal(Ids.Hash(token), stored);
    }

    /// <summary>
    /// Revocation withdraws what nobody has taken yet and LEAVES what the Host already accepted.
    /// An accepted command may be running right now; marking it withdrawn would make the panel
    /// claim something stopped when nothing did.
    /// </summary>
    [Fact]
    public async Task Revoking_withdraws_undelivered_commands_and_leaves_accepted_ones()
    {
        var (hostId, taskId) = await TaskAsync();
        var accepted = await Owner.StartAsync(taskId, Uuid());
        await Host.AcknowledgeAsync(hostId, accepted.Id);

        var secondTask = await Owner.CreateTaskAsync(hostId, "workspace-1", "Second", "Also this.");
        var undelivered = await Owner.StartAsync(secondTask, Uuid());

        await Owner.RevokeAsync(hostId);

        Assert.Equal("AcceptedByHost", Assert.Single(
            await database.StringsAsync($"SELECT status FROM commands WHERE id = '{accepted.Id}'")));
        Assert.Equal("Rejected", Assert.Single(
            await database.StringsAsync($"SELECT status FROM commands WHERE id = '{undelivered.Id}'")));
    }

    // ── command identity ────────────────────────────────────────────────────

    /// <summary>
    /// A retried POST. The owner's phone lost the reply and sent it again; the same command comes
    /// back and no second run was queued.
    /// </summary>
    [Fact]
    public async Task Repeating_a_request_with_the_same_id_queues_nothing_new()
    {
        var (_, taskId) = await TaskAsync();
        var commandId = Uuid();

        var first = await Owner.StartAsync(taskId, commandId);
        var second = await Owner.StartAsync(taskId, commandId);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Payload, second.Payload);
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM runs WHERE task_id = '{taskId}'"));
    }

    /// <summary>
    /// The same id for a DIFFERENT action. Returning the first one's result would mean an action
    /// the owner asked for never happened and nothing anywhere said so - so it is a conflict, and
    /// the fingerprint is what makes the difference visible.
    /// </summary>
    [Fact]
    public async Task Reusing_a_request_id_for_a_different_action_is_a_conflict()
    {
        var (_, taskId) = await TaskAsync();
        var commandId = Uuid();

        var start = await Owner.StartAsync(taskId, commandId);
        var runId = RemoteJson.Deserialize<StartTaskPayload>(start.Payload).RunId;

        var refused = await RefusedAsync(() => Owner.CancelAsync(runId, commandId));

        Assert.Equal(409, refused.Status);
    }

    [Fact]
    public async Task A_request_id_that_is_not_a_uuid_is_refused()
    {
        var (_, taskId) = await TaskAsync();

        Assert.Equal(400, (await RefusedAsync(() => Owner.StartAsync(taskId, "not-a-uuid"))).Status);
    }

    /// <summary>
    /// Two runs of one task would race each other over the same files, and the panel would have no
    /// way to say which timeline belonged to which.
    /// </summary>
    [Fact]
    public async Task A_task_that_is_already_running_is_not_started_again()
    {
        var (_, taskId) = await TaskAsync();
        await Owner.StartAsync(taskId, Uuid());

        Assert.Equal(409, (await RefusedAsync(() => Owner.StartAsync(taskId, Uuid()))).Status);
    }

    /// <summary>And once it has ended, it can be run again - that is what "already" means.</summary>
    [Fact]
    public async Task A_task_can_be_run_again_after_its_run_ends()
    {
        var (hostId, taskId) = await TaskAsync();
        var first = await Owner.StartAsync(taskId, Uuid());
        var runId = RemoteJson.Deserialize<StartTaskPayload>(first.Payload).RunId;

        await Host.PublishAsync(hostId, new HostEvent(Ids.New(), runId, 1, RemoteEventKind.Running));
        await Host.PublishAsync(hostId, new HostEvent(Ids.New(), runId, 2, RemoteEventKind.Completed, "done"));

        await Owner.StartAsync(taskId, Uuid());

        Assert.Equal(2, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM runs WHERE task_id = '{taskId}'"));
    }

    // ── naming a workspace, never a folder ──────────────────────────────────

    /// <summary>
    /// The owner picks from what the computer published. A task cannot name a path, and this is
    /// where "run a task remotely" is kept from becoming "reach any folder on that machine".
    /// </summary>
    [Fact]
    public async Task A_task_cannot_name_a_workspace_the_computer_never_published()
    {
        var hostId = await ConnectedHostAsync();

        var refused = await RefusedAsync(() =>
            Owner.CreateTaskAsync(hostId, "C:/somebody-elses-project", "Sneaky", "Read it."));

        Assert.Equal(400, refused.Status);
    }

    // ── the boundary that matters ───────────────────────────────────────────

    /// <summary>
    /// **A shell cannot be approved from the web.** The Host marks the request as not remotely
    /// decidable and the gateway refuses to accept an answer for it.
    ///
    /// <para>This is the decision that keeps the sandbox plan's threat model standing now that
    /// starting a task has a network origin: a leaked owner key must not become arbitrary command
    /// execution on the machine. Enforced here, on the server, because a button the panel chose not
    /// to draw is not a boundary - anyone can post the request themselves.</para>
    /// </summary>
    [Fact]
    public async Task A_shell_permission_cannot_be_answered_from_the_web()
    {
        var (hostId, runId, approvalId) = await WaitingForApprovalAsync(remoteDecidable: false);

        var refused = await RefusedAsync(() =>
            Owner.DecideAsync(approvalId, Uuid(), RemoteDecision.Allow, "hash-1"));

        Assert.Equal(FaultCode.ApprovalNotRemotelyDecidable, refused.Code);

        // Still pending, so the desktop can still answer it - refusing the remote answer must not
        // consume the request.
        Assert.Equal("Pending", Assert.Single(
            await database.StringsAsync($"SELECT status FROM approvals WHERE id = '{approvalId}'")));

        _ = (hostId, runId);
    }

    /// <summary>Anything else can be answered, and becomes a queued decision rather than an outcome.</summary>
    [Fact]
    public async Task Answering_a_permission_queues_a_decision_rather_than_allowing_the_action()
    {
        var (_, _, approvalId) = await WaitingForApprovalAsync(remoteDecidable: true);

        await Owner.DecideAsync(approvalId, Uuid(), RemoteDecision.Allow, "hash-1");

        // NOT "Allowed". The Host has yet to see this, and may refuse it - the desktop may have
        // answered first, or the run may be gone.
        Assert.Equal("DecisionQueued", Assert.Single(
            await database.StringsAsync($"SELECT status FROM approvals WHERE id = '{approvalId}'")));
    }

    /// <summary>An answer about a different action is not an answer to this one.</summary>
    [Fact]
    public async Task An_answer_carrying_the_wrong_action_hash_is_refused()
    {
        var (_, _, approvalId) = await WaitingForApprovalAsync(remoteDecidable: true);

        var refused = await RefusedAsync(() =>
            Owner.DecideAsync(approvalId, Uuid(), RemoteDecision.Allow, "some-other-hash"));

        Assert.Equal(FaultCode.ActionHashMismatch, refused.Code);
    }

    /// <summary>A second answer, after the first is already on its way, is refused.</summary>
    [Fact]
    public async Task A_permission_is_answered_once()
    {
        var (_, _, approvalId) = await WaitingForApprovalAsync(remoteDecidable: true);
        await Owner.DecideAsync(approvalId, Uuid(), RemoteDecision.Allow, "hash-1");

        var refused = await RefusedAsync(() =>
            Owner.DecideAsync(approvalId, Uuid(), RemoteDecision.Deny, "hash-1"));

        Assert.Equal(FaultCode.ApprovalAlreadyResolved, refused.Code);
    }

    // ── what the panel is shown ─────────────────────────────────────────────

    /// <summary>
    /// No credential, in any shape, ever reaches the panel. The row types carry a token hash and
    /// the view types do not, so leaking one means adding a field on purpose - but a projection is
    /// exactly the place where somebody adds a field on purpose, so it is asserted.
    /// </summary>
    [Fact]
    public async Task Nothing_secret_reaches_the_panel()
    {
        var (id, _, token) = await Owner.RegisterAsync("Studio PC");
        await Host.SyncAsync(id, [new WorkspaceRef("workspace-1", "Enactive")]);

        var json = RemoteJson.Serialize(await Panel.ReadAsync());

        Assert.DoesNotContain(token, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Ids.Hash(token), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A permission the panel must render as an explanation and not as a button. The flag travels
    /// so the panel can say WHY, but the refusal above is what actually holds.
    /// </summary>
    [Fact]
    public async Task The_panel_is_told_which_permissions_it_may_not_answer()
    {
        var (_, _, approvalId) = await WaitingForApprovalAsync(remoteDecidable: false);

        var pending = (await Panel.ReadAsync()).Approvals.Single(a => a.Id == approvalId);

        Assert.False(pending.RemoteDecidable);
        Assert.Equal("run_command", pending.Tool);
        Assert.Equal("dotnet test", pending.Arguments);
    }

    /// <summary>A computer that has not synced recently is shown as offline rather than as ready.</summary>
    [Fact]
    public async Task A_computer_that_stopped_syncing_is_shown_as_offline()
    {
        var hostId = await ConnectedHostAsync("Quiet PC");
        Assert.True((await Panel.ReadAsync()).Hosts.Single(h => h.Id == hostId).Online);

        await database.ExecuteAsync(
            $"UPDATE hosts SET last_seen_at = UTC_TIMESTAMP(3) - INTERVAL 5 MINUTE WHERE id = '{hostId}'");

        Assert.False((await Panel.ReadAsync()).Hosts.Single(h => h.Id == hostId).Online);
    }

    // ── shared ──────────────────────────────────────────────────────────────

    /// <summary>A run that has stopped and asked the owner for permission.</summary>
    private async Task<(string HostId, string RunId, string ApprovalId)> WaitingForApprovalAsync(
        bool remoteDecidable)
    {
        var (hostId, taskId) = await TaskAsync();
        var start = await Owner.StartAsync(taskId, Uuid());
        var runId = RemoteJson.Deserialize<StartTaskPayload>(start.Payload).RunId;
        var approvalId = Ids.New();

        await Host.PublishAsync(hostId, new HostEvent(Ids.New(), runId, 1, RemoteEventKind.Running));
        await Host.PublishAsync(hostId, new HostEvent(
            Ids.New(), runId, 2, RemoteEventKind.ApprovalRequested, "Run the project test suite",
            Approval: new ApprovalRequest(
                approvalId, "call-1", "run_command", "dotnet test", "C:/work/Enactive",
                "hash-1", remoteDecidable)));

        return (hostId, runId, approvalId);
    }
}
