namespace Enactive.Remote.Gateway.Tests;

using System.Security.Cryptography;
using System.Text;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using MySqlConnector;
using Xunit;

/// <summary>
/// What a signed-in person is allowed to ask for, and only within their own account.
///
/// <para>Nothing here executes anything: every action becomes a command in a queue the Host drains
/// and may still refuse. So what these test is the ASKING - that a retried request does not queue
/// the work twice, that a reused id for a different action is a conflict rather than a silent
/// no-op, that a shell cannot be authorised from here at all - and that every one of those answers
/// is about the asker's own rows. Another person's id answers exactly like an id that does not
/// exist, because an answer that differed would tell Bob that Alice has something by that id.</para>
///
/// <para>The rows a computer would write - its workspaces, a run's progress, a permission request -
/// are seeded with SQL here. The computer's own path is HostService's, and is tested there.</para>
/// </summary>
public sealed class UserServiceTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private static readonly TimeSpan Generously = TimeSpan.FromSeconds(5);

    /// <summary>The hash a computer would raise its permission request under.</summary>
    private static readonly string ActionHash = Ids.Hash("run_command dotnet test");

    private Database Db => new(database.ConnectionString);

    private UserService Users => new(Db, Limits.Unlimited, TimeProvider.System);

    private static string Uuid() => Guid.NewGuid().ToString();

    private Task<UserAccess> PersonAsync(string stem)
        => TestAccounts.CreateAsync(database, stem + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// A sealed field as a browser would send it. The gateway checks only the shape - it has no key
    /// - so any key and any associated data will do.
    /// </summary>
    private static string Sealed(string text)
        => Envelope.Seal(RandomNumberGenerator.GetBytes(32), 1, Encoding.UTF8.GetBytes(text), []);

    /// <summary>A computer of <paramref name="user"/>'s that has published one workspace.</summary>
    private async Task<string> ConnectedHostAsync(UserAccess user, string label = "Studio PC")
    {
        var (id, _, _) = await Users.RegisterHostAsync(user, label, default);

        await database.ExecuteAsync(
            """
            INSERT INTO host_workspaces (owner_id, host_id, workspace_id, sealed_name)
            VALUES (@owner, @host, 'workspace-1', @name)
            """,
            ("@owner", user.UserId), ("@host", id), ("@name", Sealed("Enactive")));

        return id;
    }

    private async Task<(string HostId, string TaskId)> TaskAsync(UserAccess user)
    {
        var hostId = await ConnectedHostAsync(user);
        var taskId = Uuid();
        await Users.CreateTaskAsync(user, taskId, hostId, "workspace-1", Sealed("Run the tests"), default);
        return (hostId, taskId);
    }

    private async Task<(string HostId, string TaskId, string RunId, HostCommand Start)> StartedAsync(
        UserAccess user)
    {
        var (hostId, taskId) = await TaskAsync(user);
        var start = await Users.StartAsync(user, taskId, Uuid(), Sealed("start"), default);
        return (hostId, taskId, RemoteJson.Deserialize<StartTaskPayload>(start.Payload).RunId, start);
    }

    /// <summary>A run that has stopped and asked its owner for permission.</summary>
    private async Task<(string HostId, string RunId, string ApprovalId)> WaitingForApprovalAsync(
        UserAccess user, bool remoteDecidable)
    {
        var (hostId, _, runId, _) = await StartedAsync(user);
        var approvalId = Ids.New();

        await database.ExecuteAsync(
            $"UPDATE runs SET status = 'WaitingForUser' WHERE id = '{runId}'");
        await database.ExecuteAsync(
            """
            INSERT INTO approvals (owner_id, host_id, id, run_id, tool_call_id, action_hash,
                                   remote_decidable, sealed_action, status, created_at, expires_at)
            VALUES (@owner, @host, @id, @run, 'call-1', @hash, @decidable, @action, 'Pending',
                    UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL 1 HOUR)
            """,
            ("@owner", user.UserId), ("@host", hostId), ("@id", approvalId), ("@run", runId),
            ("@hash", ActionHash), ("@decidable", remoteDecidable), ("@action", Sealed("dotnet test")));

        return (hostId, runId, approvalId);
    }

    private static async Task<GatewayFault> RefusedAsync(Func<Task> action)
        => await Assert.ThrowsAsync<GatewayFault>(action);

    /// <summary>
    /// The refusal for another person's id, compared with the refusal for an id nobody has. Equal in
    /// every part the caller can see, or the difference is an answer to "does Alice have one?".
    /// </summary>
    private static async Task AnswersLikeAMissingIdAsync(Func<Task> foreign, Func<Task> missing)
    {
        var forForeign = await RefusedAsync(foreign);
        var forMissing = await RefusedAsync(missing);

        Assert.Equal(404, forForeign.Status);
        Assert.Equal(
            (forMissing.Code, forMissing.Status, forMissing.Message),
            (forForeign.Code, forForeign.Status, forForeign.Message));
    }

    private Task<long> CountAsync(string sql) => database.ScalarLongAsync(sql);

    // ── the credential ──────────────────────────────────────────────────────

    /// <summary>
    /// The token exists in one place afterwards: the computer it was given to. What is stored is its
    /// hash, so a copy of this database is not a set of working credentials - and the computer
    /// belongs to the person who registered it, which no request body can say otherwise.
    /// </summary>
    [Fact]
    public async Task A_computer_token_is_returned_once_and_never_stored()
    {
        var alice = await PersonAsync("alice");

        var (id, label, token) = await Users.RegisterHostAsync(alice, "Laptop", default);

        Assert.Equal("Laptop", label);
        var stored = Assert.Single(
            await database.StringsAsync($"SELECT token_hash FROM hosts WHERE id = '{id}'"));
        Assert.NotEqual(token, stored);
        Assert.Equal(Ids.Hash(token), stored);
        Assert.Equal(alice.UserId, Assert.Single(
            await database.StringsAsync($"SELECT owner_id FROM hosts WHERE id = '{id}'")));
    }

    /// <summary>
    /// Revocation withdraws what nobody has taken yet and LEAVES what the Host already accepted.
    /// An accepted command may be running right now; marking it withdrawn would make the panel
    /// claim something stopped when nothing did.
    /// </summary>
    [Fact]
    public async Task Revoking_withdraws_undelivered_commands_and_leaves_accepted_ones()
    {
        var alice = await PersonAsync("alice");
        var (hostId, _, _, accepted) = await StartedAsync(alice);
        await database.ExecuteAsync(
            $"UPDATE commands SET status = 'AcceptedByHost' WHERE id = '{accepted.Id}'");

        var secondTask = Uuid();
        await Users.CreateTaskAsync(alice, secondTask, hostId, "workspace-1", Sealed("Also this"), default);
        var undelivered = await Users.StartAsync(alice, secondTask, Uuid(), Sealed("start"), default);

        await Users.RevokeHostAsync(alice, hostId, default);

        Assert.Equal("AcceptedByHost", Assert.Single(
            await database.StringsAsync($"SELECT status FROM commands WHERE id = '{accepted.Id}'")));
        Assert.Equal("Rejected", Assert.Single(
            await database.StringsAsync($"SELECT status FROM commands WHERE id = '{undelivered.Id}'")));
    }

    [Fact]
    public async Task Bob_cannot_revoke_alices_computer()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var alicesHost = await ConnectedHostAsync(alice);

        await AnswersLikeAMissingIdAsync(
            () => Users.RevokeHostAsync(bob, alicesHost, default),
            () => Users.RevokeHostAsync(bob, Ids.New(), default));

        Assert.Equal(0, await CountAsync($"SELECT revoked FROM hosts WHERE id = '{alicesHost}'"));
    }

    // ── command identity ────────────────────────────────────────────────────

    /// <summary>
    /// A retried POST. The phone lost the reply and sent it again; the same command comes back and
    /// no second run was queued.
    /// </summary>
    [Fact]
    public async Task Repeating_a_request_with_the_same_id_queues_nothing_new()
    {
        var alice = await PersonAsync("alice");
        var (_, taskId) = await TaskAsync(alice);
        var commandId = Uuid();
        var seal = Sealed("start");

        var first = await Users.StartAsync(alice, taskId, commandId, seal, default);
        var second = await Users.StartAsync(alice, taskId, commandId, seal, default);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Payload, second.Payload);
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM runs WHERE task_id = '{taskId}'"));
    }

    /// <summary>
    /// The same id for a DIFFERENT action. Returning the first one's result would mean an action
    /// the person asked for never happened and nothing anywhere said so - so it is a conflict, and
    /// the fingerprint is what makes the difference visible.
    /// </summary>
    [Fact]
    public async Task Reusing_a_request_id_for_a_different_action_is_a_conflict()
    {
        var alice = await PersonAsync("alice");
        var (_, taskId) = await TaskAsync(alice);
        var commandId = Uuid();

        var start = await Users.StartAsync(alice, taskId, commandId, Sealed("start"), default);
        var runId = RemoteJson.Deserialize<StartTaskPayload>(start.Payload).RunId;

        var refused = await RefusedAsync(() =>
            Users.CancelAsync(alice, runId, commandId, Sealed("cancel"), default));

        Assert.Equal(409, refused.Status);
    }

    /// <summary>
    /// The seal is part of the action. A request id resent with a different sealed authorization is
    /// not a retry - a retry resends the same bytes - and replaying the first command for it would
    /// hand the computer an authorization the browser no longer stands behind.
    /// </summary>
    [Fact]
    public async Task Reusing_a_request_id_with_a_different_seal_is_a_conflict()
    {
        var alice = await PersonAsync("alice");
        var (_, taskId) = await TaskAsync(alice);
        var commandId = Uuid();
        await Users.StartAsync(alice, taskId, commandId, Sealed("start"), default);

        var refused = await RefusedAsync(() =>
            Users.StartAsync(alice, taskId, commandId, Sealed("start, sealed again"), default));

        Assert.Equal(409, refused.Status);
    }

    [Fact]
    public async Task A_request_id_that_is_not_a_uuid_is_refused()
    {
        var alice = await PersonAsync("alice");
        var (_, taskId) = await TaskAsync(alice);

        var refused = await RefusedAsync(() =>
            Users.StartAsync(alice, taskId, "not-a-uuid", Sealed("start"), default));

        Assert.Equal(400, refused.Status);
    }

    /// <summary>
    /// The lookup of a used request id is itself a read of somebody's data. Keyed on the id alone,
    /// Bob sending Alice's command id with his own matching action would be handed Alice's command -
    /// her run id and her sealed task - as "the command you already queued".
    /// </summary>
    [Fact]
    public async Task Bobs_command_id_returns_none_of_alices_payload()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var (_, alicesTask, alicesRun, alicesStart) = await StartedAsync(alice);
        var (_, bobsTask) = await TaskAsync(bob);

        // Alice's id on Alice's task, sent by Bob: the task is not his, so there is nothing to start.
        await AnswersLikeAMissingIdAsync(
            () => Users.StartAsync(bob, alicesTask, alicesStart.Id, Sealed("start"), default),
            () => Users.StartAsync(bob, Uuid(), alicesStart.Id, Sealed("start"), default));

        // Alice's id on Bob's own task: a new command of Bob's, made of Bob's rows only.
        var bobs = await Users.StartAsync(bob, bobsTask, alicesStart.Id, Sealed("start"), default);
        var payload = RemoteJson.Deserialize<StartTaskPayload>(bobs.Payload);

        Assert.Equal(bobsTask, payload.TaskId);
        Assert.NotEqual(alicesRun, payload.RunId);
        Assert.DoesNotContain(alicesRun, bobs.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(
            RemoteJson.Deserialize<StartTaskPayload>(alicesStart.Payload).SealedTask,
            bobs.Payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// Request ids are the browser's, so two people can pick the same one. Each is a command in its
    /// own account: a global key would refuse Bob's as a conflict, which both breaks his request and
    /// tells him that somebody else has used that id.
    /// </summary>
    [Fact]
    public async Task The_same_command_id_in_two_accounts_is_two_commands()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var (_, alicesTask) = await TaskAsync(alice);
        var (_, bobsTask) = await TaskAsync(bob);
        var commandId = Uuid();

        await Users.StartAsync(alice, alicesTask, commandId, Sealed("start"), default);
        await Users.StartAsync(bob, bobsTask, commandId, Sealed("start"), default);

        var owners = await database.StringsAsync(
            $"SELECT owner_id FROM commands WHERE id = '{commandId}' ORDER BY owner_id");
        Assert.Equal(new[] { alice.UserId, bob.UserId }.Order(StringComparer.Ordinal), owners);
    }

    // ── tasks ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The browser makes the task id, because it seals the task under it before the gateway has
    /// seen anything. A retried create is therefore the same id with the same content, and must not
    /// fail - the browser could not tell "already written" from "refused".
    /// </summary>
    [Fact]
    public async Task Repeating_a_task_with_the_same_content_writes_it_once()
    {
        var alice = await PersonAsync("alice");
        var hostId = await ConnectedHostAsync(alice);
        var taskId = Uuid();
        var task = Sealed("Run the tests");

        await Users.CreateTaskAsync(alice, taskId, hostId, "workspace-1", task, default);
        await Users.CreateTaskAsync(alice, taskId, hostId, "workspace-1", task, default);

        Assert.Equal(task, Assert.Single(
            await database.StringsAsync($"SELECT sealed FROM tasks WHERE id = '{taskId}'")));
    }

    /// <summary>The same id for a different task is not a retry; keeping the first silently would lose the second.</summary>
    [Fact]
    public async Task Reusing_a_task_id_for_different_content_is_a_conflict()
    {
        var alice = await PersonAsync("alice");
        var hostId = await ConnectedHostAsync(alice);
        var taskId = Uuid();
        await Users.CreateTaskAsync(alice, taskId, hostId, "workspace-1", Sealed("Run the tests"), default);

        var refused = await RefusedAsync(() =>
            Users.CreateTaskAsync(alice, taskId, hostId, "workspace-1", Sealed("Delete the tests"), default));

        Assert.Equal(409, refused.Status);
    }

    /// <summary>
    /// A task id is checked only within its owner's account. Alice's ids are unknowable to Bob, but
    /// if one collided, refusing Bob's task as a conflict would tell him the id is taken.
    /// </summary>
    [Fact]
    public async Task A_task_id_alice_used_is_free_in_bobs_account()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var (_, taskId) = await TaskAsync(alice);
        var bobsHost = await ConnectedHostAsync(bob);

        await Users.CreateTaskAsync(bob, taskId, bobsHost, "workspace-1", Sealed("Bob's own"), default);

        Assert.Equal(2, await CountAsync($"SELECT COUNT(*) FROM tasks WHERE id = '{taskId}'"));
    }

    /// <summary>The id is in the associated data the task is sealed under, so only a UUID's exact text will do.</summary>
    [Fact]
    public async Task A_task_id_that_is_not_a_uuid_is_refused()
    {
        var alice = await PersonAsync("alice");
        var hostId = await ConnectedHostAsync(alice);

        var refused = await RefusedAsync(() =>
            Users.CreateTaskAsync(alice, "task-1", hostId, "workspace-1", Sealed("Run"), default));

        Assert.Equal(400, refused.Status);
    }

    /// <summary>
    /// A title and prompt in the clear are exactly what this design keeps from the gateway, so a
    /// browser that sent them unsealed - an old panel, a bug - is refused rather than stored. The
    /// size bound is the same refusal: a request body is at most 64 KB.
    /// </summary>
    [Theory]
    [InlineData("Run the tests, please")]
    [InlineData("e1:")]
    [InlineData("e1:not-base64url!")]
    [InlineData("oversized")]
    public async Task A_task_whose_sealed_part_is_not_an_envelope_is_refused(string task)
    {
        var alice = await PersonAsync("alice");
        var hostId = await ConnectedHostAsync(alice);
        var taskId = Uuid();
        // A valid envelope whose plaintext alone is the maximum, so it is over the maximum whatever the
        // envelope's own overhead.
        var sealedTask = task == "oversized" ? Sealed(new string('x', UserService.MaxSealedTask)) : task;

        var refused = await RefusedAsync(() =>
            Users.CreateTaskAsync(alice, taskId, hostId, "workspace-1", sealedTask, default));

        Assert.Equal(FaultCode.EnvelopeMalformed, refused.Code);
        Assert.Equal(400, refused.Status);
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM tasks WHERE id = '{taskId}'"));
    }

    /// <summary>
    /// The person picks from what the computer published. A task cannot name a path, and this is
    /// where "run a task remotely" is kept from becoming "reach any folder on that machine".
    /// </summary>
    [Fact]
    public async Task A_task_cannot_name_a_workspace_the_computer_never_published()
    {
        var alice = await PersonAsync("alice");
        var hostId = await ConnectedHostAsync(alice);

        var refused = await RefusedAsync(() => Users.CreateTaskAsync(
            alice, Uuid(), hostId, "C:/somebody-elses-project", Sealed("Read it"), default));

        Assert.Equal(400, refused.Status);
    }

    [Fact]
    public async Task Bob_cannot_create_a_task_on_alices_computer()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var alicesHost = await ConnectedHostAsync(alice);

        await AnswersLikeAMissingIdAsync(
            () => Users.CreateTaskAsync(bob, Uuid(), alicesHost, "workspace-1", Sealed("Mine now"), default),
            () => Users.CreateTaskAsync(bob, Uuid(), Ids.New(), "workspace-1", Sealed("Mine now"), default));

        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM tasks WHERE host_id = '{alicesHost}'"));
    }

    // ── starting ────────────────────────────────────────────────────────────

    /// <summary>
    /// The command carries the task exactly as the browser sealed it and the start authorization
    /// beside it. The gateway adds only what it made itself - the run id - and the routing ids.
    /// </summary>
    [Fact]
    public async Task Starting_queues_the_sealed_task_and_the_sealed_authorization()
    {
        var alice = await PersonAsync("alice");
        var hostId = await ConnectedHostAsync(alice);
        var taskId = Uuid();
        var task = Sealed("Run the tests");
        var authorization = Sealed("start");
        await Users.CreateTaskAsync(alice, taskId, hostId, "workspace-1", task, default);

        var command = await Users.StartAsync(alice, taskId, Uuid(), authorization, default);
        var payload = RemoteJson.Deserialize<StartTaskPayload>(command.Payload);

        Assert.Equal((CommandKind.StartTask, hostId), (command.Kind, command.HostId));
        Assert.Equal((taskId, "workspace-1", task, authorization),
            (payload.TaskId, payload.WorkspaceId, payload.SealedTask, payload.SealedStart));
        Assert.Equal(RemoteProtocol.CommandLifetime, command.ExpiresAt - command.CreatedAt);
        Assert.Equal(alice.UserId, Assert.Single(
            await database.StringsAsync($"SELECT owner_id FROM runs WHERE id = '{payload.RunId}'")));
    }

    /// <summary>
    /// Two runs of one task would race each other over the same files, and the panel would have no
    /// way to say which timeline belonged to which.
    /// </summary>
    [Fact]
    public async Task A_task_that_is_already_running_is_not_started_again()
    {
        var alice = await PersonAsync("alice");
        var (_, taskId, _, _) = await StartedAsync(alice);

        var refused = await RefusedAsync(() =>
            Users.StartAsync(alice, taskId, Uuid(), Sealed("start"), default));

        Assert.Equal(409, refused.Status);
    }

    /// <summary>And once it has ended, it can be run again - that is what "already" means.</summary>
    [Fact]
    public async Task A_task_can_be_run_again_after_its_run_ends()
    {
        var alice = await PersonAsync("alice");
        var (_, taskId, runId, _) = await StartedAsync(alice);
        await database.ExecuteAsync(
            $"UPDATE runs SET status = 'Completed', ended_at = UTC_TIMESTAMP(3) WHERE id = '{runId}'");

        await Users.StartAsync(alice, taskId, Uuid(), Sealed("start"), default);

        Assert.Equal(2, await CountAsync($"SELECT COUNT(*) FROM runs WHERE task_id = '{taskId}'"));
    }

    [Fact]
    public async Task Bob_cannot_start_alices_task()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var (_, alicesTask) = await TaskAsync(alice);

        await AnswersLikeAMissingIdAsync(
            () => Users.StartAsync(bob, alicesTask, Uuid(), Sealed("start"), default),
            () => Users.StartAsync(bob, Uuid(), Uuid(), Sealed("start"), default));

        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM runs WHERE task_id = '{alicesTask}'"));
    }

    /// <summary>
    /// A start that arrives while the computer is being revoked waits for the revocation and is then
    /// refused. Read without a lock, the computer looked live to the start's snapshot, and its
    /// command was queued after the revocation had already withdrawn the undelivered ones - a
    /// command for a computer that no longer exists, left pending for a day.
    /// </summary>
    [Fact]
    public async Task A_start_racing_a_revocation_waits_for_it_and_is_refused()
    {
        var alice = await PersonAsync("alice");
        var (hostId, taskId) = await TaskAsync(alice);

        // What RevokeHostAsync does, held open: the host row is locked and marked revoked.
        await using var connection = await database.OpenAsync();
        await using var revoking = await connection.BeginAsync(default);
        await connection.ExecuteAsync(revoking,
            "UPDATE hosts SET revoked = 1 WHERE id = @host", ("@host", hostId));

        var start = Users.StartAsync(alice, taskId, Uuid(), Sealed("start"), default);
        await Assert.ThrowsAsync<TimeoutException>(() => start.WaitAsync(TimeSpan.FromMilliseconds(500)));

        await revoking.CommitAsync();

        var refused = await Assert.ThrowsAsync<GatewayFault>(() => start.WaitAsync(Generously));
        Assert.Equal(404, refused.Status);
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM runs WHERE task_id = '{taskId}'"));
    }

    // ── cancelling ──────────────────────────────────────────────────────────

    /// <summary>
    /// Asking is all a cancel does: the run is marked as asked, and only the Host's own Cancelled
    /// event says it stopped. The authorization travels sealed, so the gateway cannot cancel a run
    /// on its own.
    /// </summary>
    [Fact]
    public async Task Cancelling_asks_and_carries_the_sealed_authorization()
    {
        var alice = await PersonAsync("alice");
        var (hostId, _, runId, _) = await StartedAsync(alice);
        var authorization = Sealed("cancel");

        var command = await Users.CancelAsync(alice, runId, Uuid(), authorization, default);

        Assert.Equal((CommandKind.CancelRun, hostId), (command.Kind, command.HostId));
        Assert.Equal(new CancelRunPayload(runId, authorization),
            RemoteJson.Deserialize<CancelRunPayload>(command.Payload));
        Assert.Equal("CancelRequested", Assert.Single(
            await database.StringsAsync($"SELECT status FROM runs WHERE id = '{runId}'")));
    }

    [Fact]
    public async Task Bob_cannot_cancel_alices_run()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var (_, _, alicesRun, _) = await StartedAsync(alice);

        await AnswersLikeAMissingIdAsync(
            () => Users.CancelAsync(bob, alicesRun, Uuid(), Sealed("cancel"), default),
            () => Users.CancelAsync(bob, Ids.New(), Uuid(), Sealed("cancel"), default));

        Assert.Equal("Queued", Assert.Single(
            await database.StringsAsync($"SELECT status FROM runs WHERE id = '{alicesRun}'")));
    }

    // ── the boundary that matters ───────────────────────────────────────────

    /// <summary>
    /// **A shell cannot be approved from the web.** The Host marks the request as not remotely
    /// decidable and the gateway refuses to accept an answer for it.
    ///
    /// <para>This is the decision that keeps the sandbox plan's threat model standing now that
    /// starting a task has a network origin: a stolen session must not become arbitrary command
    /// execution on the machine. Enforced here, on the server, because a button the panel chose not
    /// to draw is not a boundary - anyone can post the request themselves.</para>
    /// </summary>
    [Fact]
    public async Task A_shell_permission_cannot_be_answered_from_the_web()
    {
        var alice = await PersonAsync("alice");
        var (hostId, _, approvalId) = await WaitingForApprovalAsync(alice, remoteDecidable: false);

        var refused = await RefusedAsync(() => Users.DecideAsync(
            alice, approvalId, hostId, Uuid(), RemoteDecision.Allow, ActionHash, Sealed("allow"), default));

        Assert.Equal(FaultCode.ApprovalNotRemotelyDecidable, refused.Code);

        // Still pending, so the desktop can still answer it - refusing the remote answer must not
        // consume the request.
        Assert.Equal("Pending", Assert.Single(
            await database.StringsAsync($"SELECT status FROM approvals WHERE id = '{approvalId}'")));
    }

    /// <summary>
    /// Anything else can be answered, and becomes a queued decision rather than an outcome. The
    /// decision is kept in the clear only so the panel can say "Sent: Allow"; the Host acts on the
    /// sealed copy, which is the only one a gateway cannot write.
    /// </summary>
    [Fact]
    public async Task Answering_a_permission_queues_a_decision_rather_than_allowing_the_action()
    {
        var alice = await PersonAsync("alice");
        var (hostId, runId, approvalId) = await WaitingForApprovalAsync(alice, remoteDecidable: true);
        var answer = Sealed("allow");

        var command = await Users.DecideAsync(
            alice, approvalId, hostId, Uuid(), RemoteDecision.Allow, ActionHash, answer, default);

        // NOT "Allowed". The Host has yet to see this, and may refuse it - the desktop may have
        // answered first, or the run may be gone.
        Assert.Equal("DecisionQueued|Allow", Assert.Single(await database.StringsAsync(
            $"SELECT CONCAT(status, '|', requested_decision) FROM approvals WHERE id = '{approvalId}'")));
        Assert.Equal((CommandKind.ResolveApproval, hostId), (command.Kind, command.HostId));
        Assert.Equal(new ResolveApprovalPayload(approvalId, runId, ActionHash, answer),
            RemoteJson.Deserialize<ResolveApprovalPayload>(command.Payload));
    }

    /// <summary>An answer about a different action is not an answer to this one.</summary>
    [Fact]
    public async Task An_answer_carrying_the_wrong_action_hash_is_refused()
    {
        var alice = await PersonAsync("alice");
        var (hostId, _, approvalId) = await WaitingForApprovalAsync(alice, remoteDecidable: true);

        var refused = await RefusedAsync(() => Users.DecideAsync(
            alice, approvalId, hostId, Uuid(), RemoteDecision.Allow, Ids.Hash("something else"),
            Sealed("allow"), default));

        Assert.Equal(FaultCode.ActionHashMismatch, refused.Code);
    }

    /// <summary>A second answer, after the first is already on its way, is refused.</summary>
    [Fact]
    public async Task A_permission_is_answered_once()
    {
        var alice = await PersonAsync("alice");
        var (hostId, _, approvalId) = await WaitingForApprovalAsync(alice, remoteDecidable: true);
        await Users.DecideAsync(
            alice, approvalId, hostId, Uuid(), RemoteDecision.Allow, ActionHash, Sealed("allow"), default);

        var refused = await RefusedAsync(() => Users.DecideAsync(
            alice, approvalId, hostId, Uuid(), RemoteDecision.Deny, ActionHash, Sealed("deny"), default));

        Assert.Equal(FaultCode.ApprovalAlreadyResolved, refused.Code);
    }

    /// <summary>
    /// A request past its expiry is not answered, by the service's own clock. The computer gives up
    /// on it at that time, so an answer queued afterwards would show as sent for a question nobody
    /// is asking any more.
    /// </summary>
    [Fact]
    public async Task An_expired_request_is_not_answered()
    {
        var alice = await PersonAsync("alice");
        var (hostId, _, approvalId) = await WaitingForApprovalAsync(alice, remoteDecidable: true);
        var later = new UserService(Db, Limits.Unlimited, new FixedClock(DateTimeOffset.UtcNow.AddHours(2)));

        var refused = await RefusedAsync(() => later.DecideAsync(
            alice, approvalId, hostId, Uuid(), RemoteDecision.Allow, ActionHash, Sealed("allow"), default));

        Assert.Equal(409, refused.Status);
        Assert.Equal("Pending", Assert.Single(
            await database.StringsAsync($"SELECT status FROM approvals WHERE id = '{approvalId}'")));
    }

    [Fact]
    public async Task Bob_cannot_answer_alices_permission()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var (hostId, _, approvalId) = await WaitingForApprovalAsync(alice, remoteDecidable: true);

        await AnswersLikeAMissingIdAsync(
            () => Users.DecideAsync(
                bob, approvalId, hostId, Uuid(), RemoteDecision.Allow, ActionHash, Sealed("allow"), default),
            () => Users.DecideAsync(
                bob, Ids.New(), hostId, Uuid(), RemoteDecision.Allow, ActionHash, Sealed("allow"), default));

        Assert.Equal("Pending", Assert.Single(
            await database.StringsAsync($"SELECT status FROM approvals WHERE id = '{approvalId}'")));
    }

    /// <summary>
    /// Approval ids are made by the computer and unique only on it, so a request is addressed by its
    /// computer too. The same id under another of Alice's computers is a different request - here,
    /// one that does not exist.
    /// </summary>
    [Fact]
    public async Task A_permission_is_addressed_by_its_computer()
    {
        var alice = await PersonAsync("alice");
        var (_, _, approvalId) = await WaitingForApprovalAsync(alice, remoteDecidable: true);
        var otherHost = await ConnectedHostAsync(alice, "Laptop");

        var refused = await RefusedAsync(() => Users.DecideAsync(
            alice, approvalId, otherHost, Uuid(), RemoteDecision.Allow, ActionHash, Sealed("allow"), default));

        Assert.Equal(404, refused.Status);
    }

    // ── device commands ─────────────────────────────────────────────────────

    /// <summary>
    /// Which browser a computer trusts is the computer's decision, made on what only a trusted device
    /// could seal. The gateway queues the envelope and nothing else - it never learns which device.
    /// </summary>
    [Theory]
    [InlineData(CommandKind.RevokeDevice)]
    [InlineData(CommandKind.EndorseDevice)]
    public async Task A_device_command_is_queued_sealed_for_its_computer(CommandKind kind)
    {
        var alice = await PersonAsync("alice");
        var hostId = await ConnectedHostAsync(alice);
        var instruction = Sealed("device");

        var command = await Users.SendDeviceCommandAsync(alice, hostId, kind, Uuid(), instruction, default);

        Assert.Equal((kind, hostId, CommandStatus.PendingDelivery), (command.Kind, command.HostId, command.Status));
        Assert.Equal(new DevicePayload(instruction), RemoteJson.Deserialize<DevicePayload>(command.Payload));
    }

    /// <summary>
    /// The device path checks nothing about runs, tasks or approvals, so the commands that need those
    /// checks cannot be sent through it.
    /// </summary>
    [Theory]
    [InlineData(CommandKind.StartTask)]
    [InlineData(CommandKind.CancelRun)]
    [InlineData(CommandKind.ResolveApproval)]
    public async Task Only_device_commands_go_through_the_device_path(CommandKind kind)
    {
        var alice = await PersonAsync("alice");
        var hostId = await ConnectedHostAsync(alice);

        var refused = await RefusedAsync(() =>
            Users.SendDeviceCommandAsync(alice, hostId, kind, Uuid(), Sealed("device"), default));

        Assert.Equal(400, refused.Status);
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM commands WHERE host_id = '{hostId}'"));
    }

    [Fact]
    public async Task Bob_cannot_send_a_device_command_to_alices_computer()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var alicesHost = await ConnectedHostAsync(alice);

        await AnswersLikeAMissingIdAsync(
            () => Users.SendDeviceCommandAsync(
                bob, alicesHost, CommandKind.RevokeDevice, Uuid(), Sealed("device"), default),
            () => Users.SendDeviceCommandAsync(
                bob, Ids.New(), CommandKind.RevokeDevice, Uuid(), Sealed("device"), default));

        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM commands WHERE host_id = '{alicesHost}'"));
    }

    // ── sealed arguments ────────────────────────────────────────────────────

    /// <summary>
    /// Every command's authorization is an envelope of bounded size. Anything else is refused before
    /// it is queued: the computer would refuse it anyway, a day later and with nobody watching.
    /// </summary>
    [Theory]
    [InlineData("start", "plain")]
    [InlineData("start", "oversized")]
    [InlineData("cancel", "plain")]
    [InlineData("cancel", "oversized")]
    [InlineData("decide", "plain")]
    [InlineData("decide", "oversized")]
    [InlineData("device", "plain")]
    [InlineData("device", "oversized")]
    public async Task A_command_whose_seal_is_not_an_envelope_is_refused(string command, string seal)
    {
        var alice = await PersonAsync("alice");
        var (hostId, runId, approvalId) = await WaitingForApprovalAsync(alice, remoteDecidable: true);

        // A task not yet started, so that with a good seal every one of these would be queued.
        var taskId = Uuid();
        await Users.CreateTaskAsync(alice, taskId, hostId, "workspace-1", Sealed("Another"), default);
        var commandsBefore = await CountAsync($"SELECT COUNT(*) FROM commands WHERE host_id = '{hostId}'");

        // "oversized" is a valid envelope whose plaintext alone is the maximum, so it is over the
        // maximum whatever the envelope's own overhead.
        var max = command == "device" ? UserService.MaxSealedDevice : UserService.MaxSealedCommand;
        var bad = seal == "plain" ? "Allow" : Sealed(new string('x', max));

        Func<Task> send = command switch
        {
            "start" => () => Users.StartAsync(alice, taskId, Uuid(), bad, default),
            "cancel" => () => Users.CancelAsync(alice, runId, Uuid(), bad, default),
            "decide" => () => Users.DecideAsync(
                alice, approvalId, hostId, Uuid(), RemoteDecision.Allow, ActionHash, bad, default),
            _ => () => Users.SendDeviceCommandAsync(
                alice, hostId, CommandKind.RevokeDevice, Uuid(), bad, default)
        };

        var refused = await RefusedAsync(send);

        Assert.Equal((FaultCode.EnvelopeMalformed, 400), (refused.Code, refused.Status));
        Assert.Equal(commandsBefore,
            await CountAsync($"SELECT COUNT(*) FROM commands WHERE host_id = '{hostId}'"));
    }

    // ── replaying a retry ───────────────────────────────────────────────────

    /// <summary>
    /// A retry is answered with the queued command only while the computer can still receive it.
    /// Once it is revoked, the same request id gets the refusal a new request would - replaying the
    /// first answer would tell the panel its request was queued for a computer that is gone.
    /// </summary>
    [Theory]
    [InlineData("start")]
    [InlineData("cancel")]
    [InlineData("decide")]
    public async Task A_retry_is_not_replayed_once_the_computer_is_revoked(string command)
    {
        var alice = await PersonAsync("alice");
        var (hostId, runId, approvalId) = await WaitingForApprovalAsync(alice, remoteDecidable: true);
        var taskId = Uuid();
        await Users.CreateTaskAsync(alice, taskId, hostId, "workspace-1", Sealed("Another"), default);
        var commandId = Uuid();
        var seal = Sealed(command);

        Func<Task<HostCommand>> send = command switch
        {
            "start" => () => Users.StartAsync(alice, taskId, commandId, seal, default),
            "cancel" => () => Users.CancelAsync(alice, runId, commandId, seal, default),
            _ => () => Users.DecideAsync(
                alice, approvalId, hostId, commandId, RemoteDecision.Allow, ActionHash, seal, default)
        };

        await send();
        await Users.RevokeHostAsync(alice, hostId, default);

        Assert.Equal(404, (await RefusedAsync(send)).Status);
    }

    // ── another account's locks ─────────────────────────────────────────────

    /// <summary>
    /// Holds Alice's row locked in an open transaction, as one of her own operations does while it
    /// runs, and makes Bob's call with her id. It must be refused at once. A lookup that locked
    /// Alice's row on Bob's behalf - filtered by owner, but found through the table's global key -
    /// made Bob wait for Alice: the wait told him the id exists, and his transaction held up hers.
    /// </summary>
    private async Task RefusedWithoutWaitingOnAliceAsync(
        string lockAlicesRow, (string Name, object? Value)[] parameters, Func<Task> bobsCall)
    {
        await using var connection = await database.OpenAsync();
        await using var alicesTransaction = await connection.BeginAsync(default);
        await connection.ExecuteAsync(alicesTransaction, lockAlicesRow, parameters);

        var call = bobsCall();

        var refused = await Assert.ThrowsAsync<GatewayFault>(() => call.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(404, refused.Status);
    }

    [Fact]
    public async Task Bobs_revocation_does_not_wait_on_alices_computer()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var alicesHost = await ConnectedHostAsync(alice);

        await RefusedWithoutWaitingOnAliceAsync(
            "SELECT id FROM hosts WHERE id = @id FOR UPDATE", [("@id", alicesHost)],
            () => Users.RevokeHostAsync(bob, alicesHost, default));
    }

    /// <summary>The check that a computer is live, which every command and every new task makes.</summary>
    [Fact]
    public async Task Bobs_task_does_not_wait_on_alices_computer()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var alicesHost = await ConnectedHostAsync(alice);

        await RefusedWithoutWaitingOnAliceAsync(
            "SELECT id FROM hosts WHERE id = @id FOR UPDATE", [("@id", alicesHost)],
            () => Users.CreateTaskAsync(bob, Uuid(), alicesHost, "workspace-1", Sealed("Mine"), default));
    }

    [Fact]
    public async Task Bobs_cancel_does_not_wait_on_alices_run()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var (_, _, alicesRun, _) = await StartedAsync(alice);

        await RefusedWithoutWaitingOnAliceAsync(
            "SELECT id FROM runs WHERE id = @id FOR UPDATE", [("@id", alicesRun)],
            () => Users.CancelAsync(bob, alicesRun, Uuid(), Sealed("cancel"), default));
    }

    [Fact]
    public async Task Bobs_answer_does_not_wait_on_alices_permission()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var (hostId, _, approvalId) = await WaitingForApprovalAsync(alice, remoteDecidable: true);

        await RefusedWithoutWaitingOnAliceAsync(
            "SELECT id FROM approvals WHERE host_id = @host AND id = @id FOR UPDATE",
            [("@host", hostId), ("@id", approvalId)],
            () => Users.DecideAsync(
                bob, approvalId, hostId, Uuid(), RemoteDecision.Allow, ActionHash, Sealed("allow"), default));
    }

    // ── lock order and deadlocks ────────────────────────────────────────────

    /// <summary>The call has not finished: it is waiting for a lock somebody else holds.</summary>
    private static async Task StillWaitingAsync(Task call)
        => await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(TimeSpan.FromMilliseconds(500)));

    /// <summary>
    /// A command reads its computer before it locks the thing on it. Locking the target first, an
    /// answer held its request while it waited behind a revocation for the computer - and a report
    /// from that computer, already holding the computer shared, waited for the request: see the race
    /// below. Here a revocation holds the computer, and the target must still be free while the
    /// command waits; NOWAIT makes a held one fail at once instead of hanging the test.
    /// </summary>
    [Theory]
    [InlineData("start")]
    [InlineData("cancel")]
    [InlineData("decide")]
    public async Task A_command_reads_its_computer_before_it_locks_its_target(string command)
    {
        var alice = await PersonAsync("alice");
        var (hostId, runId, approvalId) = await WaitingForApprovalAsync(alice, remoteDecidable: true);
        var taskId = Uuid();
        await Users.CreateTaskAsync(alice, taskId, hostId, "workspace-1", Sealed("Another"), default);

        // A revocation, held open: the computer is locked exclusively.
        await using var connection = await database.OpenAsync();
        await using var revoking = await connection.BeginAsync(default);
        await connection.ExecuteAsync(revoking,
            "SELECT id FROM hosts WHERE id = @host FOR UPDATE", ("@host", hostId));

        var send = command switch
        {
            "start" => Users.StartAsync(alice, taskId, Uuid(), Sealed("start"), default),
            "cancel" => Users.CancelAsync(alice, runId, Uuid(), Sealed("cancel"), default),
            _ => Users.DecideAsync(
                alice, approvalId, hostId, Uuid(), RemoteDecision.Allow, ActionHash, Sealed("allow"), default)
        };

        await StillWaitingAsync(send);

        var (lockTarget, target) = command switch
        {
            "start" => ("SELECT id FROM tasks WHERE owner_id = @owner AND id = @id FOR UPDATE NOWAIT", taskId),
            "cancel" => ("SELECT id FROM runs WHERE owner_id = @owner AND id = @id FOR UPDATE NOWAIT", runId),
            _ => ("SELECT id FROM approvals WHERE host_id = @host AND id = @id FOR UPDATE NOWAIT", approvalId)
        };

        await using (var probing = await database.OpenAsync())
        await using (var probe = await probing.BeginAsync(default))
        {
            await probing.ExecuteAsync(probe, lockTarget,
                ("@owner", alice.UserId), ("@host", hostId), ("@id", target));
        }

        await revoking.RollbackAsync();
        await send.WaitAsync(Generously);
    }

    /// <summary>
    /// A computer reporting how a permission request ended, its owner answering that request, and
    /// its owner revoking the computer, all at once. The answer locked its request and then read the
    /// computer; the report had read the computer and then reached for the request; and the
    /// revocation, waiting to lock the computer exclusively, queued between them, so the answer's
    /// shared read waited behind it. Each waited for the next, and the database rolled one back: a
    /// 500 for the person, or an event the computer retried and then parked.
    ///
    /// <para>The answer reads the computer first now, so it waits behind the revocation holding
    /// nothing; the report finishes, the revocation after it, and the answer is refused in its own
    /// words.</para>
    /// </summary>
    [Fact]
    public async Task A_decide_publish_revoke_race_does_not_surface_a_deadlock()
    {
        var alice = await PersonAsync("alice");
        var (hostId, runId, approvalId) = await WaitingForApprovalAsync(alice, remoteDecidable: true);
        var computer = new HostService(Db);

        // Holds the report at the right moment: it has read the account and the computer, shared,
        // and waits for the run.
        await using var connection = await database.OpenAsync();
        await using var holding = await connection.BeginAsync(default);
        await connection.ExecuteAsync(holding,
            "SELECT id FROM runs WHERE id = @run FOR UPDATE", ("@run", runId));

        var publish = computer.PublishAsync(new HostAccess(hostId, alice.UserId), new HostEvent(
            Ids.New(), runId, 1, RemoteEventKind.ApprovalResolved,
            Resolution: new ApprovalResolution(approvalId, ActionHash, ApprovalOutcome.Allowed)));
        await StillWaitingAsync(publish);

        var revoke = Users.RevokeHostAsync(alice, hostId, default);
        await StillWaitingAsync(revoke);

        var decide = Users.DecideAsync(
            alice, approvalId, hostId, Uuid(), RemoteDecision.Allow, ActionHash, Sealed("allow"), default);
        await StillWaitingAsync(decide);

        // Let the report go on to the request.
        await holding.RollbackAsync();

        var all = Task.WhenAll(publish, revoke, decide);
        await Record.ExceptionAsync(() => all.WaitAsync(Generously));

        Assert.True(all.IsCompleted, "The three calls were still waiting after five seconds.");
        Assert.All(new[] { publish, revoke, decide }, call => Assert.False(
            call.Exception?.InnerException is MySqlException, call.Exception?.InnerException?.Message));

        // The report and the revocation went through; the answer came too late for either.
        Assert.True(publish.IsCompletedSuccessfully);
        Assert.True(revoke.IsCompletedSuccessfully);
        Assert.IsType<GatewayFault>(decide.Exception?.InnerException);
        Assert.Equal("Allowed", Assert.Single(
            await database.StringsAsync($"SELECT status FROM approvals WHERE id = '{approvalId}'")));
        Assert.Equal(0, await CountAsync(
            $"SELECT COUNT(*) FROM commands WHERE host_id = '{hostId}' AND kind = 'ResolveApproval'"));
    }

    /// <summary>
    /// A deadlock no lock order prevents - another transaction takes the same rows the other way
    /// round - is retried, and the person's cancel is queued as if nothing had happened. Without the
    /// retry the database's choice of victim reached the person as a 500, for a request that would
    /// have succeeded a moment later.
    /// </summary>
    [Fact]
    public async Task A_deadlock_is_retried_and_succeeds()
    {
        var alice = await PersonAsync("alice");
        var (hostId, _, runId, _) = await StartedAsync(alice);
        var commandId = Uuid();

        await using var connection = await database.OpenAsync();
        await using var other = await connection.BeginAsync(default);

        // The database rolls back the transaction that has written least. These rows make the other
        // transaction the heavier one, so the cancel is the one chosen.
        for (var i = 0; i < 20; i++)
        {
            await connection.ExecuteAsync(other,
                "INSERT INTO audit (owner_id, at, actor, action) VALUES (NULL, UTC_TIMESTAMP(3), 'operator', 'test')");
        }

        // An uncommitted row under the cancel's own command id: the cancel locks the run and then
        // waits here, in its idempotency lookup.
        await connection.ExecuteAsync(other,
            """
            INSERT INTO commands (owner_id, id, host_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES (@owner, @id, @host, 'CancelRun', '{}', SHA2(@id, 256), 'AcceptedByHost',
                    UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL 1 DAY)
            """,
            ("@owner", alice.UserId), ("@id", commandId), ("@host", hostId));

        var cancel = Users.CancelAsync(alice, runId, commandId, Sealed("cancel"), default);
        await StillWaitingAsync(cancel);

        // ...and this reaches for the run the cancel holds: each waits for the other.
        await connection.ExecuteAsync(other,
            "SELECT id FROM runs WHERE id = @run FOR UPDATE", ("@run", runId));
        await other.RollbackAsync();

        Assert.Equal(CommandKind.CancelRun, (await cancel.WaitAsync(Generously)).Kind);
        Assert.Equal("CancelRequested", Assert.Single(
            await database.StringsAsync($"SELECT status FROM runs WHERE id = '{runId}'")));
    }

    /// <summary>
    /// Only a deadlock is worth running again. Anything else - a duplicate key, here - would fail the
    /// same way every time, and three attempts would only make the person wait three times as long
    /// for the same error.
    /// </summary>
    [Fact]
    public async Task Only_deadlocks_are_retried()
    {
        var alice = await PersonAsync("alice");
        var hostId = await ConnectedHostAsync(alice);
        var attempts = 0;

        var refused = await Assert.ThrowsAsync<MySqlException>(() => Db.InTransactionAsync(
            async (connection, transaction) =>
            {
                attempts++;

                // The workspace ConnectedHostAsync published, a second time.
                return await connection.ExecuteAsync(transaction,
                    """
                    INSERT INTO host_workspaces (owner_id, host_id, workspace_id, sealed_name)
                    VALUES (@owner, @host, 'workspace-1', @name)
                    """,
                    ("@owner", alice.UserId), ("@host", hostId), ("@name", Sealed("Enactive")));
            },
            default));

        Assert.Equal(MySqlErrorCode.DuplicateKeyEntry, refused.ErrorCode);
        Assert.Equal(1, attempts);
    }

    // ── notices ─────────────────────────────────────────────────────────────

    /// <summary>
    /// "Mark all read" covers what the person was shown and nothing newer. A notice that committed
    /// after the panel's snapshot - a permission request that arrived a second before the click -
    /// stays unread, or it would be marked read without ever having been seen. And Bob's click is
    /// about Bob's notices.
    /// </summary>
    [Fact]
    public async Task Mark_read_stops_at_the_displayed_ordinal()
    {
        var alice = await PersonAsync("alice");
        var bob = await PersonAsync("bob");
        var (_, _, alicesRun, _) = await StartedAsync(alice);
        var (_, _, bobsRun, _) = await StartedAsync(bob);

        var first = await NoticeAsync(alice, alicesRun);
        var displayed = await NoticeAsync(alice, alicesRun);
        var newer = await NoticeAsync(alice, alicesRun);
        var bobs = await NoticeAsync(bob, bobsRun);

        await Users.MarkNoticesReadAsync(alice, 1, displayed, default);

        Assert.Equal(1, await IsReadAsync(alice, first));
        Assert.Equal(1, await IsReadAsync(alice, displayed));
        Assert.Equal(0, await IsReadAsync(alice, newer));
        Assert.Equal(0, await IsReadAsync(bob, bobs));
    }

    /// <summary>
    /// The bound is a position on the person's line, and is refused when it is not one. After a reset
    /// of the line, an ordinal of the old epoch counts something else; an ordinal past the line's end
    /// counts notices nobody has been shown. Either, taken as it came, marks read what no screen
    /// displayed - so nothing is marked at all.
    /// </summary>
    [Fact]
    public async Task Mark_read_refuses_a_bound_that_is_not_on_the_current_line()
    {
        var alice = await PersonAsync("alice");
        var (_, _, run, _) = await StartedAsync(alice);
        var shown = await NoticeAsync(alice, run);

        var ahead = await Assert.ThrowsAsync<GatewayFault>(
            () => Users.MarkNoticesReadAsync(alice, 1, shown + 1, default));

        await database.ExecuteAsync(
            "UPDATE user_streams SET epoch = epoch + 1 WHERE owner_id = @owner", ("@owner", alice.UserId));

        var stale = await Assert.ThrowsAsync<GatewayFault>(
            () => Users.MarkNoticesReadAsync(alice, 1, shown, default));

        Assert.Equal(400, ahead.Status);
        Assert.Equal(400, stale.Status);
        Assert.Equal(0, await IsReadAsync(alice, shown));
    }

    /// <summary>
    /// The line is read as it stands when the marking is done, not as a snapshot taken earlier. A reset
    /// of the line committing while the click is in flight would otherwise be missed: the click compared
    /// its cursor with the old epoch, matched, and marked read by ordinals that now count other notices.
    /// Here the reset holds the line; the click waits for it, sees the new epoch and marks nothing.
    /// </summary>
    [Fact]
    public async Task Mark_read_waits_for_a_reset_of_the_line_in_flight_and_then_refuses()
    {
        var alice = await PersonAsync("alice");
        var (_, _, run, _) = await StartedAsync(alice);
        var shown = await NoticeAsync(alice, run);

        await using var connection = await database.OpenAsync();
        await using var resetting = await connection.BeginAsync(default);
        await connection.ExecuteAsync(resetting,
            "UPDATE user_streams SET epoch = epoch + 1 WHERE owner_id = @owner", ("@owner", alice.UserId));

        var mark = Users.MarkNoticesReadAsync(alice, 1, shown, default);
        await StillWaitingAsync(mark);

        await resetting.CommitAsync();

        var refused = await Assert.ThrowsAsync<GatewayFault>(() => mark.WaitAsync(Generously));
        Assert.Equal(400, refused.Status);
        Assert.Equal(0, await IsReadAsync(alice, shown));
    }

    /// <summary>A notice as the gateway writes one: on its owner's line, unread.</summary>
    private async Task<long> NoticeAsync(UserAccess owner, string runId)
    {
        await using var connection = await Db.OpenAsync();
        await using var transaction = await connection.BeginAsync(default);
        var ordinal = await StreamCursor.NextAsync(connection, transaction, owner.UserId);

        await connection.ExecuteAsync(transaction,
            """
            INSERT INTO notices (id, owner_id, run_id, kind, at, is_read, ordinal)
            VALUES (@id, @owner, @run, 'Completed', UTC_TIMESTAMP(3), 0, @ordinal)
            """,
            ("@id", Ids.New()), ("@owner", owner.UserId), ("@run", runId), ("@ordinal", ordinal));

        await transaction.CommitAsync();
        return ordinal;
    }

    private Task<long> IsReadAsync(UserAccess owner, long ordinal)
        => CountAsync($"SELECT is_read FROM notices WHERE owner_id = '{owner.UserId}' AND ordinal = {ordinal}");

    /// <summary>A clock that says what it is told, for a check that depends on the time.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
