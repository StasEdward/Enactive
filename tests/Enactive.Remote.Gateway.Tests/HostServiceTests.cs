namespace Enactive.Remote.Gateway.Tests;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Enactive.Remote.Contracts;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// The state machine a Host drives, within its own account and on its own runs only.
///
/// <para>Each test names a refusal or a transition the gateway has to get right for a Host with a
/// durable outbox to be able to make progress. Where a refusal is asserted, the FAULT CODE is what
/// is asserted and not the message: the code is what the Host classifies, and a message is for a
/// person reading a log.</para>
///
/// <para>Every test makes its own person and computer, through the real registration path, so no
/// test can disturb another's rows - and the person who owns the computer is the one whose account
/// every row it writes must land in.</para>
/// </summary>
public sealed class HostServiceTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private static readonly TimeSpan Generously = TimeSpan.FromSeconds(5);

    private Database Db => new(database.ConnectionString);

    private HostService Service => new(Db);

    private UserService Users => new(Db, Limits.Unlimited, TimeProvider.System);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private Task<UserAccess> PersonAsync(string stem)
        => TestAccounts.CreateAsync(database, stem + NewId()[..8]);

    /// <summary>
    /// A field as the Host seals it. The gateway checks only the shape - it has no key - so any key
    /// and any associated data will do.
    /// </summary>
    private static string Sealed(string text)
        => Envelope.Seal(RandomNumberGenerator.GetBytes(32), 1, Encoding.UTF8.GetBytes(text), []);

    /// <summary>A computer of <paramref name="user"/>'s, as the hub sees it once its token checks out.</summary>
    private async Task<HostAccess> ComputerAsync(UserAccess user, string label = "Studio PC")
        => (await ComputerWithTokenAsync(user, label)).Host;

    private async Task<(HostAccess Host, string Token)> ComputerWithTokenAsync(
        UserAccess user, string label = "Studio PC")
    {
        var (id, _, token) = await Users.RegisterHostAsync(user, label, default);
        return (new HostAccess(id, user.UserId), token);
    }

    /// <summary>A task and a queued run of it on <paramref name="host"/>, as a person's start leaves them.</summary>
    private async Task<string> QueuedRunAsync(HostAccess host)
    {
        var taskId = Guid.NewGuid().ToString();
        var runId = NewId();

        await database.ExecuteAsync(
            """
            INSERT INTO tasks (owner_id, id, host_id, workspace_id, sealed, fingerprint, created_at)
              VALUES (@owner, @task, @host, 'workspace-1', @sealed, SHA2(@task, 256), UTC_TIMESTAMP(3));
            INSERT INTO runs (id, owner_id, task_id, host_id, status, applied_sequence, created_at)
              VALUES (@run, @owner, @task, @host, 'Queued', 0, UTC_TIMESTAMP(3));
            """,
            ("@owner", host.OwnerId), ("@task", taskId), ("@host", host.HostId),
            ("@sealed", Sealed("Run the tests")), ("@run", runId));

        return runId;
    }

    /// <summary>A fresh person's computer with a queued run on it.</summary>
    private async Task<(HostAccess Host, string RunId)> QueuedAsync(string person = "alice")
    {
        var host = await ComputerAsync(await PersonAsync(person));
        return (host, await QueuedRunAsync(host));
    }

    /// <summary>A run that has already reported it started, which is where most of these begin.</summary>
    private async Task<(HostAccess Host, string RunId)> RunningAsync(string person = "alice")
    {
        var (host, runId) = await QueuedAsync(person);
        await Service.PublishAsync(host, Event(runId, 1, RemoteEventKind.Running));
        return (host, runId);
    }

    /// <summary>An event whose detail, if it has one, is sealed as the Host seals it.</summary>
    private static HostEvent Event(
        string runId, long sequence, RemoteEventKind kind,
        string? text = null, ApprovalRequest? approval = null, ApprovalResolution? resolution = null)
        => new(NewId(), runId, sequence, kind, text is null ? null : Sealed(text), approval, resolution);

    private static ApprovalRequest Request(string id, string hash = "hash-1", bool remoteDecidable = true)
        => new(id, "call-1", hash, remoteDecidable, Sealed("dotnet test"));

    private async Task<RemoteRunStatus> StatusAsync(string runId)
        => Enum.Parse<RemoteRunStatus>(
            (await database.StringsAsync($"SELECT status FROM runs WHERE id = '{runId}'")).Single());

    private async Task<string> CommandAsync(HostAccess host, CommandKind kind, string payload, bool expired = false)
    {
        var commandId = Guid.NewGuid().ToString();

        await database.ExecuteAsync(
            $"""
            INSERT INTO commands (owner_id, id, host_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES (@owner, @id, @host, @kind, @payload, SHA2(@id, 256), 'PendingDelivery',
                    UTC_TIMESTAMP(3) - INTERVAL {(expired ? 2 : 0)} DAY,
                    UTC_TIMESTAMP(3) + INTERVAL {(expired ? -1 : 1)} DAY)
            """,
            ("@owner", host.OwnerId), ("@id", commandId), ("@host", host.HostId),
            ("@kind", kind.ToString()), ("@payload", payload));

        return commandId;
    }

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
        var (host, runId) = await RunningAsync();
        var progress = Event(runId, 2, RemoteEventKind.Progress, "reading files");

        await Service.PublishAsync(host, progress);
        await Service.PublishAsync(host, progress);

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
        var (host, runId) = await RunningAsync();
        var completed = Event(runId, 2, RemoteEventKind.Completed, "all done");

        await Service.PublishAsync(host, completed);
        await Service.PublishAsync(host, completed);

        Assert.Equal(RemoteRunStatus.Completed, await StatusAsync(runId));
    }

    /// <summary>Behind by a number, so it has been applied already.</summary>
    [Fact]
    public async Task An_event_that_is_not_ahead_of_what_was_applied_is_refused()
    {
        var (host, runId) = await RunningAsync();
        await Service.PublishAsync(host, Event(runId, 5, RemoteEventKind.Progress));

        var refused = await RefusedAsync(() =>
            Service.PublishAsync(host, Event(runId, 5, RemoteEventKind.Progress)));

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
        var (host, runId) = await RunningAsync();

        await Service.PublishAsync(host, Event(runId, 2, RemoteEventKind.Progress));
        await Service.PublishAsync(host, Event(runId, 9, RemoteEventKind.Progress, "after a gap"));

        Assert.Equal(9, await database.ScalarLongAsync(
            $"SELECT applied_sequence FROM runs WHERE id = '{runId}'"));
    }

    /// <summary>A terminal run is never reopened - by a NEW event, which this is.</summary>
    [Fact]
    public async Task A_run_that_has_ended_is_not_reopened()
    {
        var (host, runId) = await RunningAsync();
        await Service.PublishAsync(host, Event(runId, 2, RemoteEventKind.Failed, "it broke"));

        var refused = await RefusedAsync(() =>
            Service.PublishAsync(host, Event(runId, 3, RemoteEventKind.Progress)));

        Assert.Equal(FaultCode.RunEnded, refused.Code);
    }

    // ── transitions ─────────────────────────────────────────────────────────

    /// <summary>A run that never started cannot have finished the work.</summary>
    [Fact]
    public async Task A_queued_run_cannot_report_that_it_completed()
    {
        var (host, runId) = await QueuedAsync();

        var refused = await RefusedAsync(() =>
            Service.PublishAsync(host, Event(runId, 1, RemoteEventKind.Completed, "done")));

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
        var (host, runId) = await QueuedAsync();

        await Service.PublishAsync(host, Event(runId, 1, RemoteEventKind.Interrupted, "app closed"));

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
        var (host, runId) = await QueuedAsync();
        await database.ExecuteAsync(
            $"UPDATE runs SET status = 'CancelRequested' WHERE id = '{runId}'");

        await Service.PublishAsync(host, Event(runId, 1, RemoteEventKind.Running));

        Assert.Equal(RemoteRunStatus.CancelRequested, await StatusAsync(runId));
    }

    // ── what is stored, and how the panel opens it ──────────────────────────

    /// <summary>
    /// The run's summary is the terminal event's own envelope, and the gateway cannot write another:
    /// it has no key. The envelope only opens under that event's associated data - run, sequence and
    /// kind - so the run keeps the sequence beside it, and the notice keeps the sequence and the kind.
    /// Without them the panel holds a sealed summary it can never open.
    /// </summary>
    [Fact]
    public async Task The_summary_names_the_event_it_was_copied_from()
    {
        var (host, runId) = await RunningAsync();
        var envelope = Sealed("All 412 tests pass.");

        await Service.PublishAsync(host, new HostEvent(NewId(), runId, 7, RemoteEventKind.Completed, envelope));

        Assert.Equal($"{envelope}|7", Assert.Single(await database.StringsAsync(
            $"SELECT CONCAT(sealed_summary, '|', summary_sequence) FROM runs WHERE id = '{runId}'")));
        Assert.Equal($"Completed|{envelope}|7|Completed", Assert.Single(await database.StringsAsync(
            $"""
            SELECT CONCAT(kind, '|', sealed_detail, '|', event_sequence, '|', event_kind)
            FROM notices WHERE run_id = '{runId}' AND owner_id = '{host.OwnerId}'
            """)));
        Assert.Equal(envelope, Assert.Single(await database.StringsAsync(
            $"SELECT sealed_detail FROM events WHERE run_id = '{runId}' AND sequence = 7")));
    }

    /// <summary>
    /// A permission request is stored as the computer sealed it, and only what routes it is in the
    /// clear: the tool call it belongs to, the hash an answer must carry, and whether it may be
    /// answered from the web at all. Its notice says which of the two kinds of request it is in the
    /// gateway's own words, and carries the event's envelope with what opens it.
    /// </summary>
    [Theory]
    [InlineData(true, "PermissionRequested")]
    [InlineData(false, "PermissionAtComputer")]
    public async Task A_permission_request_is_stored_sealed_and_announced_in_the_gateways_words(
        bool remoteDecidable, string noticeKind)
    {
        var (host, runId) = await RunningAsync();
        var approvalId = NewId();
        var request = new ApprovalRequest(approvalId, "call-7", "hash-7", remoteDecidable, Sealed("dotnet test"));
        var detail = Sealed("Run the tests?");

        await Service.PublishAsync(host,
            new HostEvent(NewId(), runId, 2, RemoteEventKind.ApprovalRequested, detail, request));

        Assert.Equal(
            $"{host.OwnerId}|call-7|hash-7|{(remoteDecidable ? 1 : 0)}|{request.SealedAction}|Pending",
            Assert.Single(await database.StringsAsync(
                $"""
                SELECT CONCAT(owner_id, '|', tool_call_id, '|', action_hash, '|', remote_decidable, '|',
                              sealed_action, '|', status)
                FROM approvals WHERE host_id = '{host.HostId}' AND id = '{approvalId}'
                """)));
        Assert.Equal($"{noticeKind}|{detail}|2|ApprovalRequested", Assert.Single(await database.StringsAsync(
            $"""
            SELECT CONCAT(kind, '|', sealed_detail, '|', event_sequence, '|', event_kind)
            FROM notices WHERE run_id = '{runId}'
            """)));
    }

    /// <summary>
    /// Something that should be sealed and is not even an envelope is plaintext that was never meant
    /// to reach the gateway, or garbage. Neither becomes storable on a second attempt, so the refusal
    /// carries a Drop code - a Retry would have the Host send it every fifteen seconds for ever - and
    /// nothing of it is kept.
    /// </summary>
    [Fact]
    public async Task A_malformed_envelope_is_dropped_not_retried()
    {
        var (host, runId) = await RunningAsync();

        var plainDetail = await RefusedAsync(() => Service.PublishAsync(host,
            new HostEvent(NewId(), runId, 2, RemoteEventKind.Progress, "reading C:\\clients\\acme")));
        var plainAction = await RefusedAsync(() => Service.PublishAsync(host,
            new HostEvent(NewId(), runId, 2, RemoteEventKind.ApprovalRequested, Sealed("Run?"),
                new ApprovalRequest(NewId(), "call-1", "hash-1", true, "{\"tool\":\"run_command\"}"))));
        var oversized = await RefusedAsync(() => Service.PublishAsync(host,
            Event(runId, 2, RemoteEventKind.Progress, new string('x', 64_000))));
        var plainName = await RefusedAsync(() =>
            Service.SyncAsync(host, [new WorkspaceRef("workspace-1", "Acme invoices")]));

        foreach (var refused in new[] { plainDetail, plainAction, oversized, plainName })
        {
            Assert.Equal(FaultCode.EnvelopeMalformed, refused.Code);
            Assert.Equal(FaultDisposition.Drop, RemoteFaults.DispositionOf(refused.Code));
        }

        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT applied_sequence FROM runs WHERE id = '{runId}'"));
        Assert.Equal(0, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM approvals WHERE run_id = '{runId}'"));
        Assert.Equal(0, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM host_workspaces WHERE host_id = '{host.HostId}'"));
    }

    // ── approvals ───────────────────────────────────────────────────────────

    /// <summary>An answer has to be about the action the request was raised for.</summary>
    [Fact]
    public async Task An_answer_about_a_different_action_is_refused()
    {
        var (host, runId) = await RunningAsync();
        var approvalId = NewId();

        await Service.PublishAsync(host, Event(runId, 2, RemoteEventKind.ApprovalRequested,
            "run the tests", approval: Request(approvalId, "the-real-hash")));

        var refused = await RefusedAsync(() => Service.PublishAsync(host,
            Event(runId, 3, RemoteEventKind.ApprovalResolved,
                resolution: new ApprovalResolution(approvalId, "a-different-hash", ApprovalOutcome.Allowed))));

        Assert.Equal(FaultCode.ActionHashMismatch, refused.Code);
    }

    [Fact]
    public async Task An_approval_is_not_resolved_twice()
    {
        var (host, runId) = await RunningAsync();
        var approvalId = NewId();
        var resolution = new ApprovalResolution(approvalId, "hash-1", ApprovalOutcome.Allowed);

        await Service.PublishAsync(host, Event(runId, 2, RemoteEventKind.ApprovalRequested,
            "run the tests", approval: Request(approvalId)));
        await Service.PublishAsync(host,
            Event(runId, 3, RemoteEventKind.ApprovalResolved, resolution: resolution));

        var refused = await RefusedAsync(() => Service.PublishAsync(host,
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
        var (host, runId) = await RunningAsync();
        var approvalId = NewId();

        await Service.PublishAsync(host, Event(runId, 2, RemoteEventKind.ApprovalRequested,
            "run the tests", approval: Request(approvalId)));
        await Service.PublishAsync(host, Event(runId, 3, RemoteEventKind.Cancelled, "stopped"));

        Assert.Equal("Invalidated", (await database.StringsAsync(
            $"SELECT status FROM approvals WHERE id = '{approvalId}'")).Single());
    }

    /// <summary>Answered the last one, so the run is working again rather than waiting.</summary>
    [Fact]
    public async Task Answering_the_last_request_puts_the_run_back_to_work()
    {
        var (host, runId) = await RunningAsync();
        var approvalId = NewId();

        await Service.PublishAsync(host, Event(runId, 2, RemoteEventKind.ApprovalRequested,
            "run the tests", approval: Request(approvalId)));
        Assert.Equal(RemoteRunStatus.WaitingForUser, await StatusAsync(runId));

        await Service.PublishAsync(host, Event(runId, 3, RemoteEventKind.ApprovalResolved,
            resolution: new ApprovalResolution(approvalId, "hash-1", ApprovalOutcome.Denied)));

        Assert.Equal(RemoteRunStatus.Running, await StatusAsync(runId));
    }

    /// <summary>And it keeps waiting while anything else is still unanswered.</summary>
    [Fact]
    public async Task Answering_one_of_two_requests_leaves_the_run_waiting()
    {
        var (host, runId) = await RunningAsync();
        var first = NewId();
        var second = NewId();

        await Service.PublishAsync(host, Event(runId, 2, RemoteEventKind.ApprovalRequested,
            "first", approval: Request(first, "hash-a")));
        await Service.PublishAsync(host, Event(runId, 3, RemoteEventKind.ApprovalRequested,
            "second", approval: Request(second, "hash-b")));

        await Service.PublishAsync(host, Event(runId, 4, RemoteEventKind.ApprovalResolved,
            resolution: new ApprovalResolution(first, "hash-a", ApprovalOutcome.Allowed)));

        Assert.Equal(RemoteRunStatus.WaitingForUser, await StatusAsync(runId));
    }

    /// <summary>An ApprovalRequested with nothing in it is a bug on our side, and says so.</summary>
    [Fact]
    public async Task An_approval_event_with_no_approval_in_it_is_malformed()
    {
        var (host, runId) = await RunningAsync();

        var refused = await RefusedAsync(() =>
            Service.PublishAsync(host, Event(runId, 2, RemoteEventKind.ApprovalRequested, "nothing")));

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
        var (host, runId) = await RunningAsync();
        await database.ExecuteAsync($"UPDATE hosts SET revoked = 1 WHERE id = '{host.HostId}'");

        var refused = await RefusedAsync(() =>
            Service.PublishAsync(host, Event(runId, 2, RemoteEventKind.Progress)));

        Assert.Equal(FaultCode.HostRevoked, refused.Code);
        Assert.Equal(FaultDisposition.Fatal, refused.ToContract().Disposition);
    }

    /// <summary>
    /// A publish that arrives while its computer is being revoked waits for the revocation and is
    /// then refused. Read without a lock, the computer looked live to the publish's snapshot, and an
    /// event was applied after the person had withdrawn the computer's credential.
    /// </summary>
    [Fact]
    public async Task A_publish_racing_a_revocation_waits_for_it_and_is_refused()
    {
        var (host, runId) = await RunningAsync();

        // What RevokeHostAsync does, held open: the host row is locked and marked revoked.
        await using var connection = await database.OpenAsync();
        await using var revoking = await connection.BeginAsync(default);
        await connection.ExecuteAsync(revoking,
            "UPDATE hosts SET revoked = 1 WHERE id = @host", ("@host", host.HostId));

        var publish = Service.PublishAsync(host, Event(runId, 2, RemoteEventKind.Progress));
        await Assert.ThrowsAsync<TimeoutException>(() => publish.WaitAsync(TimeSpan.FromMilliseconds(500)));

        await revoking.CommitAsync();

        var refused = await Assert.ThrowsAsync<GatewayFault>(() => publish.WaitAsync(Generously));
        Assert.Equal(FaultCode.HostRevoked, refused.Code);
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT applied_sequence FROM runs WHERE id = '{runId}'"));
    }

    /// <summary>
    /// The same for the account. A publish that arrives while its owner's account is being disabled
    /// waits for the disablement and is then refused. Read without a lock, the account looked active
    /// to the publish's snapshot, and an event was applied after the account had been disabled.
    /// </summary>
    [Fact]
    public async Task A_publish_racing_a_disablement_waits_for_it_and_is_refused()
    {
        var (host, runId) = await RunningAsync();

        // A disablement, held open: the account's row is locked and marked disabled.
        await using var connection = await database.OpenAsync();
        await using var disabling = await connection.BeginAsync(default);
        await connection.ExecuteAsync(disabling,
            "UPDATE users SET status = 'Disabled' WHERE id = @owner", ("@owner", host.OwnerId));

        var publish = Service.PublishAsync(host, Event(runId, 2, RemoteEventKind.Progress));
        await Assert.ThrowsAsync<TimeoutException>(() => publish.WaitAsync(TimeSpan.FromMilliseconds(500)));

        await disabling.CommitAsync();

        var refused = await Assert.ThrowsAsync<GatewayFault>(() => publish.WaitAsync(Generously));
        Assert.Equal(FaultCode.AccountDisabled, refused.Code);
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT applied_sequence FROM runs WHERE id = '{runId}'"));
    }

    /// <summary>
    /// A disabled account's computers stop at the next thing they do, whatever their connection was
    /// told when it opened. The code is a Fatal one: the Host stops and keeps its queue, because
    /// nothing it can send will be accepted until a person changes the account.
    /// </summary>
    [Fact]
    public async Task A_disabled_account_stops_its_computers_at_the_next_call()
    {
        var (host, runId) = await RunningAsync();
        var commandId = await CommandAsync(host, CommandKind.CancelRun,
            RemoteJson.Serialize(new CancelRunPayload(runId, Sealed("cancel"))));

        await database.ExecuteAsync($"UPDATE users SET status = 'Disabled' WHERE id = '{host.OwnerId}'");

        var refusals = new[]
        {
            await RefusedAsync(() => Service.PublishAsync(host, Event(runId, 2, RemoteEventKind.Progress))),
            await RefusedAsync(() => Service.SyncAsync(host, [])),
            await RefusedAsync(() => Service.AcknowledgeAsync(host, commandId))
        };

        foreach (var refused in refusals)
        {
            Assert.Equal(FaultCode.AccountDisabled, refused.Code);
            Assert.Equal(FaultDisposition.Fatal, refused.ToContract().Disposition);
        }

        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT applied_sequence FROM runs WHERE id = '{runId}'"));
        Assert.Equal("PendingDelivery", Assert.Single(
            await database.StringsAsync($"SELECT status FROM commands WHERE id = '{commandId}'")));
    }

    /// <summary>
    /// Alice's computer cannot speak about Bob's run. The run is found by owner, computer and id, so
    /// to Alice's computer it simply does not exist - and the refusal is the one for an id nobody
    /// has, because telling her computer that the run exists elsewhere is more than it may know.
    /// </summary>
    [Fact]
    public async Task Alices_computer_cannot_publish_to_bobs_run()
    {
        var alices = await ComputerAsync(await PersonAsync("alice"));
        var (_, bobsRun) = await RunningAsync("bob");
        var missing = NewId();

        var forBobs = await RefusedAsync(() =>
            Service.PublishAsync(alices, Event(bobsRun, 2, RemoteEventKind.Completed, "done")));
        var forMissing = await RefusedAsync(() =>
            Service.PublishAsync(alices, Event(missing, 2, RemoteEventKind.Completed, "done")));

        Assert.Equal(FaultCode.UnknownRun, forBobs.Code);
        Assert.Equal(
            (forMissing.Code, forMissing.Status, forMissing.Message.Replace(missing, "<id>")),
            (forBobs.Code, forBobs.Status, forBobs.Message.Replace(bobsRun, "<id>")));
        Assert.Equal(RemoteRunStatus.Running, await StatusAsync(bobsRun));
        Assert.Equal(1, await database.ScalarLongAsync($"SELECT COUNT(*) FROM events WHERE run_id = '{bobsRun}'"));
    }

    /// <summary>
    /// One person's two computers are two Hosts. The laptop cannot report on a run of the desktop's,
    /// even though both are Alice's: each one's outbox speaks only for what it is running itself.
    /// </summary>
    [Fact]
    public async Task A_computer_cannot_publish_to_another_computer_of_the_same_owner()
    {
        var alice = await PersonAsync("alice");
        var desktop = await ComputerAsync(alice, "Desktop");
        var laptop = await ComputerAsync(alice, "Laptop");
        var desktopsRun = await QueuedRunAsync(desktop);

        var refused = await RefusedAsync(() =>
            Service.PublishAsync(laptop, Event(desktopsRun, 1, RemoteEventKind.Running)));

        Assert.Equal(FaultCode.UnknownRun, refused.Code);
        Assert.Equal(RemoteRunStatus.Queued, await StatusAsync(desktopsRun));
    }

    /// <summary>
    /// A command is acknowledged by the computer it was sent to. Bob's command, or one meant for
    /// Alice's other computer, is not this computer's to accept: accepting it would mark it delivered
    /// to a machine that never received it, and the one it was for would never be given it.
    /// </summary>
    [Fact]
    public async Task A_computer_acknowledges_only_its_own_commands()
    {
        var alice = await PersonAsync("alice");
        var desktop = await ComputerAsync(alice, "Desktop");
        var laptop = await ComputerAsync(alice, "Laptop");
        var (bobs, bobsRun) = await QueuedAsync("bob");
        var payload = RemoteJson.Serialize(new CancelRunPayload(bobsRun, Sealed("cancel")));

        var bobsCommand = await CommandAsync(bobs, CommandKind.CancelRun, payload);
        var laptopsCommand = await CommandAsync(laptop, CommandKind.CancelRun, payload);

        Assert.Equal(404, (await RefusedAsync(() => Service.AcknowledgeAsync(desktop, bobsCommand))).Status);
        Assert.Equal(404, (await RefusedAsync(() => Service.AcknowledgeAsync(desktop, laptopsCommand))).Status);

        Assert.Equal(["PendingDelivery", "PendingDelivery"], await database.StringsAsync(
            $"SELECT status FROM commands WHERE id IN ('{bobsCommand}', '{laptopsCommand}')"));
    }

    // ── another account's locks, and the order this takes them in ──────────

    /// <summary>
    /// Bob holds his run locked, as his own publish or cancel does while it runs, and Alice's
    /// computer names that run. It is refused at once. A lookup that locked Bob's row on Alice's
    /// behalf - filtered by owner, but found through the table's global key - made her computer wait
    /// for Bob: the wait told it the id exists, and its transaction held up his.
    /// </summary>
    [Fact]
    public async Task Alices_computer_does_not_wait_on_bobs_run()
    {
        var alices = await ComputerAsync(await PersonAsync("alice"));
        var (_, bobsRun) = await RunningAsync("bob");

        await using var connection = await database.OpenAsync();
        await using var bobsTransaction = await connection.BeginAsync(default);
        await connection.ExecuteAsync(bobsTransaction,
            "SELECT id FROM runs WHERE id = @id FOR UPDATE", ("@id", bobsRun));

        var publish = Service.PublishAsync(alices, Event(bobsRun, 2, RemoteEventKind.Progress));

        var refused = await Assert.ThrowsAsync<GatewayFault>(() => publish.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(FaultCode.UnknownRun, refused.Code);
    }

    /// <summary>
    /// A sync writes off this computer's expired commands, and locks only those. A locking scan that
    /// went through the expiry key - status and expiry time, every account's commands in one range -
    /// locked Bob's expired commands as well, and Alice's computer waited for whatever of Bob's held
    /// them. The scan no longer locks at all; this keeps it that way.
    ///
    /// <para>The optimizer takes that key when it looks cheapest: this computer has a long queue of
    /// commands that have not expired, and the whole gateway has only a few that have - which is
    /// what an offline computer looks like on a quiet day.</para>
    /// </summary>
    [Fact]
    public async Task Alices_sync_does_not_wait_on_bobs_expired_commands()
    {
        var alices = await ComputerAsync(await PersonAsync("alice"));
        var (bobs, bobsRun) = await QueuedAsync("bob");

        for (var i = 0; i < 50; i++)
        {
            await CommandAsync(alices, CommandKind.CancelRun,
                RemoteJson.Serialize(new CancelRunPayload(bobsRun, Sealed("cancel"))));
        }

        var bobsCommand = await CommandAsync(bobs, CommandKind.CancelRun,
            RemoteJson.Serialize(new CancelRunPayload(bobsRun, Sealed("cancel"))), expired: true);

        // What Bob's own sync does, held open: it writes off his expired command, which changes the
        // command's entry in the expiry key.
        await using var connection = await database.OpenAsync();
        await using var bobsTransaction = await connection.BeginAsync(default);
        await connection.ExecuteAsync(bobsTransaction,
            "UPDATE commands SET status = 'Expired' WHERE owner_id = @owner AND id = @id",
            ("@owner", bobs.OwnerId), ("@id", bobsCommand));

        await Service.SyncAsync(alices, []).WaitAsync(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// A sync holds the computer only shared while it reaches for the run of an expired start. A
    /// sync that locked the computer row EXCLUSIVELY - to mark it seen - held what a transaction
    /// holding that run and then reading the computer was waiting for, while waiting for the run: a
    /// deadlock, and one of the two rolled back by the database. A person's cancel took its locks
    /// in that order until it read the computer first; the transaction held here still does, so a
    /// sync that took the computer exclusively again is caught. Shared, the read is granted at once,
    /// the transaction finishes, and the sync goes on after it.
    /// </summary>
    [Fact]
    public async Task A_sync_does_not_deadlock_with_a_cancel_of_the_run_it_expires()
    {
        var (host, runId) = await QueuedAsync();
        await CommandAsync(host, CommandKind.StartTask, RemoteJson.Serialize(new StartTaskPayload(
            runId, Guid.NewGuid().ToString(), "workspace-1", Sealed("task"), Sealed("start"))), expired: true);

        // A run locked and held open, as a cancel locked it before it read the computer first.
        await using var connection = await database.OpenAsync();
        await using var cancelling = await connection.BeginAsync(default);
        await connection.ExecuteAsync(cancelling,
            "SELECT id FROM runs WHERE id = @run FOR UPDATE", ("@run", runId));

        var sync = Service.SyncAsync(host, []);
        await Assert.ThrowsAsync<TimeoutException>(() => sync.WaitAsync(TimeSpan.FromMilliseconds(500)));

        // ...and then the computer read, sharing the row.
        await connection.ExecuteAsync(cancelling,
            "SELECT revoked FROM hosts WHERE id = @host FOR SHARE", ("@host", host.HostId));
        await connection.ExecuteAsync(cancelling,
            "UPDATE runs SET status = 'CancelRequested' WHERE id = @run", ("@run", runId));
        await cancelling.CommitAsync();

        await sync.WaitAsync(Generously);

        // The cancel committed first, so the run was no longer Queued when the sync wrote off its
        // start: the run keeps saying a stop was asked for, and the start is still expired.
        Assert.Equal(RemoteRunStatus.CancelRequested, await StatusAsync(runId));
        Assert.Equal("Expired", Assert.Single(await database.StringsAsync(
            $"SELECT status FROM commands WHERE host_id = '{host.HostId}' AND kind = 'StartTask'")));
    }

    /// <summary>
    /// The real cancel, all the way to the command it queues. It reads the computer, locks the run,
    /// and inserts its command into this computer's delivery range. A sync that locked that range to
    /// find its expired commands and THEN reached for the expired start's run held the gap the
    /// cancel's insert needed while waiting for the run the cancel held: a deadlock, and the database
    /// rolled one of them back. A sync finds them without locking, locks the runs first, and only
    /// then each command by its own key, so the cancel inserts, commits, and the sync goes on.
    /// </summary>
    [Fact]
    public async Task A_sync_does_not_deadlock_with_a_cancel_queuing_its_command()
    {
        var (host, runId) = await QueuedAsync();
        await CommandAsync(host, CommandKind.StartTask, RemoteJson.Serialize(new StartTaskPayload(
            runId, Guid.NewGuid().ToString(), "workspace-1", Sealed("task"), Sealed("start"))), expired: true);
        var cancelId = Guid.NewGuid().ToString();

        // Holds the cancel at the right moment: after it has read the computer and locked the run,
        // before it queues its command. An uncommitted row under the cancel's own command id makes its
        // idempotency lookup wait. Accepted, so it is outside the range a sync delivers from.
        await using var connection = await database.OpenAsync();
        await using var holding = await connection.BeginAsync(default);
        await connection.ExecuteAsync(holding,
            """
            INSERT INTO commands (owner_id, id, host_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES (@owner, @id, @host, 'CancelRun', '{}', SHA2(@id, 256), 'AcceptedByHost',
                    UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL 1 DAY)
            """,
            ("@owner", host.OwnerId), ("@id", cancelId), ("@host", host.HostId));

        var alice = new UserAccess(host.OwnerId, Ids.New());
        var cancel = Users.CancelAsync(alice, runId, cancelId, Sealed("cancel"), default);
        await Assert.ThrowsAsync<TimeoutException>(() => cancel.WaitAsync(TimeSpan.FromMilliseconds(500)));

        var sync = Service.SyncAsync(host, []);
        await Assert.ThrowsAsync<TimeoutException>(() => sync.WaitAsync(TimeSpan.FromMilliseconds(500)));

        // Let the cancel go on to queue its command.
        await holding.RollbackAsync();

        Assert.Equal(CommandKind.CancelRun, (await cancel.WaitAsync(Generously)).Kind);
        await sync.WaitAsync(Generously);

        Assert.Equal(RemoteRunStatus.CancelRequested, await StatusAsync(runId));
        Assert.Equal("Expired", Assert.Single(await database.StringsAsync(
            $"SELECT status FROM commands WHERE host_id = '{host.HostId}' AND kind = 'StartTask'")));
    }

    /// <summary>
    /// An event caught in a deadlock is applied all the same. Without the retry the database's choice
    /// of victim reached the computer as a server error: its outbox retried the event, and after
    /// enough of them parked it - a run whose progress stopped on the panel for no reason of its own.
    /// </summary>
    [Fact]
    public async Task A_publish_caught_in_a_deadlock_is_retried_and_applied()
    {
        var (host, runId) = await RunningAsync();

        await using var connection = await database.OpenAsync();
        await using var other = await connection.BeginAsync(default);

        // The database rolls back the transaction that has written least. These rows make the other
        // transaction the heavier one, so the publish is the one chosen.
        for (var i = 0; i < 20; i++)
        {
            await connection.ExecuteAsync(other,
                "INSERT INTO audit (owner_id, at, actor, action) VALUES (NULL, UTC_TIMESTAMP(3), 'operator', 'test')");
        }

        // The owner's stream counter, held: the publish locks the run and then waits here, for the
        // number of its event.
        await connection.ExecuteAsync(other,
            "SELECT value FROM user_streams WHERE owner_id = @owner FOR UPDATE", ("@owner", host.OwnerId));

        var publish = Service.PublishAsync(host, Event(runId, 2, RemoteEventKind.Progress));
        await Assert.ThrowsAsync<TimeoutException>(() => publish.WaitAsync(TimeSpan.FromMilliseconds(500)));

        // ...and this reaches for the run the publish holds: each waits for the other.
        await connection.ExecuteAsync(other,
            "SELECT id FROM runs WHERE id = @run FOR UPDATE", ("@run", runId));
        await other.RollbackAsync();

        await publish.WaitAsync(Generously);
        Assert.Equal(2, await database.ScalarLongAsync(
            $"SELECT applied_sequence FROM runs WHERE id = '{runId}'"));
    }

    // ── signing in ──────────────────────────────────────────────────────────

    /// <summary>
    /// A computer's token says which computer it is AND whose. Both come from the computer's own row:
    /// the hub builds every call's access from these two claims, so neither may come from anything
    /// the computer sends. The host id stays the name identifier, which is what SignalR keys on.
    /// </summary>
    [Fact]
    public async Task A_computer_signs_in_as_itself_on_behalf_of_its_owner()
    {
        var alice = await PersonAsync("alice");
        var (host, token) = await ComputerWithTokenAsync(alice);

        var result = await SignInAsync(token);

        Assert.True(result.Succeeded);
        Assert.Equal(host.HostId, result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(alice.UserId, result.Principal!.FindFirstValue(HostAuthentication.OwnerClaim));
    }

    /// <summary>
    /// A disabled account's computer is refused when it connects, with the code the Host treats as
    /// final. The per-call check above still stands for a computer that connected before.
    /// </summary>
    [Fact]
    public async Task A_computer_of_a_disabled_account_cannot_sign_in()
    {
        var alice = await PersonAsync("alice");
        var (_, token) = await ComputerWithTokenAsync(alice);
        await database.ExecuteAsync($"UPDATE users SET status = 'Disabled' WHERE id = '{alice.UserId}'");

        var result = await SignInAsync(token);

        Assert.False(result.Succeeded);
        Assert.Equal(FaultCode.AccountDisabled, Assert.IsType<GatewayFault>(result.Failure).Code);
    }

    private async Task<AuthenticateResult> SignInAsync(string token)
    {
        var handler = new HostAuthentication(
            new SchemeOptions(), NullLoggerFactory.Instance, UrlEncoder.Default, Db);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + token;

        await handler.InitializeAsync(
            new AuthenticationScheme(HostAuthentication.SchemeName, null, typeof(HostAuthentication)), context);
        return await handler.AuthenticateAsync();
    }

    private sealed class SchemeOptions : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();

        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    /// <summary>
    /// The Host says which protocol it speaks before anything else, and a different one is refused
    /// in words a person can act on. Without the check, a Host of protocol 1 would have every sealed
    /// field it never sealed refused one by one, as if each of its events were malformed.
    /// </summary>
    [Fact]
    public async Task Another_protocol_is_refused_at_hello()
    {
        var hub = new HostHub(Service, new HostConnections());

        var older = await hub.Hello(RemoteProtocol.Version - 1);
        var same = await hub.Hello(RemoteProtocol.Version);

        Assert.NotNull(older.Fault);
        Assert.Equal(FaultCode.ProtocolMismatch, older.Fault.Code);
        Assert.Equal(FaultDisposition.Fatal, older.Fault.Disposition);
        Assert.Equal("This computer and the service speak different versions - update Enactive.", older.Fault.Message);
        Assert.Null(same.Fault);
    }

    // ── sync and acknowledge ────────────────────────────────────────────────

    /// <summary>
    /// The list is complete each time and replaces what was stored. A workspace the Host no longer
    /// offers has to disappear, or the panel keeps offering to start tasks in a folder that was
    /// removed months ago. The names are stored as they were sealed.
    /// </summary>
    [Fact]
    public async Task Sync_replaces_the_workspace_list_rather_than_adding_to_it()
    {
        var host = await ComputerAsync(await PersonAsync("alice"));
        var renamed = Sealed("Beta renamed");

        await Service.SyncAsync(host, [new WorkspaceRef("a", Sealed("Alpha")), new WorkspaceRef("b", Sealed("Beta"))]);
        await Service.SyncAsync(host, [new WorkspaceRef("b", renamed)]);

        var stored = await database.StringsAsync(
            $"SELECT CONCAT(owner_id, '|', workspace_id, '=', sealed_name) FROM host_workspaces WHERE host_id = '{host.HostId}'");

        Assert.Equal([$"{host.OwnerId}|b={renamed}"], stored);
    }

    /// <summary>
    /// A sync is what says the computer is there. It is marked seen only when the sync went through,
    /// so a computer whose every call is refused does not look online to its owner.
    /// </summary>
    [Fact]
    public async Task A_sync_marks_the_computer_seen()
    {
        var host = await ComputerAsync(await PersonAsync("alice"));

        await Service.SyncAsync(host, []);

        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM hosts WHERE id = '{host.HostId}' AND last_seen_at IS NOT NULL"));
    }

    /// <summary>
    /// Acknowledging twice is a lost reply, not a mistake, and the second one has to succeed for
    /// the Host to be able to stop asking.
    /// </summary>
    [Fact]
    public async Task Acknowledging_a_command_twice_is_not_an_error()
    {
        var (host, runId) = await QueuedAsync();
        var commandId = await CommandAsync(host, CommandKind.CancelRun,
            RemoteJson.Serialize(new CancelRunPayload(runId, Sealed("cancel"))));

        await Service.AcknowledgeAsync(host, commandId);
        await Service.AcknowledgeAsync(host, commandId);

        Assert.Equal("AcceptedByHost", (await database.StringsAsync(
            $"SELECT status FROM commands WHERE id = '{commandId}'")).Single());
    }

    /// <summary>
    /// A start nobody ever accepted. The run is reported Incomplete rather than left Queued for
    /// ever: an absence is not an answer, and "still queued, three weeks later" is an absence
    /// dressed as a state. The notice says only that it never started: the gateway has no key to seal
    /// a sentence with, so the words are the panel's.
    /// </summary>
    [Fact]
    public async Task A_start_command_that_expired_undelivered_makes_its_run_incomplete()
    {
        var (host, runId) = await QueuedAsync();
        var commandId = await CommandAsync(host, CommandKind.StartTask, RemoteJson.Serialize(new StartTaskPayload(
            runId, Guid.NewGuid().ToString(), "workspace-1", Sealed("task"), Sealed("start"))), expired: true);

        var delivered = await Service.SyncAsync(host, []);

        Assert.Empty(delivered);
        Assert.Equal(RemoteRunStatus.Incomplete, await StatusAsync(runId));
        Assert.Equal("Expired", (await database.StringsAsync(
            $"SELECT status FROM commands WHERE id = '{commandId}'")).Single());
        Assert.Equal(0, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM runs WHERE id = '{runId}' AND (sealed_summary IS NOT NULL OR summary_sequence IS NOT NULL)"));
        Assert.Equal("NotStarted|none", Assert.Single(await database.StringsAsync(
            $"""
            SELECT CONCAT(kind, '|', COALESCE(sealed_detail, event_sequence, event_kind, 'none'))
            FROM notices WHERE run_id = '{runId}' AND owner_id = '{host.OwnerId}'
            """)));
    }

    /// <summary>
    /// Two syncs of one computer at once - a reconnect overlapping the old connection's last call -
    /// both find the same expired start. One writes it off; the other waits for the run, finds there
    /// is nothing left to write off, and must not then hand the start to the computer. Its delivery
    /// read came from the snapshot taken before the wait, where the start was still undelivered, and
    /// the computer was given a start whose run had already been reported as never started.
    /// </summary>
    [Fact]
    public async Task Two_syncs_at_once_do_not_deliver_a_start_one_of_them_wrote_off()
    {
        var (host, runId) = await QueuedAsync();
        var start = await CommandAsync(host, CommandKind.StartTask, RemoteJson.Serialize(new StartTaskPayload(
            runId, Guid.NewGuid().ToString(), "workspace-1", Sealed("task"), Sealed("start"))), expired: true);

        // Something of the person's holding the run, so both syncs find the start and then wait.
        await using var connection = await database.OpenAsync();
        await using var holding = await connection.BeginAsync(default);
        await connection.ExecuteAsync(holding,
            "SELECT id FROM runs WHERE id = @run FOR UPDATE", ("@run", runId));

        var first = Service.SyncAsync(host, []);
        var second = Service.SyncAsync(host, []);
        await Assert.ThrowsAsync<TimeoutException>(
            () => Task.WhenAll(first, second).WaitAsync(TimeSpan.FromMilliseconds(500)));

        await holding.RollbackAsync();

        var delivered = (await Task.WhenAll(first, second).WaitAsync(Generously)).SelectMany(c => c);

        Assert.DoesNotContain(delivered, command => command.Id == start);
        Assert.Equal(RemoteRunStatus.Incomplete, await StatusAsync(runId));
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM notices WHERE run_id = '{runId}' AND kind = 'NotStarted'"));
    }

    /// <summary>
    /// An expired command is never handed over, whether or not a sync has written it off yet. One
    /// that became visible while this sync was waiting - after it had looked for expired commands -
    /// is still undelivered when the commands to deliver are read, and the computer would be given a
    /// command it must refuse as too old.
    /// </summary>
    [Fact]
    public async Task An_expired_command_is_never_delivered()
    {
        var (host, runId) = await QueuedAsync();
        await CommandAsync(host, CommandKind.StartTask, RemoteJson.Serialize(new StartTaskPayload(
            runId, Guid.NewGuid().ToString(), "workspace-1", Sealed("task"), Sealed("start"))), expired: true);

        await using var connection = await database.OpenAsync();
        await using var holding = await connection.BeginAsync(default);
        await connection.ExecuteAsync(holding,
            "SELECT id FROM runs WHERE id = @run FOR UPDATE", ("@run", runId));

        var sync = Service.SyncAsync(host, []);
        await Assert.ThrowsAsync<TimeoutException>(() => sync.WaitAsync(TimeSpan.FromMilliseconds(500)));

        // Committed while the sync waits, already past its expiry.
        var late = await CommandAsync(host, CommandKind.CancelRun,
            RemoteJson.Serialize(new CancelRunPayload(runId, Sealed("cancel"))), expired: true);

        await holding.RollbackAsync();

        Assert.DoesNotContain(await sync.WaitAsync(Generously), command => command.Id == late);
    }

    /// <summary>
    /// What a sync hands over: this computer's undelivered commands, and nobody else's - not Bob's,
    /// and not those of Alice's other computer, which are hers but are not this computer's to carry
    /// out. The second is what an owner filter alone would let through.
    /// </summary>
    [Fact]
    public async Task A_sync_delivers_only_this_computers_commands()
    {
        var (host, runId) = await QueuedAsync();
        var laptop = await ComputerAsync(new UserAccess(host.OwnerId, Ids.New()), "Laptop");
        var laptopsRun = await QueuedRunAsync(laptop);
        var (bobs, bobsRun) = await QueuedAsync("bob");
        var mine = await CommandAsync(host, CommandKind.CancelRun,
            RemoteJson.Serialize(new CancelRunPayload(runId, Sealed("cancel"))));
        await CommandAsync(laptop, CommandKind.CancelRun,
            RemoteJson.Serialize(new CancelRunPayload(laptopsRun, Sealed("cancel"))));
        await CommandAsync(bobs, CommandKind.CancelRun,
            RemoteJson.Serialize(new CancelRunPayload(bobsRun, Sealed("cancel"))));

        var delivered = await Service.SyncAsync(host, []);

        Assert.Equal(new[] { (mine, host.HostId) }, delivered.Select(c => (c.Id, c.HostId)));
    }
}
