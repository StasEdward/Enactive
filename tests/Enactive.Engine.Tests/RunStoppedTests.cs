namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Xunit;

/// <summary>
/// A run that dies has to say what killed it, in its own record.
///
/// <para>Found on 2026-09-10 by the first real scheduled runs. Both died forty milliseconds in on
/// <c>model 'qwen2.5-coder' not found</c>. The record held three events and no terminal one, so the
/// history said "Incomplete", the report had no reason, and the Inbox — the only thing that reaches
/// a person who was asleep — said "Incomplete · 0 artifact(s)". The cause existed, in full, in a log
/// file nobody had been told about.</para>
///
/// <para>An unattended run that cannot say why it stopped is the exact failure the whole scheduler
/// was built to prevent, so this is not a nicety about error messages.</para>
/// </summary>
public sealed class RunStoppedTests
{
    private sealed class Collecting : IRunStore
    {
        public RunRecord? Saved { get; private set; }

        public Task SaveAsync(RunRecord record, CancellationToken ct)
        {
            Saved = record;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RunSummary>> LoadSummariesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RunSummary>>(Array.Empty<RunSummary>());
        public Task<RunRecord?> LoadAsync(Guid runId, CancellationToken ct)
            => Task.FromResult<RunRecord?>(null);
        public Task<IReadOnlyList<RunRecord>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RunRecord>>(Array.Empty<RunRecord>());
    }

    private static readonly Guid Task1 = Guid.NewGuid();
    private static readonly Guid Run1 = Guid.NewGuid();

    private static WorkEvent Event(EventKind kind, string summary, string? payload = null)
        => new(Guid.NewGuid(), Task1, Run1, DateTimeOffset.Now, kind, summary, payload);

    /// <summary>The three events the real run got through, then whatever ends it.</summary>
    private static async IAsyncEnumerable<WorkEvent> UpToRouting(Exception? throws)
    {
        yield return Event(EventKind.IntentReceived, "Intent: review this");
        yield return Event(EventKind.ContextAssembled, "Workspace 'x'");
        yield return Event(EventKind.Routed, "Worker 'Developer' -> model ollama/qwen2.5-coder");

        await Task.Yield();
        if (throws is not null)
            throw throws;
    }

    private static async Task<RunRecord?> DrainAsync(IAsyncEnumerable<WorkEvent> stream)
    {
        var store = new Collecting();
        var recorder = new RunRecorder(store);

        try
        {
            await foreach (var _ in recorder.RecordAsync(stream, CancellationToken.None)) { }
        }
        catch (Exception) { /* the caller still gets it; this test is about the RECORD */ }

        return store.Saved;
    }

    // ── the record ──────────────────────────────────────────────────────────

