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

    private static CriterionResult Compare(string before, string after)
    {
        var baseline = new BuildBaseline(Eco, "t", 0, null, null) { Kind = BaselineKind.Test, Tests = Eco.ParseTests(before)! };
        var ran = new CriterionResult("tests", "run", false, CriterionOutcome.Failed, 0, null, CriterionOrigin.System) { Output = after };
        return BuildRegression.Compare(baseline, ran, "C:/w");
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
