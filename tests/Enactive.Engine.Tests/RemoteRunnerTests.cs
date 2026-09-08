namespace Enactive.Engine.Tests;

using System.Runtime.CompilerServices;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Orchestration;
using Enactive.Remote.Contracts;
using Enactive.Remote.Host;
using Xunit;

/// <summary>
/// The engine binding. Stage 4 of <c>Docs/REMOTE_DESIGN.md</c>.
///
/// <para>The claim being tested is narrow on purpose: a remote command becomes an ordinary Intent,
/// the events that Intent produces become queued remote events, and nothing about the engine
/// changes. So these use a fake orchestrator - what a real run does is what the other thousand
/// tests in this project are for, and putting one here would be testing the engine again through a
/// smaller window.</para>
/// </summary>
public sealed class RemoteRunnerTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "enactive-runner-" + Guid.NewGuid().ToString("N"));

    private HostStore Open() => new(Path.Combine(_folder, "remote.db"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Litter, not a failure.
        }
    }

    private static readonly StartTaskPayload Task1 = new(
        "run-1", "task-1", "workspace-1", "Run the tests", "Please run them.", DateTimeOffset.UtcNow);

    private static HostCommand Start(string commandId = "command-1", StartTaskPayload? task = null)
        => new(commandId, "host-1", CommandKind.StartTask, RemoteJson.Serialize(task ?? Task1),
            CommandStatus.PendingDelivery, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24));

    private static RemoteRunner Runner(HostStore store, IOrchestrator orchestrator)
        => new(store, new RemoteApprovals(), (task, _, _) => System.Threading.Tasks.Task.FromResult<
            (IOrchestrator, Intent)>((
            orchestrator,
            new Intent(
                Guid.NewGuid(), task.Prompt, IntentSource.Remote,
                new WorkContext(Guid.NewGuid(), task.WorkspaceId, null, null, null, [], []),
                DateTimeOffset.UtcNow))));

    private static WorkEvent Event(EventKind kind, string summary, string? payload = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, kind, summary, payload);

    /// <summary>Everything the run queued, in order, which is what the owner will eventually see.</summary>
    private static (RemoteEventKind Kind, string? Detail)[] Queued(HostStore store, string runId)
    {
        var events = new List<(RemoteEventKind, string?)>();

        // NextOwed hands back one event per run, so the queue is read by draining it.
        while (store.NextOwed().FirstOrDefault(o => o.RunId == runId) is { } owed)
        {
            events.Add((owed.Event.Kind, owed.Event.Detail));
            store.Discard(owed.EventId);
        }

        return events.ToArray();
    }

    // ── the mapping ─────────────────────────────────────────────────────────

    /// <summary>
    /// A kind added to the engine without a decision here would be silently dropped, which is the
    /// same defect as silently forwarding it - just quieter, and only noticed by somebody wondering
    /// why their phone never mentions the new thing.
    /// </summary>
    [Fact]
    public void Every_engine_event_kind_is_either_carried_or_deliberately_not()
        => Assert.Empty(Enum.GetValues<EventKind>().Except(EventMapping.Classified));

    /// <summary>
    /// The reason <see cref="RunOutcomeKind"/> exists, restated for the wire.
    ///
    /// <para>A run whose work a reviewer rejected reaches TaskCompleted. Reading the event KIND
    /// calls that a success - which is exactly the defect that type was introduced to end, and it
    /// would be reintroduced here if the mapping keyed off the kind.</para>
    /// </summary>
    [Fact]
    public void A_rejected_run_is_reported_failed_even_though_its_event_says_completed()
    {
        var ending = EventMapping.Ending(Event(
            EventKind.TaskCompleted, "Task finished",
            WorkEventPayload.OutcomePayload(RunOutcomeKind.Failed, "1 step(s) rejected by the reviewer")));

        Assert.Equal(RemoteEventKind.Failed, ending!.Value.Kind);
        Assert.Contains("rejected by the reviewer", ending.Value.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A terminal event that does not say how it ended is not assumed to have gone well. Guessing
    /// "completed" is guessing in the one direction that cannot be taken back.
    /// </summary>
    [Fact]
    public void A_terminal_event_that_does_not_say_how_it_ended_is_reported_unfinished()
    {
        var ending = EventMapping.Ending(Event(EventKind.TaskCompleted, "Task finished"));

        Assert.Equal(RemoteEventKind.Incomplete, ending!.Value.Kind);
        Assert.Contains("without recording how", ending.Value.Detail, StringComparison.Ordinal);
    }

    // ── through a run ───────────────────────────────────────────────────────

    /// <summary>
    /// What reaches the phone, and what does not. The tokens a model speaks and a command's whole
    /// output are the run's bulk - and the output is the part most likely to carry something out of
    /// the workspace that the gateway has no business holding.
    /// </summary>
    [Fact]
    public async Task A_run_reports_its_plan_and_its_steps_and_not_its_tokens()
    {
        using var store = Open();
        store.Accept(Start());

        await Runner(store, new FakeOrchestrator(
            Event(EventKind.AssistantDelta, "I"),
            Event(EventKind.AssistantDelta, " will"),
            Event(EventKind.PlanCreated, "Fix the parser - 2 steps"),
            Event(EventKind.StepStarted, "[1/2] Read the parser"),
            Event(EventKind.ToolResult, "<4000 lines of file>"),
            Event(EventKind.StepCompleted, "[1/2] done"),
            Event(EventKind.TaskCompleted, "All done",
                WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed))))
            .ApplyAsync(Start());

        Assert.Equal(
            [
                (RemoteEventKind.Running, "Started: Run the tests"),
                (RemoteEventKind.Progress, "Fix the parser - 2 steps"),
                (RemoteEventKind.Progress, "[1/2] Read the parser"),
                (RemoteEventKind.Progress, "[1/2] done"),
                (RemoteEventKind.Completed, "All done")
            ],
            Queued(store, "run-1"));
    }

    /// <summary>
    /// The whole reason the claim is written before the work starts. A redelivered start is the
    /// gateway doing exactly what at-least-once delivery means, and running the task again is not a
    /// recoverable mistake when the task writes files.
    /// </summary>
    [Fact]
    public async Task A_redelivered_start_does_not_run_the_task_twice()
    {
        using var store = Open();
        store.Accept(Start());

        var orchestrator = new FakeOrchestrator(
            Event(EventKind.TaskCompleted, "done", WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed)));
        var runner = Runner(store, orchestrator);

        await runner.ApplyAsync(Start());
        await runner.ApplyAsync(Start());

        Assert.Equal(1, orchestrator.Submissions);
    }

    /// <summary>
    /// A stop is asked for, and reported only once the run has actually stopped. External effects
    /// may already have happened, so a timeline that said "cancelled" the moment the button was
    /// pressed would be making that up.
    /// </summary>
    [Fact]
    public async Task A_cancelled_run_reports_it_only_after_the_engine_has_stopped()
    {
        using var store = Open();
        store.Accept(Start());

        var orchestrator = new FakeOrchestrator(Event(EventKind.StepStarted, "[1/1] Working")) { BlockAfterFirst = true };
        var runner = Runner(store, orchestrator);

        var running = runner.ApplyAsync(Start());
        await orchestrator.Reached.Task;

        runner.Cancel("run-1");
        await running;

        Assert.Equal(RemoteEventKind.Cancelled, Queued(store, "run-1").Last().Kind);
        Assert.True(orchestrator.SawCancellation);
    }

    /// <summary>
    /// A stream that stops without saying how. Not something the engine is supposed to do, and the
    /// answer is still not to guess: an unfinished run reported as unfinished is recoverable, one
    /// reported as complete is not.
    /// </summary>
    [Fact]
    public async Task A_run_whose_stream_ends_without_an_ending_is_reported_unfinished()
    {
        using var store = Open();
        store.Accept(Start());

        await Runner(store, new FakeOrchestrator(Event(EventKind.StepStarted, "[1/1] Working")))
            .ApplyAsync(Start());

        Assert.Equal(RemoteEventKind.Incomplete, Queued(store, "run-1").Last().Kind);
    }

    /// <summary>
    /// A run that throws does not get to sit open. A remote owner watching a status that never
    /// changes has no way to tell a long step from a dead process.
    /// </summary>
    [Fact]
    public async Task A_run_that_throws_is_reported_rather_than_left_open()
    {
        using var store = Open();
        store.Accept(Start());

        await Runner(store, new FakeOrchestrator { Throw = new InvalidOperationException("provider is down") })
            .ApplyAsync(Start());

        var last = Queued(store, "run-1").Last();

        Assert.Equal(RemoteEventKind.Failed, last.Kind);
        Assert.Contains("provider is down", last.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An answer for a request nobody is waiting on any more - the desktop got there first, or it
    /// expired. Doing nothing is correct and is not silence: the outcome that actually happened is
    /// already on its way as an ApprovalResolved event, so there is nothing to report or correct.
    /// </summary>
    [Fact]
    public async Task An_answer_for_a_request_nobody_is_waiting_on_is_harmless()
    {
        using var store = Open();
        var resolve = new HostCommand(
            "command-2", "host-1", CommandKind.ResolveApproval,
            RemoteJson.Serialize(new ResolveApprovalPayload("a1", "run-1", "call-1", "hash", RemoteDecision.Allow)),
            CommandStatus.PendingDelivery, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24));

        await Runner(store, new FakeOrchestrator()).ApplyAsync(resolve);
    }

    /// <summary>An orchestrator that emits what the test says and nothing else.</summary>
    private sealed class FakeOrchestrator(params WorkEvent[] events) : IOrchestrator
    {
        public int Submissions { get; private set; }

        public bool SawCancellation { get; private set; }

        /// <summary>Waits after the first event, so a test can cancel a run that is under way.</summary>
        public bool BlockAfterFirst { get; init; }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? Throw { get; init; }

        public async IAsyncEnumerable<WorkEvent> SubmitIntentAsync(
            Intent intent, [EnumeratorCancellation] CancellationToken ct)
        {
            Submissions++;

            if (Throw is not null)
            {
                await System.Threading.Tasks.Task.Yield();
                throw Throw;
            }

            foreach (var published in events)
            {
                yield return published;

                if (!BlockAfterFirst)
                {
                    continue;
                }

                Reached.TrySetResult();

                try
                {
                    await System.Threading.Tasks.Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    SawCancellation = true;
                    throw;
                }
            }
        }

        public IAsyncEnumerable<WorkEvent> ResumeRunAsync(
            RunCheckpoint checkpoint, WorkContext context, CancellationToken ct)
            => throw new NotSupportedException("Resuming is not part of what this test exercises.");
    }
}