    /// <summary>
    /// The exception is rethrown. Recording it must not swallow it: the host decides the exit code,
    /// and a run that reported failure while the process returned 0 would be worse than silence.
    /// </summary>
    [Fact]
    public async Task The_exception_still_reaches_the_caller()
    {
        var recorder = new RunRecorder(new Collecting());
        var boom = new InvalidOperationException("provider said no");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in recorder.RecordAsync(UpToRouting(boom), CancellationToken.None)) { }
        });

        Assert.Same(boom, thrown);
    }

    [Fact]
    public async Task A_run_killed_by_an_exception_records_the_reason()
    {
        var record = await DrainAsync(UpToRouting(
            new HttpRequestException("Provider 'ollama' returned 404: model 'qwen2.5-coder' not found")));

        Assert.NotNull(record);
        Assert.Contains("qwen2.5-coder", RunReport.ReasonOf(record!));
    }

    /// <summary>
    /// And as an OUTCOME, not only as prose. The report, the status column and the Inbox line are
    /// all built from the typed value; a message with no outcome beside it leaves every one of them
    /// saying "Incomplete", which is what happened.
    /// </summary>
    [Fact]
    public async Task It_ends_as_Failed_rather_than_Incomplete()
    {
        var record = await DrainAsync(UpToRouting(new HttpRequestException("404")));

        Assert.Equal(RunOutcomeKind.Failed, RunReport.OutcomeOf(record!));
        Assert.Equal("Failed", record!.Status);
    }

    /// <summary>The message is on the timeline too, which is where a person scrolls for it.</summary>
    [Fact]
    public async Task The_error_is_on_the_timeline()
    {
        var record = await DrainAsync(UpToRouting(new HttpRequestException("model not found")));

        Assert.Contains(record!.Events, e =>
            e.Kind == nameof(EventKind.ErrorObserved) && e.Summary.Contains("model not found"));
    }

    /// <summary>
    /// Cancelled is not failed. Somebody pressing Stop, or a scheduled run cut off at its time
    /// limit, has not produced a wrong answer, and calling it a failure sends them looking for a
    /// defect that is not there.
    /// </summary>
    [Fact]
    public async Task A_cancelled_run_is_recorded_as_cancelled()
    {
        var record = await DrainAsync(UpToRouting(new OperationCanceledException()));

        Assert.Equal(RunOutcomeKind.Cancelled, RunReport.OutcomeOf(record!));
        Assert.DoesNotContain(record!.Events, e => e.Kind == nameof(EventKind.ErrorObserved));
    }

    /// <summary>
    /// A run that ended properly is untouched. The whole change is about the case where the engine
    /// did NOT get to say how it ended.
    /// </summary>
    [Fact]
    public async Task A_run_that_ended_normally_is_left_alone()
    {
        async IAsyncEnumerable<WorkEvent> Finished()
        {
            yield return Event(EventKind.IntentReceived, "Intent: review this");
            yield return Event(EventKind.TaskCompleted, "Completed",
                WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed));
            await Task.CompletedTask;
        }

        var record = await DrainAsync(Finished());

        Assert.Equal(RunOutcomeKind.Completed, RunReport.OutcomeOf(record!));
        Assert.Equal(2, record!.Events.Count);
    }

    /// <summary>
    /// Nothing is invented out of nothing. An exception before any event means there is no run to
    /// attach a failure to, and a record fabricated from it would be a run that never started.
    /// </summary>
    [Fact]
    public async Task An_exception_before_any_event_records_no_run()
    {
        async IAsyncEnumerable<WorkEvent> NothingAtAll()
        {
            await Task.Yield();
            throw new InvalidOperationException("died on the way in");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        Assert.Null(await DrainAsync(NothingAtAll()));
    }

    // ── and what the window says about it ───────────────────────────────────

    /// <summary>
    /// The run above rebuilds no step cards, and the reason is NOT that the record is old. Saying so
    /// sends a person looking for a history problem while the failure sits in the same record.
    /// </summary>
    [Fact]
    public async Task A_run_that_never_planned_says_so_rather_than_blaming_its_age()
    {
        var record = await DrainAsync(UpToRouting(
            new HttpRequestException("model 'qwen2.5-coder' not found")));

        var words = RunReport.WhyNoSteps(record!);

        Assert.Contains("stopped before it had a plan", words);
        Assert.Contains("qwen2.5-coder", words);
        Assert.DoesNotContain("before step numbers", words);
    }

    /// <summary>A record that DID plan and still has no step numbers is the old one, and still says so.</summary>
    [Fact]
    public void A_record_that_planned_without_step_numbers_is_the_old_kind()
    {
        var at = DateTimeOffset.Now;
        var record = new RunRecord(
            Guid.NewGuid(), Guid.NewGuid(), "old", null, at, at, "Completed",
            new[]
            {
                new RunEventRecord(at, nameof(EventKind.PlanCreated), "Plan: 3 steps"),
                new RunEventRecord(at, nameof(EventKind.TaskCompleted), "Completed")
            },
            Array.Empty<string>(), Array.Empty<string>());

        Assert.Contains("before step numbers", RunReport.WhyNoSteps(record));
    }

    /// <summary>With steps there is nothing to explain, and the window shows the cards.</summary>
    [Fact]
    public void A_record_with_steps_explains_nothing()
    {
        var at = DateTimeOffset.Now;
        var record = new RunRecord(
            Guid.NewGuid(), Guid.NewGuid(), "fine", null, at, at, "Completed",
            new[] { new RunEventRecord(at, nameof(EventKind.StepStarted), "Step 1", 1) },
            Array.Empty<string>(), Array.Empty<string>());

        Assert.Equal("", RunReport.WhyNoSteps(record));
    }
}
