namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Xunit;

/// <summary>
/// The state machine a Host drives. Stage 2 of the remote-access design.
///
/// <para>Each test names a refusal or a transition the gateway has to get right for a Host with a
/// durable outbox to be able to make progress. Where a refusal is asserted, the FAULT CODE is what
/// is asserted and not the message: the code is what the Host classifies, and a message is for a
/// person reading a log.</para>
/// </summary>
public sealed class HostServiceTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private const string HostId = "1111111111111111111111111111aaaa";
    private const string OtherHostId = "2222222222222222222222222222bbbb";

    private HostService Service => new(new Database(database.ConnectionString));

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>A host, a task and a queued run of its own, so tests cannot disturb each other.</summary>
    private async Task<string> QueuedRunAsync(string hostId = HostId)
    {
        await database.ExecuteAsync($"""
            INSERT IGNORE INTO hosts (id, name, token_hash, revoked, created_at)
            VALUES ('{hostId}', 'Host', SHA2('{hostId}', 256), 0, UTC_TIMESTAMP(3))
            """);

        var taskId = NewId();
        var runId = NewId();

        await database.ExecuteAsync($"""
            INSERT INTO tasks (id, host_id, workspace_id, title, prompt, created_at)
              VALUES ('{taskId}', '{hostId}', 'workspace-1', 'Test', 'Do it.', UTC_TIMESTAMP(3));
            INSERT INTO runs (id, task_id, host_id, status, created_at)
              VALUES ('{runId}', '{taskId}', '{hostId}', 'Queued', UTC_TIMESTAMP(3));
            """);

        return runId;
    }

    /// <summary>A run that has already reported it started, which is where most of these begin.</summary>
    private async Task<string> RunningRunAsync()
    {
        var runId = await QueuedRunAsync();
        await Service.PublishAsync(HostId, Event(runId, 1, RemoteEventKind.Running));
        return runId;
    }

    private static HostEvent Event(
        string runId, long sequence, RemoteEventKind kind,
        string? detail = null, ApprovalRequest? approval = null, ApprovalResolution? resolution = null)
        => new(NewId(), runId, sequence, kind, detail, approval, resolution);

    private static ApprovalRequest Request(string id, string hash = "hash-1", bool remoteDecidable = true)
        => new(id, "call-1", hash, remoteDecidable, "e1:sealed-action");

    private async Task<RemoteRunStatus> StatusAsync(string runId)
        => Enum.Parse<RemoteRunStatus>(
            (await database.StringsAsync($"SELECT status FROM runs WHERE id = '{runId}'")).Single());

    private static async Task<GatewayFault> RefusedAsync(Func<Task> action)
        => await Assert.ThrowsAsync<GatewayFault>(action);

    // ── delivery: what a retry means ────────────────────────────────────────

    /// <summary>
    /// A lost reply, not a mistake. The Host keeps the event and sends it again with the same id;
    /// refusing would make it retry for ever.
    /// </summary>
    [Fact]
    public async Task A_retried_event_is_accepted_silently()
    {
        var runId = await RunningRunAsync();
        var progress = Event(runId, 2, RemoteEventKind.Progress, "reading files");

        await Service.PublishAsync(HostId, progress);
        await Service.PublishAsync(HostId, progress);

        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM events WHERE run_id = '{runId}' AND sequence = 2"));
    }

    /// <summary>
    /// The subtle one, and the reason deduplication is checked BEFORE the run is examined.
    ///
    /// <para>The event being retried is the one that ended the run. Ask "has this run ended?" first
    /// and the answer is yes, so the retry is refused with RunEnded - a Drop code - and the Host
    /// throws away an event it never got an acknowledgement for. It would look fine. The run would
    /// be correct. And the next lost reply on a terminal event would do it again.</para>
    /// </summary>
    [Fact]
    public async Task A_retry_of_the_event_that_ended_the_run_is_still_accepted()
    {
        var runId = await RunningRunAsync();
        var completed = Event(runId, 2, RemoteEventKind.Completed, "all done");

        await Service.PublishAsync(HostId, completed);
        await Service.PublishAsync(HostId, completed);

        Assert.Equal(RemoteRunStatus.Completed, await StatusAsync(runId));
    }

    /// <summary>Behind by a number, so it has been applied already.</summary>
    [Fact]
    public async Task An_event_that_is_not_ahead_of_what_was_applied_is_refused()
    {
        var runId = await RunningRunAsync();
        await Service.PublishAsync(HostId, Event(runId, 5, RemoteEventKind.Progress));

        var refused = await RefusedAsync(() =>
            Service.PublishAsync(HostId, Event(runId, 5, RemoteEventKind.Progress)));

        Assert.Equal(FaultCode.SequenceAlreadyApplied, refused.Code);
        Assert.Equal(FaultDisposition.Drop, refused.ToContract().Disposition);
    }

    /// <summary>
    /// Sequences increase; they are not contiguous, and this is where that matters.
    ///
    /// <para>An event the Host's outbox discarded on a Drop-coded refusal never arrives, so a hole
    /// is the normal consequence of the fault taxonomy working. A gateway that demanded the next
    /// number would wedge that run's queue permanently, and the Host would have no way out: the
    /// missing event is gone.</para>
    /// </summary>
    [Fact]
    public async Task A_gap_left_by_a_dropped_event_does_not_wedge_the_run()
    {
        var runId = await RunningRunAsync();

        await Service.PublishAsync(HostId, Event(runId, 2, RemoteEventKind.Progress));
        await Service.PublishAsync(HostId, Event(runId, 9, RemoteEventKind.Progress, "after a gap"));

        Assert.Equal(9, await database.ScalarLongAsync(
            $"SELECT applied_sequence FROM runs WHERE id = '{runId}'"));
    }

    /// <summary>A terminal run is never reopened - by a NEW event, which this is.</summary>
    [Fact]
    public async Task A_run_that_has_ended_is_not_reopened()
    {
        var runId = await RunningRunAsync();
        await Service.PublishAsync(HostId, Event(runId, 2, RemoteEventKind.Failed, "it broke"));

        var refused = await RefusedAsync(() =>
            Service.PublishAsync(HostId, Event(runId, 3, RemoteEventKind.Progress)));

        Assert.Equal(FaultCode.RunEnded, refused.Code);
    }

    // ── transitions ─────────────────────────────────────────────────────────

    /// <summary>A run that never started cannot have finished the work.</summary>
    [Fact]
    public async Task A_queued_run_cannot_report_that_it_completed()
    {
        var runId = await QueuedRunAsync();

        var refused = await RefusedAsync(() =>
            Service.PublishAsync(HostId, Event(runId, 1, RemoteEventKind.Completed, "done")));

        Assert.Equal(FaultCode.InvalidTransition, refused.Code);
    }

    /// <summary>
    /// And the counterpart, which is what stops the rule above from being "a queued run may not
    /// end". A start command that was accepted and then lost to a crash ends exactly here, and a
    /// gateway that refused it would leave the run Queued for ever.
    /// </summary>
    [Fact]
    public async Task A_queued_run_may_report_that_it_was_interrupted()
    {
        var runId = await QueuedRunAsync();

        await Service.PublishAsync(HostId, Event(runId, 1, RemoteEventKind.Interrupted, "app closed"));

        Assert.Equal(RemoteRunStatus.Interrupted, await StatusAsync(runId));
    }

    /// <summary>
    /// The stop request and the start crossed in flight. The run really is running, and the owner
    /// really did ask it to stop, so the asked-to-stop state is what the panel keeps showing until
    /// the Host reports that it actually stopped.
    /// </summary>
    [Fact]
    public async Task A_run_asked_to_stop_keeps_saying_so_when_it_reports_that_it_started()
    {
        var runId = await QueuedRunAsync();
        await database.ExecuteAsync(
            $"UPDATE runs SET status = 'CancelRequested' WHERE id = '{runId}'");

        await Service.PublishAsync(HostId, Event(runId, 1, RemoteEventKind.Running));

        Assert.Equal(RemoteRunStatus.CancelRequested, await StatusAsync(runId));
    }

    // ── approvals ───────────────────────────────────────────────────────────

    /// <summary>An answer has to be about the action the request was raised for.</summary>
    [Fact]
    public async Task An_answer_about_a_different_action_is_refused()
    {
        var runId = await RunningRunAsync();
        var approvalId = NewId();

        await Service.PublishAsync(HostId, Event(runId, 2, RemoteEventKind.ApprovalRequested,
            "run the tests", approval: Request(approvalId, "the-real-hash")));

        var refused = await RefusedAsync(() => Service.PublishAsync(HostId,
            Event(runId, 3, RemoteEventKind.ApprovalResolved,
                resolution: new ApprovalResolution(approvalId, "a-different-hash", ApprovalOutcome.Allowed))));

        Assert.Equal(FaultCode.ActionHashMismatch, refused.Code);
    }

    [Fact]
    public async Task An_approval_is_not_resolved_twice()
    {
        var runId = await RunningRunAsync();
        var approvalId = NewId();
        var resolution = new ApprovalResolution(approvalId, "hash-1", ApprovalOutcome.Allowed);

        await Service.PublishAsync(HostId, Event(runId, 2, RemoteEventKind.ApprovalRequested,
            "run the tests", approval: Request(approvalId)));
        await Service.PublishAsync(HostId,
            Event(runId, 3, RemoteEventKind.ApprovalResolved, resolution: resolution));

        var refused = await RefusedAsync(() => Service.PublishAsync(HostId,
            Event(runId, 4, RemoteEventKind.ApprovalResolved, resolution: resolution)));

        Assert.Equal(FaultCode.ApprovalAlreadyResolved, refused.Code);
    }

    /// <summary>
    /// The run ended while the owner was still being asked. Their answer can no longer be applied
    /// to anything, and a panel that kept offering the button would be offering to authorise an
    /// action that cannot happen.
    /// </summary>
    [Fact]
    public async Task Ending_a_run_invalidates_what_it_was_still_asking()
    {
        var runId = await RunningRunAsync();
        var approvalId = NewId();

        await Service.PublishAsync(HostId, Event(runId, 2, RemoteEventKind.ApprovalRequested,
            "run the tests", approval: Request(approvalId)));
        await Service.PublishAsync(HostId, Event(runId, 3, RemoteEventKind.Cancelled, "stopped"));

        Assert.Equal("Invalidated", (await database.StringsAsync(
            $"SELECT status FROM approvals WHERE id = '{approvalId}'")).Single());
    }

    /// <summary>Answered the last one, so the run is working again rather than waiting.</summary>
    [Fact]
    public async Task Answering_the_last_request_puts_the_run_back_to_work()
    {
        var runId = await RunningRunAsync();
        var approvalId = NewId();

        await Service.PublishAsync(HostId, Event(runId, 2, RemoteEventKind.ApprovalRequested,
            "run the tests", approval: Request(approvalId)));
        Assert.Equal(RemoteRunStatus.WaitingForUser, await StatusAsync(runId));

        await Service.PublishAsync(HostId, Event(runId, 3, RemoteEventKind.ApprovalResolved,
            resolution: new ApprovalResolution(approvalId, "hash-1", ApprovalOutcome.Denied)));

        Assert.Equal(RemoteRunStatus.Running, await StatusAsync(runId));
    }

    /// <summary>And it keeps waiting while anything else is still unanswered.</summary>
    [Fact]
    public async Task Answering_one_of_two_requests_leaves_the_run_waiting()
    {
        var runId = await RunningRunAsync();
        var first = NewId();
        var second = NewId();

        await Service.PublishAsync(HostId, Event(runId, 2, RemoteEventKind.ApprovalRequested,
            "first", approval: Request(first, "hash-a")));
        await Service.PublishAsync(HostId, Event(runId, 3, RemoteEventKind.ApprovalRequested,
            "second", approval: Request(second, "hash-b")));

        await Service.PublishAsync(HostId, Event(runId, 4, RemoteEventKind.ApprovalResolved,
            resolution: new ApprovalResolution(first, "hash-a", ApprovalOutcome.Allowed)));

        Assert.Equal(RemoteRunStatus.WaitingForUser, await StatusAsync(runId));
    }

    /// <summary>An ApprovalResolved with nothing in it is a bug on our side, and says so.</summary>
    [Fact]
    public async Task An_approval_event_with_no_approval_in_it_is_malformed()
    {
        var runId = await RunningRunAsync();

        var refused = await RefusedAsync(() =>
            Service.PublishAsync(HostId, Event(runId, 2, RemoteEventKind.ApprovalRequested, "nothing")));

        Assert.Equal(FaultCode.MalformedEvent, refused.Code);
        Assert.Equal(FaultDisposition.Drop, refused.ToContract().Disposition);
    }

    // ── who is allowed to say it ────────────────────────────────────────────

    /// <summary>
    /// Revocation has to bite at the next thing the Host does, not at its next reconnect. A device
    /// that is already connected when its credential is withdrawn is exactly the case revocation
    /// exists for.
    /// </summary>
    [Fact]
    public async Task A_revoked_host_cannot_publish()
    {
        var runId = await RunningRunAsync();
        await database.ExecuteAsync($"UPDATE hosts SET revoked = 1 WHERE id = '{HostId}'");

        try
        {
            var refused = await RefusedAsync(() =>
                Service.PublishAsync(HostId, Event(runId, 2, RemoteEventKind.Progress)));

            Assert.Equal(FaultCode.HostRevoked, refused.Code);
            Assert.Equal(FaultDisposition.Fatal, refused.ToContract().Disposition);
        }
        finally
        {
            await database.ExecuteAsync($"UPDATE hosts SET revoked = 0 WHERE id = '{HostId}'");
        }
    }

    /// <summary>
    /// One Host cannot speak about another's run. The run is found by id AND host, so to the wrong
    /// Host it simply does not exist - which is also the right thing to say, since telling it that
    /// the run exists elsewhere is more than it is entitled to know.
    /// </summary>
    [Fact]
    public async Task A_host_cannot_publish_about_another_hosts_run()
    {
        var runId = await RunningRunAsync();
        await QueuedRunAsync(OtherHostId);

        var refused = await RefusedAsync(() =>
            Service.PublishAsync(OtherHostId, Event(runId, 2, RemoteEventKind.Progress)));

        Assert.Equal(FaultCode.UnknownRun, refused.Code);
    }

    // ── sync and acknowledge ────────────────────────────────────────────────

    /// <summary>
    /// The list is complete each time and replaces what was stored. A workspace the Host no longer
    /// offers has to disappear, or the panel keeps offering to start tasks in a folder that was
    /// removed months ago.
    /// </summary>
    [Fact]
    public async Task Sync_replaces_the_workspace_list_rather_than_adding_to_it()
    {
        await QueuedRunAsync();

        await Service.SyncAsync(HostId, [new WorkspaceRef("a", "Alpha"), new WorkspaceRef("b", "Beta")]);
        await Service.SyncAsync(HostId, [new WorkspaceRef("b", "Beta renamed")]);

        var stored = await database.StringsAsync(
            $"SELECT CONCAT(workspace_id, '=', name) FROM host_workspaces WHERE host_id = '{HostId}'");

        Assert.Equal(["b=Beta renamed"], stored);
    }

    /// <summary>
    /// Acknowledging twice is a lost reply, not a mistake, and the second one has to succeed for
    /// the Host to be able to stop asking.
    /// </summary>
    [Fact]
    public async Task Acknowledging_a_command_twice_is_not_an_error()
    {
        await QueuedRunAsync();
        var commandId = Guid.NewGuid().ToString();
        var cancelPayload = RemoteJson.Serialize(new CancelRunPayload("x", ""));

        await database.ExecuteAsync($"""
            INSERT INTO commands (id, host_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES ('{commandId}', '{HostId}', 'CancelRun', '{cancelPayload}', SHA2('x', 256),
                    'PendingDelivery', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL 1 DAY)
            """);

        await Service.AcknowledgeAsync(HostId, commandId);
        await Service.AcknowledgeAsync(HostId, commandId);

        Assert.Equal("AcceptedByHost", (await database.StringsAsync(
            $"SELECT status FROM commands WHERE id = '{commandId}'")).Single());
    }

    /// <summary>
    /// A start nobody ever accepted. The run is reported Incomplete rather than left Queued for
    /// ever: an absence is not an answer, and "still queued, three weeks later" is an absence
    /// dressed as a state.
    /// </summary>
    [Fact]
    public async Task A_start_command_that_expired_undelivered_makes_its_run_incomplete()
    {
        var runId = await QueuedRunAsync();
        var commandId = Guid.NewGuid().ToString();
        var payload = RemoteJson.Serialize(new StartTaskPayload(
            runId, "task", "workspace-1", "e1:sealed-task", "e1:sealed-start"));

        await database.ExecuteAsync($"""
            INSERT INTO commands (id, host_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES ('{commandId}', '{HostId}', 'StartTask', '{payload}', SHA2('{commandId}', 256),
                    'PendingDelivery', UTC_TIMESTAMP(3) - INTERVAL 2 DAY, UTC_TIMESTAMP(3) - INTERVAL 1 DAY)
            """);

        await Service.SyncAsync(HostId, []);

        Assert.Equal(RemoteRunStatus.Incomplete, await StatusAsync(runId));
        Assert.Equal("Expired", (await database.StringsAsync(
            $"SELECT status FROM commands WHERE id = '{commandId}'")).Single());
    }
}
