namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Builds;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// A worker that deletes a failing test makes "no test that passed now fails" true: the test is not failing, it is
/// not there. A test that passed before and is not named now is reported with the regressions - in the same finding,
/// at the same severity (advisory), since its disappearance may be deliberate.
/// </summary>
public sealed class ADeletedTestIsARegressionTests
{
    /// <summary>Reads "PASS name" / "FAIL name" lines as tests.</summary>
    private sealed class Lines : IEcosystem
    {
        public string Name => "lines";
        public EcosystemTargets? Detect(string workspaceRoot) => new(Name, ["lines.lint"], ["t"]);
        public bool Owns(string relativePath) => relativePath.EndsWith(".page", StringComparison.OrdinalIgnoreCase);
        public string BuildCommand(string target) => "echo built";
        public string TestCommand(string target) => "echo tests";
        public IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string output, string workspaceRoot) => [];

        public TestRunReport? ParseTests(string output)
        {
            var cases = output.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("PASS ", StringComparison.Ordinal) || l.StartsWith("FAIL ", StringComparison.Ordinal))
                .Select(l => new TestCaseResult(l[5..], l[0] == 'P' ? TestVerdict.Passed : TestVerdict.Failed))
                .ToArray();
            return cases.Length == 0 ? null : new TestRunReport(cases, null);
        }
    }

    private static readonly Lines Eco = new();

    private static CriterionResult Ran(string output, int? exit = 0)
        => new("tests", "run", false, CriterionOutcome.Failed, exit, exit is null ? "the command tool was not available" : null,
            CriterionOrigin.System) { Output = output };

    private static CriterionResult Compare(string before, string after, CriterionResult? again = null)
    {
        var baseline = new BuildBaseline(Eco, "t", 0, null, null) { Kind = BaselineKind.Test, Tests = Eco.ParseTests(before)! };
        return BuildRegression.Compare(baseline, Ran(after), "C:/w", again: again);
    }

    // ── confirmed by the second run: a disappearance is not believed on one run ───────

    [Fact]
    public void A_test_the_second_run_names_again_has_not_disappeared()
    {
        var result = Compare("PASS t1\nPASS t2", "PASS t1", again: Ran("PASS t1\nPASS t2"));

        Assert.Equal(CriterionOutcome.Passed, result.Outcome);
        Assert.Contains("1 test(s) that passed before the work were not run the first time and ran when run again - not counted: t2",
            result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_test_the_second_run_does_not_name_either_has_disappeared()
    {
        var result = Compare("PASS t1\nPASS t2", "PASS t1", again: Ran("PASS t1"));

        Assert.Equal(CriterionOutcome.Failed, result.Outcome);
        Assert.Contains("1 test(s) passed before the work and were not run now (deleted, renamed or no longer run): t2",
            result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_run_that_did_not_happen_confirms_no_disappearance()
    {
        var result = Compare("PASS t1\nPASS t2", "PASS t1", again: Ran("", exit: null));

        Assert.Equal(CriterionOutcome.Unknown, result.Outcome);
        Assert.Contains("not confirmed by a second run (it did not run: the command tool was not available): t2",
            result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_run_that_names_no_test_confirms_no_disappearance()
    {
        var result = Compare("PASS t1\nPASS t2", "PASS t1", again: Ran("build failed"));

        Assert.Equal(CriterionOutcome.Unknown, result.Outcome);
        Assert.Contains("nothing it printed names a test): t2", result.Detail, StringComparison.Ordinal);
    }

    // ── the cap ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_five_missing_tests_are_named_and_the_rest_counted()
    {
        var names = Enumerable.Range(1, 7).Select(i => $"t{i}").ToArray();
        var result = Compare(string.Join("\n", names.Select(n => "PASS " + n)) + "\nPASS keep", "PASS keep");

        Assert.Contains("7 test(s) passed before the work and were not run now (deleted, renamed or no longer run): "
                        + "t1, t2, t3, t4, t5, and 2 more.", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("t6", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_test_that_passed_and_is_not_named_now_is_named_in_the_finding()
    {
        var result = Compare("PASS t1\nPASS t2\nPASS t3", "PASS t1\nPASS t3");

        Assert.Equal(CriterionOutcome.Failed, result.Outcome);
        Assert.False(result.Required);
        Assert.Equal(CriterionOrigin.System, result.Origin);
        Assert.Contains("1 test(s) passed before the work and were not run now (deleted, renamed or no longer run): t2",
            result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_regression_and_a_disappearance_are_in_one_finding()
    {
        var result = Compare("PASS t1\nPASS t2\nPASS t3", "FAIL t1\nPASS t3");

        Assert.Equal(CriterionOutcome.Failed, result.Outcome);
        Assert.Contains("1 test(s) passed before the work and fail now: t1", result.Detail, StringComparison.Ordinal);
        Assert.Contains("1 test(s) passed before the work and were not run now (deleted, renamed or no longer run): t2",
            result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void With_nothing_missing_the_message_is_unchanged()
    {
        var result = Compare("PASS t1\nPASS t2", "PASS t1\nPASS t2\nPASS t3");

        Assert.Equal(CriterionOutcome.Passed, result.Outcome);
        Assert.StartsWith("no test that passed before the work fails now (", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("not run now", result.Detail, StringComparison.Ordinal);
    }
}
