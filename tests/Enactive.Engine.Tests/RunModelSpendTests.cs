namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// What a run says it ran on.
///
/// <para>The record kept ONE model string, scraped out of the first routing event's sentence. A run
/// with phase bindings is not one model: on 2026-09-11 a scheduled run planned and reviewed on
/// <c>Antropic/claude-sonnet-4-6</c> and executed on <c>ollama/gemma4:31b-cloud</c>, and its record
/// said <c>model: ollama/gemma4:31b-cloud</c>. The half that costs money is the half it left
/// out.</para>
///
/// <para>What is recorded is what was SPENT, not what was configured. A review bound to an expensive
/// model and never invoked appears nowhere here, because it cost nothing and a bill is not a list of
/// intentions.</para>
/// </summary>
public sealed class RunModelSpendTests
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

    private static WorkEvent Spend(string purpose, string provider, string model, int inTok, int outTok)
        => Event(EventKind.UsageReported,
                 $"tokens: {inTok} in, {outTok} out ({provider}/{model}, {purpose})",
                 WorkEventPayload.UsagePayload(inTok, outTok, null, provider, model, purpose));

    private static async Task<RunRecord?> RecordOf(params WorkEvent[] events)
    {
        var store = new Collecting();
        var recorder = new RunRecorder(store);

        await foreach (var _ in recorder.RecordAsync(Stream(events), CancellationToken.None)) { }
        return store.Saved;

        static async IAsyncEnumerable<WorkEvent> Stream(WorkEvent[] events)
        {
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    /// <summary>The run from the screenshot: three phases, two providers, one of them the cloud.</summary>
    private static WorkEvent[] TheScheduledRun() =>
    [
        Event(EventKind.IntentReceived, "Intent: sync the docs"),
        Event(EventKind.Routed, "Worker 'Developer' -> model ollama/gemma4:31b-cloud",
              WorkEventPayload.RoutePayload("worker", "ollama", "gemma4:31b-cloud")),
        Event(EventKind.Routed, "Planner -> Antropic/claude-sonnet-4-6",
              WorkEventPayload.RoutePayload("plan", "Antropic", "claude-sonnet-4-6")),
        Spend("plan", "Antropic", "claude-sonnet-4-6", 580, 66),
        Spend("execute", "ollama", "gemma4:31b-cloud", 3305, 15),
        Spend("execute", "ollama", "gemma4:31b-cloud", 3443, 21),
        Spend("review", "Antropic", "claude-sonnet-4-6", 2396, 95),
        Event(EventKind.TaskCompleted, "done",
              WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed)),
    ];

    // ── every model that spent anything ──────────────────────────────────────────────

    [Fact]
    public async Task A_run_on_three_phases_records_every_model_that_spent()
    {
        var record = await RecordOf(TheScheduledRun());

        var spend = record!.Usage!.ByModel;

        Assert.NotNull(spend);
        Assert.Equal(3, spend!.Count);
        Assert.Contains(spend, s => s.Purpose == "plan" && s.Ref == "Antropic/claude-sonnet-4-6");
        Assert.Contains(spend, s => s.Purpose == "execute" && s.Ref == "ollama/gemma4:31b-cloud");
        Assert.Contains(spend, s => s.Purpose == "review" && s.Ref == "Antropic/claude-sonnet-4-6");
    }

    /// <summary>
    /// The same model under two phases stays two rows. It is the one thing a bill is read for: the
    /// planner and the reviewer are both Sonnet here, and "what did review cost me" has no answer
    /// once they are added together.
    /// </summary>
    [Fact]
    public async Task One_model_serving_two_phases_is_two_rows()
    {
        var record = await RecordOf(TheScheduledRun());

        var sonnet = record!.Usage!.ByModel!
            .Where(s => s.Ref == "Antropic/claude-sonnet-4-6")
            .ToList();

        Assert.Equal(2, sonnet.Count);
        Assert.Equal(646, sonnet.Single(s => s.Purpose == "plan").Total);
        Assert.Equal(2491, sonnet.Single(s => s.Purpose == "review").Total);
    }

    /// <summary>Repeat calls to one model in one phase add up, and are counted.</summary>
    [Fact]
    public async Task Repeat_calls_to_one_model_are_summed()
    {
        var record = await RecordOf(TheScheduledRun());

        var execute = record!.Usage!.ByModel!.Single(s => s.Purpose == "execute");

        Assert.Equal(2, execute.Calls);
        Assert.Equal(6748, execute.PromptTokens);
        Assert.Equal(36, execute.CompletionTokens);
    }

    /// <summary>
    /// The breakdown adds up to the total. If it did not, one of the two numbers on the screen would
    /// be wrong and there would be no way to tell which.
    /// </summary>
    [Fact]
    public async Task The_breakdown_adds_up_to_the_total()
    {
        var usage = (await RecordOf(TheScheduledRun()))!.Usage!;

        Assert.Equal(usage.PromptTokens, usage.ByModel!.Sum(s => s.PromptTokens));
        Assert.Equal(usage.CompletionTokens, usage.ByModel!.Sum(s => s.CompletionTokens));
    }

    // ── the single Model field, from values rather than a sentence ───────────────────

    /// <summary>
    /// The listed model survives a reworded routing message.
    ///
    /// <para>This is the fault, stated correctly on the second try. The old code found the worker's
    /// route by searching every routing summary for the phrase "-&gt; model ", and it picked the
    /// right one only because the worker's sentence is the one that happens to contain it — the
    /// planner's reads "Planner -&gt; Antropic/…". So the column a run list shows rested on five
    /// characters of wording in a message written for a person, and editing that message would have
    /// emptied it with nothing failing.</para>
    ///
    /// <para>My first version of this test asserted the wrong thing — that the ORDER of the routing
    /// events mattered — and stayed green through the differential, because it never did. Worth
    /// keeping the note: a differential is also how you find out your test guards nothing.</para>
    /// </summary>
    [Fact]
    public async Task The_listed_model_survives_a_reworded_routing_message()
    {
        var record = await RecordOf(
            Event(EventKind.IntentReceived, "Intent: sync the docs"),
            Event(EventKind.Routed, "Planner -> Antropic/claude-sonnet-4-6",
                  WorkEventPayload.RoutePayload("plan", "Antropic", "claude-sonnet-4-6")),
            // The same event, worded any other way. The values are unchanged.
            Event(EventKind.Routed, "Developer will work on ollama/gemma4:31b-cloud",
                  WorkEventPayload.RoutePayload("worker", "ollama", "gemma4:31b-cloud")),
            Event(EventKind.TaskCompleted, "done",
                  WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed)));

        Assert.Equal("ollama/gemma4:31b-cloud", record!.Model);
    }

    /// <summary>A record from before routing carried values still reads its model out of the
    /// sentence — the same arrangement every other value in this file has.</summary>
    [Fact]
    public async Task An_older_record_still_names_its_model()
    {
        var record = await RecordOf(
            Event(EventKind.IntentReceived, "Intent: sync the docs"),
            Event(EventKind.Routed, "Worker 'Developer' -> model ollama/qwen2.5-coder"),
            Event(EventKind.TaskCompleted, "done"));

        Assert.Equal("ollama/qwen2.5-coder", record!.Model);
    }

    // ── what is NOT recorded ─────────────────────────────────────────────────────────

    /// <summary>
    /// Null, not an empty list, when nothing named a model. A run recorded by an older build has
    /// tokens and no attribution, and an empty list would read as "this run spent nothing anywhere".
    /// </summary>
    [Fact]
    public async Task A_run_whose_calls_named_no_model_records_no_breakdown()
    {
        var record = await RecordOf(
            Event(EventKind.IntentReceived, "Intent: sync the docs"),
            Event(EventKind.UsageReported, "tokens: 10 in, 20 out", """{"in":10,"out":20}"""),
            Event(EventKind.TaskCompleted, "done"));

        Assert.NotNull(record!.Usage);
        Assert.Equal(30, record.Usage!.Total);
        Assert.Null(record.Usage.ByModel);
    }

    /// <summary>
    /// A phase that was bound and never ran is not in the breakdown. This records spending, and a
    /// reviewer that was configured but never invoked cost nothing — listing it would turn the one
    /// honest number on the screen into a list of intentions.
    /// </summary>
    [Fact]
    public async Task A_phase_that_never_ran_is_not_in_the_breakdown()
    {
        var record = await RecordOf(
            Event(EventKind.IntentReceived, "Intent: sync the docs"),
            Event(EventKind.Routed, "Reviewer -> Antropic/claude-sonnet-4-6",
                  WorkEventPayload.RoutePayload("review", "Antropic", "claude-sonnet-4-6")),
            Spend("execute", "ollama", "gemma4:31b-cloud", 100, 10),
            Event(EventKind.TaskCompleted, "done"));

        Assert.Equal("execute", Assert.Single(record!.Usage!.ByModel!).Purpose);
    }
}
