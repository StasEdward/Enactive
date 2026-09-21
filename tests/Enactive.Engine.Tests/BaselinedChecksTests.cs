namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// What each proposed check is worth, asked before any of the work.
///
/// <para><b>Measured 2026-09-21.</b> A run on the real repository was called finished because two
/// checks passed: <c>Docs/DRIFT_ollama.md exists and is not empty</c>, and <c>every wiki page is
/// named in it</c>. Both were true of a file that had been sitting in the workspace since the
/// previous evening. The work that run did was real — it rewrote the report from 38 KB to 178 KB
/// — but the evidence used to let it through established nothing about it.</para>
///
/// <para>So the checks are tried first. One that FAILS then is proof. One that PASSES then is a
/// regression guard and nothing more. One that cannot run at all is dropped rather than carried
/// to the end to say the same thing again.</para>
/// </summary>
public sealed class BaselinedChecksTests
{
    /// <summary>A planner answer with one check in it, as the planner is now asked to produce.</summary>
    private static string PlanWith(string command)
        => $$"""
           {"disposition":"quick_action","title":"write the file","steps":[],
            "checks":[{"name":"the check","command":"{{command}}"}]}
           """;

    private static FakeChatProvider Worker(string command) => new(
        Turn.Says(PlanWith(command)),
        Turn.Calls1("write_file", """{"path":"notes.md","content":"# notes\n"}"""),
        Turn.Says("Done."),
        Turn.Says("I cannot make that check pass."),
        Turn.Says("Done."));

    /// <summary>
    /// THE ONE THIS EXISTS FOR. A check that was already true cannot be the reason a run the
    /// transcript could not call finished is called finished. It is still reported, marked.
    /// </summary>
    [Fact]
    public void A_check_that_was_already_passing_does_not_answer_an_Incomplete()
    {
        var report = new SuccessReport(new[]
        {
            new CriterionResult("the report is there", "dir Docs\\DRIFT.md", Required: true,
                                CriterionOutcome.Passed, 0, null,
                                CriterionOrigin.Proposed, AlreadyPassing: true)
        });

        Assert.Empty(report.Blocking);
        Assert.False(report.Proved);
        Assert.Equal(RunOutcomeKind.Incomplete, report.Apply(RunOutcomeKind.Incomplete));
        Assert.Contains("already passing", report.Describe(), StringComparison.Ordinal);
    }

    /// <summary>But one that was failing, and now passes, is exactly that reason.</summary>
    [Fact]
    public void A_check_that_was_failing_and_now_passes_answers_an_Incomplete()
    {
        var report = new SuccessReport(new[]
        {
            new CriterionResult("tests pass", "dotnet test", Required: true,
                                CriterionOutcome.Passed, 0, null, CriterionOrigin.Proposed)
        });

        Assert.True(report.Proved);
        Assert.Equal(RunOutcomeKind.Completed, report.Apply(RunOutcomeKind.Incomplete));
    }

    /// <summary>
    /// It is kept, not dropped: if the work BREAKS something that used to work, nothing else in
    /// the engine would notice. It can hold a run back; it just cannot let one through.
    /// </summary>
    [Fact]
    public void A_check_that_was_already_passing_still_catches_a_regression()
    {
        var report = new SuccessReport(new[]
        {
            new CriterionResult("it builds", "dotnet build", Required: true,
                                CriterionOutcome.Failed, 1, "error CS0246",
                                CriterionOrigin.Proposed, AlreadyPassing: true)
        });

        Assert.Equal(RunOutcomeKind.Failed, report.Apply(RunOutcomeKind.Completed));
    }

    // ── end to end, against the real shell ──────────────────────────────────

    /// <summary>
    /// The live shape of the reported run: the work happens, the check passes at the end, and it
    /// passed at the beginning too — so it decides nothing, and the run keeps the outcome the
    /// rest of the engine gave it.
    /// </summary>
    [Fact]
    public async Task A_check_true_before_the_run_does_not_turn_it_green()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(Worker("exit /b 0"), EngineFixture.Role("developer")),
            "write notes.md");

        var checks = events.Where(e => e.Kind == EventKind.CriterionEvaluated).ToArray();

        Assert.Single(checks);
        Assert.Equal("Passed", checks[0].CriterionOutcomeName());
        Assert.Contains(events, e => (e.Summary ?? "").Contains("already passes before any work",
                                                                StringComparison.Ordinal));
    }

    /// <summary>
    /// A check the shell will not start is NOT thrown away by the baseline. It keeps the meaning
    /// it would have had, and the end finds out what the beginning could not.
    ///
    /// <para>The first version of this dropped such a check, and the first live run showed what
    /// that costs: the shell policy asks, the console answers it with <c>--approve allow</c>, and
    /// the baseline was running behind a handler that always said no - so BOTH checks came back
    /// "the user did not permit this" and were deleted. The baseline destroyed the evidence it
    /// was added to grade. A baseline that cannot be taken is an absence of information about the
    /// check, never a finding against it.</para>
    /// </summary>
    [Fact]
    public async Task A_check_that_cannot_be_tried_beforehand_is_kept_not_thrown_away()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(Worker("Select-String -Path notes.md -Pattern notes"),
                     EngineFixture.Role("developer")),
            "write notes.md");

        // It survived to the end and was judged there - as Unknown, which blocks nothing because
        // nobody asked for it.
        var check = Assert.Single(events.Where(e => e.Kind == EventKind.CriterionEvaluated).ToArray());
        Assert.Equal("Unknown", check.CriterionOutcomeName());
        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// And the check that earns its place: failing before, passing after. This is the only shape
    /// that can call a run finished.
    /// </summary>
    [Fact]
    public async Task A_check_that_the_work_makes_pass_is_kept_as_proof()
    {
        using var fx = new EngineFixture();

        // Fails while notes.md is absent, passes once the step writes it.
        var events = await fx.RunAsync(
            fx.Build(Worker("dir notes.md"), EngineFixture.Role("developer")),
            "write notes.md");

        var check = Assert.Single(events.Where(e => e.Kind == EventKind.CriterionEvaluated).ToArray());

        Assert.Equal("Passed", check.CriterionOutcomeName());
        Assert.DoesNotContain(events, e => (e.Summary ?? "").Contains("already passes",
                                                                     StringComparison.Ordinal));
    }
}
