namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// A long generation says it is still going.
///
/// <para><b>Measured 2026-09-24 15:34-15:37, run a2142be6.</b> One <c>write_file</c> of 6,795 tokens
/// took three minutes on a local model, and nothing on the step card moved: the model's text
/// streams to the card, but tool-call arguments and reasoning were collected in silence. Asked the
/// same afternoon: "the model went off generating something again". It was writing a 27 KB report.
/// Any model writing a big file, or reasoning at length, looks exactly as stuck.</para>
/// </summary>
public sealed class ALongGenerationSaysItIsStillGoingTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write the report"}""";

    private static IEnumerable<WorkEvent> Progress(IEnumerable<WorkEvent> events)
        => events.Where(e => e.Kind == EventKind.GenerationProgress);

    [Fact]
    public async Task A_long_tool_call_says_it_is_still_being_written()
    {
        using var fx = new EngineFixture();
        var body = new string('x', 7_000);

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", $$"""{"path":"report.md","content":"{{body}}"}"""),
            Turn.Says("Written."));

        var events = await fx.RunAsync(fx.Build(provider), "write the report");

        var said = Assert.Single(Progress(events));
        Assert.Contains("Writing write_file", said.Summary, StringComparison.Ordinal);
        Assert.Contains("characters so far", said.Summary, StringComparison.Ordinal);
    }

    /// <summary>A model reasoning at length is the other silence, and says so the same way.</summary>
    [Fact]
    public async Task Long_reasoning_says_it_is_still_going()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            new Turn("Done.", Thinking: new string('r', 3_000)));

        var events = await fx.RunAsync(fx.Build(provider), "write the report");

        Assert.Contains(Progress(events), e => e.Summary.StartsWith("Reasoning: 3,000", StringComparison.Ordinal));
    }

    /// <summary>THE BOUNDARY. An ordinary call is not announced: most calls are a path and a pattern.</summary>
    [Fact]
    public async Task A_short_call_says_nothing_extra()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "a");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("read_file", """{"path":"a.md"}"""),
            Turn.Says("Read it."));

        var events = await fx.RunAsync(fx.Build(provider), "write the report");

        Assert.Empty(Progress(events));
    }

    /// <summary>
    /// Progress is for the person watching, not for the record: "still writing" stops being true of
    /// anything the moment the write arrives, and a run's history is read long after.
    /// </summary>
    [Fact]
    public async Task Progress_is_not_kept_in_the_runs_record()
    {
        var saved = new Saving();
        var recorder = new RunRecorder(saved);
        var task = Guid.NewGuid();
        var run = Guid.NewGuid();

        async IAsyncEnumerable<WorkEvent> Stream()
        {
            yield return new(Guid.NewGuid(), task, run, DateTimeOffset.Now, EventKind.IntentReceived, "Intent: write", null);
            yield return new(Guid.NewGuid(), task, run, DateTimeOffset.Now, EventKind.GenerationProgress, "Writing write_file: 2,000 characters so far…", null);
            yield return new(Guid.NewGuid(), task, run, DateTimeOffset.Now, EventKind.TaskCompleted, "done", null);
            await Task.CompletedTask;
        }

        await foreach (var _ in recorder.RecordAsync(Stream(), CancellationToken.None)) { }

        Assert.NotNull(saved.Record);
        Assert.DoesNotContain(saved.Record!.Events, e => e.Kind == nameof(EventKind.GenerationProgress));
        Assert.Contains(saved.Record.Events, e => e.Kind == nameof(EventKind.TaskCompleted));
    }

    private sealed class Saving : IRunStore
    {
        public RunRecord? Record { get; private set; }

        public Task SaveAsync(RunRecord record, CancellationToken ct)
        {
            Record = record;
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
}
