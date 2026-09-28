namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Builds;
using Enactive.Core.Events;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Deferred item B: on this machine one test suite gave 41, 42, 43 and 46 failures on the same code, so
/// "a test that passed now fails" is not believed on one run. A test that fails is run again, and only
/// what fails both times is the work's.
/// </summary>
public sealed class AFlakyTestIsRunAgainTests
{
    /// <summary>
    /// A "test run" that alternates: with <c>flip.flag</c> present it passes and removes the flag; without
    /// it, it fails and leaves the flag. A test that fails and then passes - flaky by construction.
    /// </summary>
    private sealed class Flip : IEcosystem
    {
        private readonly bool _alwaysFails;
        public Flip(bool alwaysFails = false) => _alwaysFails = alwaysFails;

        public string Name => "flip";

        public EcosystemTargets? Detect(string workspaceRoot) => new(Name, ["flip.lint"], ["t"]);

        public bool Owns(string relativePath) => relativePath.EndsWith(".page", StringComparison.OrdinalIgnoreCase);

        public string BuildCommand(string target) => "echo built";

        public string TestCommand(string target) => _alwaysFails
            ? "if exist flip.flag (del flip.flag & echo PASS t1) else (echo FAIL t1)"
            : "if exist flip.flag (del flip.flag & echo PASS t1) else (echo x>flip.flag & echo FAIL t1)";

        public IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string output, string workspaceRoot) => [];

        public TestRunReport? ParseTests(string output)
        {
            var cases = output.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("PASS ", StringComparison.Ordinal) || l.StartsWith("FAIL ", StringComparison.Ordinal)
                            || l.StartsWith("SKIP ", StringComparison.Ordinal))
                .Select(l => new TestCaseResult(l[5..], l[0] switch { 'P' => TestVerdict.Passed, 'F' => TestVerdict.Failed, _ => TestVerdict.Skipped }))
                .ToArray();
            return cases.Length == 0 ? null : new TestRunReport(cases, null);
        }
    }

    private static async Task<WorkEvent> TestCheckAsync(bool alwaysFails)
    {
        using var fx = new EngineFixture { EcosystemsOverride = [new Flip(alwaysFails)] };
        fx.Write("flip.flag", "x");                                                 // the baseline passes
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"edit a page"}"""),
            Turn.Calls1("write_file", """{"path":"a.page","content":"# A"}""", "w1"),
            Turn.Says("Wrote the page."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "edit a page");
        return Assert.Single(events, e => e.Kind == EventKind.CriterionEvaluated
                                          && e.Summary.Contains(BuildRegression.TestName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_test_that_fails_once_and_then_passes_is_not_counted()
    {
        var check = await TestCheckAsync(alwaysFails: false);
        Assert.StartsWith("PASS", check.Summary, StringComparison.Ordinal);
        Assert.Contains("1 test(s) failed and then passed when run again - flaky, not counted: t1", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_that_fails_both_times_is_the_works()
    {
        var check = await TestCheckAsync(alwaysFails: true);
        Assert.StartsWith("FAIL", check.Summary, StringComparison.Ordinal);
        Assert.Contains("1 test(s) passed before the work and fail now: t1, both times it ran", check.Summary, StringComparison.Ordinal);
    }

    // ── the second run, read strictly (review P2) ─────────────────────────────────────

    private static readonly Flip Eco = new();

    private static CriterionResult Ran(string output, int? exit = 0)
        => new("tests", "run", false, CriterionOutcome.Failed, exit, exit is null ? "the command tool was not available" : null,
            CriterionOrigin.System) { Output = output };

    private static CriterionResult Compare(string first, CriterionResult again)
    {
        var before = new BuildBaseline(Eco, "t", 0, null, null)
            { Kind = BaselineKind.Test, Tests = Eco.ParseTests("PASS t1\nPASS t2")! };
        return BuildRegression.Compare(before, Ran(first), "C:/w", again: again);
    }

    [Fact]
    public void A_test_the_second_run_skipped_is_not_flaky_it_is_unconfirmed()
    {
        var result = Compare("FAIL t1\nPASS t2", Ran("SKIP t1\nPASS t2"));
        Assert.Equal(CriterionOutcome.Unknown, result.Outcome);
        Assert.DoesNotContain("flaky", result.Detail, StringComparison.Ordinal);
        Assert.Contains("1 test(s) failed and were not confirmed by a second run (it skipped them or did not name them): t1", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_test_the_second_run_did_not_name_is_unconfirmed()
    {
        var result = Compare("FAIL t1\nPASS t2", Ran("PASS t2"));
        Assert.Equal(CriterionOutcome.Unknown, result.Outcome);
        Assert.Contains("not confirmed", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_run_that_did_not_happen_confirms_nothing()
    {
        var result = Compare("FAIL t1\nPASS t2", Ran("", exit: null));
        Assert.Equal(CriterionOutcome.Unknown, result.Outcome);
        Assert.DoesNotContain("both times", result.Detail, StringComparison.Ordinal);
        Assert.Contains("(it did not run: the command tool was not available): t1", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_twice_is_counted_and_what_was_not_confirmed_is_said_beside_it()
    {
        var result = Compare("FAIL t1\nFAIL t2", Ran("FAIL t1\nSKIP t2"));
        Assert.Equal(CriterionOutcome.Failed, result.Outcome);
        Assert.Contains("1 test(s) passed before the work and fail now: t1, both times it ran", result.Detail, StringComparison.Ordinal);
        Assert.Contains("not confirmed by a second run (it skipped them or did not name them): t2", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_test_that_passed_the_second_time_is_flaky()
    {
        var result = Compare("FAIL t1\nPASS t2", Ran("PASS t1\nPASS t2"));
        Assert.Equal(CriterionOutcome.Passed, result.Outcome);
        Assert.Contains("flaky, not counted: t1", result.Detail, StringComparison.Ordinal);
    }
}
