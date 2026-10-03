namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// A run may keep tools back from a step - forbidden by its policy, or needing an approval nobody is there to give.
/// A step that cannot be done without one of them used to have no way to end but badly: it could not call the tool, so
/// the engine saw no refusal; it said what it could not do, and the review, never told the tool had been kept back,
/// called that unsupported and failed the step - twice (run 9384e2, 2026-10-03: a task started from the web, where no
/// shell is offered, asked to measure something only a shell can measure; two attempts, two reviews, "review rejected").
///
/// <para>So where the engine kept tools back, the step may say it cannot go on without them, the engine records the
/// block as its own finding - it is the one that kept them back - and the review is told what was kept back.</para>
///
/// Deliberately not code and not a shell: invoices, a summary, and a file tool the run does not allow.
/// </summary>
public sealed class AStepThatNeedsAWithheldToolTests
{
    private const string Plan = """
        {"disposition":"task","title":"invoices",
         "steps":[{"title":"Add up the invoices","dependsOn":[]},
                  {"title":"Write the summary","dependsOn":[0]},
                  {"title":"Say it is ready","dependsOn":[1]}]}
        """;

    private sealed class Store : IRunCheckpointStore
    {
        public List<RunCheckpoint> Saved { get; } = [];
        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct) { lock (Saved) Saved.Add(checkpoint); return Task.CompletedTask; }
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) { lock (Saved) return Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved.ToArray()); }
        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) { lock (Saved) return Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId)); }
        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A run that may not write files at all: the tool is kept back from the step, not asked about.</summary>
    private static PermissionPolicy NoWriting() => PermissionPolicy.PermissiveDefault with { Deny = ["write_file"] };

    private static IEnumerable<WorkEvent> StepsDone(IEnumerable<WorkEvent> events) => events.Where(e => e.Kind == EventKind.StepCompleted);

    /// <summary>THE ONE THAT MATTERS: one turn, no review, and a cause that says which tool and why it was not there.</summary>
    [Fact]
    public async Task A_step_that_cannot_go_on_without_a_tool_the_run_kept_back_is_blocked_and_the_engine_says_which()
    {
        using var fx = new EngineFixture();                                             // the report's switch is OFF
        var store = new Store();
        var worker = new ByStepChatProvider(Plan)
            .Step("Add up the invoices", Turn.Says("The invoices add up to 10."))
            .Step("Write the summary", Turn.Calls1(AgentBlocked.ToolName,
                """{"reason":"the summary has to be written to a file","needs":"write_file"}""", "b1"));
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        var events = await fx.RunAsync(fx.Build(worker, worker: EngineFixture.WorkerWith("write_file", "read_file"),
            policy: NoWriting(), router: Routers.WithReviewer(), reviewProvider: reviewer, checkpoints: store),
            "add up the invoices and write a summary");

        Assert.Equal(RunOutcomeKind.Blocked, events.Last().Outcome());
        var step = Assert.Single(StepsDone(events), e => e.Summary.Contains("Write the summary"));
        Assert.Equal(StepOutcomeKind.Blocked, step.StepOutcome());
        Assert.Equal("needs a tool this run does not offer: write_file — blocked by this run's permission policy; "
                     + "the step reports it cannot go on: the summary has to be written to a file (needs: write_file)",
            step.OutcomeReason());
        // The engine's finding, not the step's word: it is the engine that kept the tool back.
        Assert.Equal(OutcomeCause.BlockedPermission,
            store.Saved.Last().Steps.Single(s => s.Title == "Write the summary").Blocks![^1].Cause);
        // One turn, and nothing to review: the step did no work a review could judge.
        Assert.Single(worker.RequestsFor("Write the summary"));
        // (Step 1's review names this step among the others, so the review is told apart by the step it is OF.)
        Assert.Contains(reviewer.Requests, r => (r.Messages.Last().Content ?? "").Contains("THIS STEP (1):", StringComparison.Ordinal));
        Assert.DoesNotContain(reviewer.Requests, r => (r.Messages.Last().Content ?? "").Contains("THIS STEP (2):", StringComparison.Ordinal));
        // What waits on it is blocked with it, not skipped.
        Assert.Equal(StepOutcomeKind.Blocked, Assert.Single(StepsDone(events), e => e.Summary.Contains("Say it is ready")).StepOutcome());
    }

    /// <summary>
    /// A tool kept back is the cause only where the step names it. A step in a run without write_file that waits on
    /// a person's answer is not blocked by write_file, and saying so would send the person to the wrong remedy.
    /// </summary>
    [Fact]
    public async Task A_report_that_names_no_tool_the_run_kept_back_stays_the_steps_own_word()
    {
        using var fx = new EngineFixture();
        var store = new Store();
        var worker = new ByStepChatProvider(Plan)
            .Step("Add up the invoices", Turn.Calls1(AgentBlocked.ToolName,
                """{"reason":"the invoices are in two currencies and the request names none","needs":"which currency the total is in"}""", "b1"));

        var events = await fx.RunAsync(fx.Build(worker, worker: EngineFixture.WorkerWith("write_file", "read_file"),
            policy: NoWriting(), checkpoints: store), "add up the invoices and write a summary");

        var step = Assert.Single(StepsDone(events), e => e.Summary.Contains("Add up the invoices"));
        Assert.Equal("the step reports it cannot go on: the invoices are in two currencies and the request names none "
                     + "(needs: which currency the total is in)", step.OutcomeReason());
        Assert.Equal(OutcomeCause.BlockedReported, store.Saved.Last().Steps.Single(s => s.Title == "Add up the invoices").Blocks![^1].Cause);
    }

    /// <summary>The step is told it may say so - and only where something was kept back.</summary>
    [Fact]
    public async Task The_step_is_offered_the_report_and_told_of_it_only_where_the_run_kept_a_tool_back()
    {
        using var fx = new EngineFixture();
        var kept = new ByStepChatProvider(Plan);
        await fx.RunAsync(fx.Build(kept, worker: EngineFixture.WorkerWith("write_file", "read_file"), policy: NoWriting()),
            "add up the invoices and write a summary");

        var asked = kept.RequestsFor("Add up the invoices")[0];
        Assert.Contains(AgentBlocked.ToolName, asked.Tools!.Select(t => t.Name));
        var told = string.Join("\n", asked.Messages.Select(m => m.Content));
        Assert.Contains("Not available in this run: write_file — blocked by this run's permission policy.", told, StringComparison.Ordinal);
        Assert.Contains($"If this step cannot be done without them, call {AgentBlocked.ToolName} and name the tool it needs", told, StringComparison.Ordinal);

        // Nothing kept back, switch off: no report, and no word of one - as before.
        using var plain = new EngineFixture();
        var free = new ByStepChatProvider(Plan);
        await plain.RunAsync(plain.Build(free, worker: EngineFixture.WorkerWith("write_file", "read_file")),
            "add up the invoices and write a summary");
        Assert.DoesNotContain(free.Requests.SelectMany(r => r.Tools ?? []), t => t.Name == AgentBlocked.ToolName);
        Assert.DoesNotContain(free.Requests.SelectMany(r => r.Messages), m => m.Content?.Contains(AgentBlocked.ToolName, StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// A step that hands in what it could do, and says what it could not: the review is told what the engine kept back,
    /// as the engine's own fact, so "I could not write the file" is not judged as a claim no call supports.
    /// </summary>
    [Fact]
    public async Task The_review_is_told_which_tools_the_engine_kept_back_from_the_step()
    {
        using var fx = new EngineFixture();
        var worker = new ByStepChatProvider(Plan)
            .Step("Write the summary", Turn.Says("The total is 10. I could not write summary.md: write_file is not available in this run."));
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        await fx.RunAsync(fx.Build(worker, worker: EngineFixture.WorkerWith("write_file", "read_file"),
            policy: NoWriting(), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "add up the invoices and write a summary");

        var review = Assert.Single(reviewer.Requests,
            r => Verdicts.IsStepVerdict(r) && (r.Messages.Last().Content ?? "").Contains("THIS STEP (2): Write the summary", StringComparison.Ordinal));
        var shown = review.Messages.Last().Content!;
        Assert.Contains("Kept back from this step by the engine, before any work: write_file — blocked by this run's permission policy.",
            shown, StringComparison.Ordinal);
        Assert.Contains("The step could not have called them", shown, StringComparison.Ordinal);
    }
}
