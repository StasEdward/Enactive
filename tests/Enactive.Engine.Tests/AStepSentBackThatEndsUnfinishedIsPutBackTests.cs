namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Bench build-error, 2026-10-18: the step review rejected the step for a change to a file the request forbade, made with a
/// PowerShell <c>-replace | Set-Content</c> after the write boundary had refused the edit twice. The attempt after the
/// rejection got stuck and the step ended INCOMPLETE - and with RevertRejectedSteps on, the run ended with the forbidden
/// change still there: only a ReviewRejected step was put back, and a command's writes are not the store's to put back.
/// A step whose last verdict sent its work back and that ended with no verdict since is now put back as a rejected step
/// is; and what a command changed that a revert leaves is named, with why it is left.
/// </summary>
public sealed class AStepSentBackThatEndsUnfinishedIsPutBackTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write"}""";

    private static IReadOnlyList<WorkEvent> Reverts(IEnumerable<WorkEvent> events)
        => events.Where(e => e.Kind == EventKind.ArtifactReverted).ToArray();

    /// <summary>
    /// THE ONE THAT MATTERS: rejected by its review, the step's next attempt only goes round in circles and is stopped -
    /// Incomplete, with no verdict since the rejection. Its work goes back as a rejected step's does, and the log says why.
    /// </summary>
    [Fact]
    public async Task A_step_sent_back_by_its_review_that_then_ends_incomplete_has_its_work_put_back()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}""", "w1"), Turn.Says("Wrote the record."))
        {
            // The attempt after the rejection reads the same file over and over: the engine stops it as stuck.
            WhenExhausted = Turn.Calls1("read_file", """{"path":"record.txt"}""", "r1")
        };
        var reviewer = new FakeChatProvider(Verdicts.Fail("the record is wrong"));

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "Write one record");

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.False(fx.Exists("record.txt"));
        Assert.Contains(Reverts(events), e => e.Summary.StartsWith("The step ended incomplete because the step got stuck repeating the same calls, not on a verdict.",
            StringComparison.Ordinal));
        Assert.Contains(Reverts(events), e => e.RevertedPaths()!.Contains("record.txt"));
    }

    /// <summary>
    /// A step that ends incomplete with no verdict ever against it keeps its work, as it always did: nobody said it was
    /// wrong. And one whose last attempt had a verdict of its own - what is left cannot be reached within the request -
    /// keeps it too, though an earlier verdict sent it back.
    /// </summary>
    [Fact]
    public async Task A_step_that_was_never_sent_back_or_was_last_judged_unreachable_keeps_its_work()
    {
        using (var fx = new EngineFixture())
        {
            var worker = new FakeChatProvider(Turn.Says(QuickAction),
                Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}""", "w1"))
            {
                WhenExhausted = Turn.Calls1("read_file", """{"path":"record.txt"}""", "r1")
            };
            var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: new FakeChatProvider()),
                "Write one record");

            Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
            Assert.Equal("one", fx.Read("record.txt"));
            Assert.Empty(Reverts(events));
        }

        using (var fx = new EngineFixture())
        {
            var worker = new FakeChatProvider(Turn.Says(QuickAction),
                Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}""", "w1"), Turn.Says("Wrote the record."),
                Turn.Calls1("write_file", """{"path":"record.txt","content":"two"}""", "w2"), Turn.Says("Wrote it again."));
            var reviewer = new FakeChatProvider(Verdicts.Fail("the record is wrong"),
                Turn.Says(Verdicts.ShortVerdict("unreachable", "the rest needs a file the request forbids", [], [])));

            var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "Write one record");

            Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
            Assert.Equal("two", fx.Read("record.txt"));
            Assert.Empty(Reverts(events));
        }
    }

    // ── a plan's step, as the bench's was ──────────────────────────────────────────────────

    private const string TwoSteps = """
        {"disposition":"task","title":"records","steps":[{"title":"Write the record","dependsOn":[]},{"title":"Report on it","dependsOn":[0]}]}
        """;

    /// <summary>
    /// The bench's own shape: a plan's step, sent back by its review, then stuck - its work goes back through the plan's
    /// call site, and the step after it is skipped as before.
    /// </summary>
    [Fact]
    public async Task A_plan_step_sent_back_by_its_review_that_then_ends_incomplete_has_its_work_put_back()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(Turn.Says(TwoSteps),
            Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}""", "w1"), Turn.Says("Wrote the record."))
        {
            WhenExhausted = Turn.Calls1("read_file", """{"path":"record.txt"}""", "r1")
        };
        var reviewer = new FakeChatProvider(Verdicts.Fail("the record is wrong"));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "Write one record and report on it");

        var step = events.Last(e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("Write the record", StringComparison.Ordinal));
        Assert.Equal(StepOutcomeKind.Incomplete, step.StepOutcome());
        Assert.False(fx.Exists("record.txt"));
        Assert.Contains(Reverts(events), e => e.Summary.StartsWith("[1] The step ended incomplete because the step got stuck repeating the same calls, not on a verdict.",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// Sent back by the engine's gate - a blocking check failed on it - then stuck: no review ever saw it, and its work goes
    /// back whole, as a step the gate rejects does (nothing found right).
    /// </summary>
    [Fact]
    public async Task A_step_sent_back_by_the_gate_that_then_ends_incomplete_has_its_work_put_back()
    {
        using var fx = new EngineFixture
        {
            Checks = new Enactive.Core.Checks.CheckSnapshot([new Enactive.Core.Checks.ResolvedCheck(
                new Enactive.Core.Checks.CheckDefinition("lint", null, "echo LINT-ERROR-42 && exit 1", ["src/**"],
                    Enactive.Core.Checks.CheckFire.Step, Enactive.Core.Checks.CheckSeverity.Blocking,
                    Enactive.Core.Checks.CheckLimits.DefaultTimeoutSeconds, null), Enactive.Core.Checks.CheckOrigin.Workspace)], [])
        };
        var worker = new FakeChatProvider(Turn.Says(TwoSteps),
            Turn.Calls1("write_file", """{"path":"src/record.txt","content":"one"}""", "w1"), Turn.Says("Wrote the record."))
        {
            WhenExhausted = Turn.Calls1("read_file", """{"path":"src/record.txt"}""", "r1")
        };
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "Write one record and report on it");

        Assert.Empty(reviewer.Requests);
        Assert.Contains(events, e => e.Kind == EventKind.ReviewFailed && e.Summary.Contains("the engine's check 'lint'", StringComparison.Ordinal));
        var step = events.Last(e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("Write the record", StringComparison.Ordinal));
        Assert.Equal(StepOutcomeKind.Incomplete, step.StepOutcome());
        Assert.False(fx.Exists("src/record.txt"));
        Assert.Contains(Reverts(events), e => e.RevertedPaths()!.Contains("src/record.txt"));
    }

    // ── what the revert says ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sent back with nothing changed, then stuck: nothing to put back, and nothing is said - the line on why it goes back
    /// was said above nothing, reading as a revert that put back nothing.
    /// </summary>
    [Fact]
    public async Task A_step_sent_back_that_changed_nothing_says_nothing_of_a_revert()
    {
        using var fx = new EngineFixture();
        fx.Write("record.txt", "as it was");
        var worker = new FakeChatProvider(Turn.Says(QuickAction), Turn.Says("The record is fine as it is."))
        {
            WhenExhausted = Turn.Calls1("read_file", """{"path":"record.txt"}""", "r1")
        };
        var reviewer = new FakeChatProvider(Verdicts.Fail("the record was to be written"));

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "Write one record");

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Empty(Reverts(events));
    }

    /// <summary>A provider that answers its script, then fails as one whose retries are spent.</summary>
    private sealed class SpentAfter(FakeChatProvider inner, int answers) : Enactive.Core.Providers.IChatProvider
    {
        private int _asked;

        public IAsyncEnumerable<Enactive.Core.Chat.ChatStreamEvent> StreamChatAsync(Enactive.Core.Chat.ChatRequest request, CancellationToken ct)
            => ++_asked > answers ? throw new Enactive.Core.Execution.RetryBudgetExceededException("the provider's retries are spent",
                new HttpRequestException("503")) : inner.StreamChatAsync(request, ct);

        public Task<Enactive.Core.Chat.ChatCompletion> CompleteAsync(Enactive.Core.Chat.ChatRequest request, CancellationToken ct)
            => ++_asked > answers ? throw new Enactive.Core.Execution.RetryBudgetExceededException("the provider's retries are spent",
                new HttpRequestException("503")) : inner.CompleteAsync(request, ct);
    }

    /// <summary>
    /// A quick action sent back, whose next attempt breaks on the provider - its retries spent: Incomplete with no verdict
    /// since, as a plan's step in the same place, and its work goes back. The quick action's revert sat after the attempts
    /// inside the same try, so the exception skipped it.
    /// </summary>
    [Fact]
    public async Task A_quick_action_sent_back_whose_provider_then_gives_out_has_its_work_put_back()
    {
        using var fx = new EngineFixture();
        var script = new FakeChatProvider(Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}""", "w1"), Turn.Says("Wrote the record."));
        var reviewer = new FakeChatProvider(Verdicts.Fail("the record is wrong"));

        var events = await fx.RunAsync(fx.Build(new SpentAfter(script, 3), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "Write one record");

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.False(fx.Exists("record.txt"));
        Assert.Contains(Reverts(events), e => e.Summary.StartsWith("The step ended incomplete because the provider gave out, not on a verdict.",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// A plan's step in the same place: its provider gives out on the attempt after the review sent it back. The exception
    /// ends the step past its result, so what stopped it is carried to the revert apart - and named, not read as a verdict.
    /// </summary>
    [Fact]
    public async Task A_plan_step_sent_back_whose_provider_then_gives_out_says_so_above_its_revert()
    {
        using var fx = new EngineFixture();
        var script = new FakeChatProvider(Turn.Says(TwoSteps),
            Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}""", "w1"), Turn.Says("Wrote the record."));
        var reviewer = new FakeChatProvider(Verdicts.Fail("the record is wrong"));

        var events = await fx.RunAsync(fx.Build(new SpentAfter(script, 3), EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "Write one record and report on it");

        var step = events.Last(e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("Write the record", StringComparison.Ordinal));
        Assert.Equal(StepOutcomeKind.Incomplete, step.StepOutcome());
        Assert.False(fx.Exists("record.txt"));
        Assert.Contains(Reverts(events), e => e.Summary.StartsWith("[1] The step ended incomplete because the provider gave out, not on a verdict.",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// A rejected step that changed a file through a command: the store puts back what the file tools wrote, and the file
    /// the command changed is named as left, with why - it used to be named nowhere, as if the revert had undone the step.
    /// </summary>
    [Fact]
    public async Task What_a_rejected_steps_command_changed_is_named_as_left_with_why()
    {
        using var fx = new EngineFixture();
        fx.Write("other.txt", "as it was");
        var worker = new FakeChatProvider(Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"record.txt","content":"one"}""", "w1"),
            Turn.Calls1("run_command", """{"command":"echo changed> other.txt"}""", "c1"),
            Turn.Says("Wrote the record and tidied the other file.")) { WhenExhausted = Turn.Says("Wrote the record.") };
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Fail("other.txt was not to be changed") };

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "Write one record");

        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
        Assert.False(fx.Exists("record.txt"));
        Assert.Contains("changed", fx.Read("other.txt"), StringComparison.Ordinal);
        var left = Assert.Single(Reverts(events), e => e.Summary.StartsWith("Left as it is, changed by a command", StringComparison.Ordinal));
        Assert.Contains("other.txt - a command's writes are not recorded per file", left.Summary, StringComparison.Ordinal);
        Assert.Equal(["other.txt"], left.KeptPaths()!);
        Assert.DoesNotContain("record.txt", left.Summary, StringComparison.Ordinal);
    }
}
