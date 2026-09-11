namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// FIX_PLAN §9am, M1: how much of a run came from a cache.
///
/// <para>Prompt caching was built on 2026-09-11 and the number stopped at the log. The run record
/// could say what a run SPENT and not what it SAVED, which is the half somebody reading a bill is
/// actually looking for — and with no record of it, "is caching on" could only be answered by
/// opening the provider's own console.</para>
///
/// <para>The rule every assertion here is about: <b>null is not zero.</b> Zero says the cache was
/// cold or the prompt was below the model's minimum. Null says nobody counted. Rendering the second
/// as the first tells somebody their caching is broken when the truth is that Ollama has no such
/// number to give.</para>
/// </summary>
public sealed class CachedSpendTests
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

    private static WorkEvent Spend(
        string purpose, string provider, string model, int inTok, int outTok, int? cached)
        => new(Guid.NewGuid(), Task1, Run1, DateTimeOffset.Now, EventKind.UsageReported,
               $"tokens: {inTok} in, {outTok} out ({provider}/{model}, {purpose})",
               WorkEventPayload.UsagePayload(inTok, outTok, null, provider, model, purpose, cached));

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

    // ── the number reaching the record ──────────────────────────────────────

    /// <summary>
    /// The whole point: a run that was served part of its prompt from a cache says so, per model and
    /// in total.
    /// </summary>
    [Fact]
    public async Task A_run_records_how_much_of_its_prompt_came_from_a_cache()
    {
        var record = await RecordOf(
            Spend("plan", "Antropic", "claude-sonnet-4-6", 2_000, 100, cached: 0),
            Spend("review", "Antropic", "claude-sonnet-4-6", 4_000, 200, cached: 3_600));

        var usage = Assert.IsType<RunUsage>(record!.Usage);

        Assert.Equal(3_600, usage.CachedPromptTokens);

        var review = usage.ByModel!.Single(m => m.Purpose == "review");
        Assert.Equal(3_600, review.CachedPromptTokens);

        // And the plan row keeps its own answer, which is a real zero: it was asked and the cache
        // was cold.
        var plan = usage.ByModel!.Single(m => m.Purpose == "plan");
        Assert.Equal(0, plan.CachedPromptTokens);
    }

    /// <summary>
    /// A run where nothing reports a cache reports NOTHING, not zero. Every local runtime is this
    /// case, and so is every record written before 2026-09-11.
    /// </summary>
    [Fact]
    public async Task A_run_that_was_never_told_says_nothing_rather_than_zero()
    {
        var record = await RecordOf(
            Spend("execute", "ollama", "gemma4:31b", 5_000, 300, cached: null),
            Spend("execute", "ollama", "gemma4:31b", 6_000, 250, cached: null));

        var usage = Assert.IsType<RunUsage>(record!.Usage);

        Assert.Null(usage.CachedPromptTokens);
        Assert.Null(usage.ByModel!.Single().CachedPromptTokens);

        // The tokens themselves are still counted. Nothing about the cached share may quietly
        // remove a run's spending from the record.
        Assert.Equal(11_000, usage.PromptTokens);
    }

    /// <summary>
    /// The mixed run, which is the shape of this machine: the worker is on Ollama and says nothing,
    /// the reviewer is on Anthropic and does. The total is the reviewer's number — a SUM over the
    /// models that answered, not an average over all of them.
    /// </summary>
    [Fact]
    public async Task One_model_answering_is_enough_for_the_run_to_answer()
    {
        var record = await RecordOf(
            Spend("execute", "ollama", "gemma4:31b", 5_000, 300, cached: null),
            Spend("review", "Antropic", "claude-sonnet-4-6", 2_400, 120, cached: 1_800));

        var usage = Assert.IsType<RunUsage>(record!.Usage);

        Assert.Equal(1_800, usage.CachedPromptTokens);
        Assert.Null(usage.ByModel!.Single(m => m.ProviderId == "ollama").CachedPromptTokens);
    }

    /// <summary>
    /// Turns are added, like every other figure in a run. Providers report a total per turn rather
    /// than an increment, so the run is the sum of the turns.
    /// </summary>
    [Fact]
    public async Task Two_turns_on_one_model_add_up()
    {
        var record = await RecordOf(
            Spend("execute", "Antropic", "claude-sonnet-4-6", 3_000, 100, cached: 0),
            Spend("execute", "Antropic", "claude-sonnet-4-6", 5_000, 120, cached: 2_800));

        var row = Assert.Single(Assert.IsType<RunUsage>(record!.Usage).ByModel!);

        Assert.Equal(2_800, row.CachedPromptTokens);
        Assert.Equal(2, row.Calls);
    }

    // ── the arithmetic that keeps null meaning null ─────────────────────────

    /// <summary>
    /// The one function this all rests on. <c>(a ?? 0) + (b ?? 0)</c> is the obvious way to write it
    /// and turns "this provider has no such number" into "this provider's caching is doing nothing",
    /// which is a claim about somebody's setup that nobody made.
    /// </summary>
    [Fact]
    public void Adding_two_silences_is_still_silence()
    {
        Assert.Null(TokenCounts.Add(null, null));
        Assert.Equal(400, TokenCounts.Add(null, 400));
        Assert.Equal(400, TokenCounts.Add(400, null));
        Assert.Equal(700, TokenCounts.Add(300, 400));

        // And a real zero survives being added to, rather than being treated as absent.
        Assert.Equal(0, TokenCounts.Add(0, 0));
        Assert.Equal(5, TokenCounts.Add(0, 5));
    }

    /// <summary>
    /// The share, for a reader who wants "is caching doing anything" answered without dividing two
    /// five-digit numbers in their head. Null wherever there is nothing to divide, because a
    /// percentage invented from an absent number is worse than no percentage.
    /// </summary>
    [Fact]
    public void The_share_is_a_percentage_or_nothing_at_all()
    {
        Assert.Equal(90, new ModelSpend("review", "Antropic", "m", 4_000, 200, 1)
        {
            CachedPromptTokens = 3_600
        }.CachedPercent);

        Assert.Null(new ModelSpend("execute", "ollama", "m", 4_000, 200, 1).CachedPercent);

        // No prompt at all is not a zero percent cache hit rate; there was nothing to hit.
        Assert.Null(new ModelSpend("execute", "ollama", "m", 0, 0, 1)
        {
            CachedPromptTokens = 0
        }.CachedPercent);
    }

    // ── the payload it travels in ───────────────────────────────────────────

    /// <summary>
    /// The cached field is written AFTER "out" on purpose: the in/out pair is matched as an ADJACENT
    /// pair, so a field inserted between them would stop every usage event in the product being read
    /// at all — silently, since the reader returns null rather than throwing.
    /// </summary>
    [Fact]
    public void Adding_the_cached_field_did_not_break_reading_the_other_two()
    {
        var ev = Spend("execute", "Antropic", "m", 1_234, 56, cached: 789);

        Assert.Equal((1_234, 56), ev.Usage());
        Assert.Equal(789, ev.CachedTokens());
    }

    /// <summary>
    /// A payload written before this existed reads back as "nobody counted", not as zero. Every
    /// record in anybody's history is this case.
    /// </summary>
    [Fact]
    public void A_payload_from_before_this_existed_says_nothing()
    {
        var ev = Spend("execute", "Antropic", "m", 1_000, 50, cached: null);

        Assert.Equal((1_000, 50), ev.Usage());
        Assert.Null(ev.CachedTokens());
        Assert.DoesNotContain("cached", ev.PayloadJson!, StringComparison.Ordinal);
    }

    // ── the provider that produces it ───────────────────────────────────────

    /// <summary>
    /// The streaming path carries it too. The tool loop — the place caching actually pays, because
    /// every turn re-sends the whole transcript — reads <c>UsageDelta</c> and nothing else, so a
    /// number that reached only <c>ChatCompletion</c> would be a number the worker never reported.
    /// </summary>
    [Fact]
    public void The_streaming_path_carries_the_cached_share()
    {
        var delta = new UsageDelta(1_000, 50, 800);

        Assert.Equal(800, delta.CachedPromptTokens);

        // And the two-argument form still means what it meant: not counted.
        Assert.Null(new UsageDelta(1_000, 50).CachedPromptTokens);
    }
}
