namespace Enactive.Core.Builds;

/// <summary>How one test came out.</summary>
public enum TestVerdict { Passed, Failed, Skipped }

/// <summary>One test, by the name its runner printed, and how it came out.</summary>
public sealed record TestCaseResult(string Name, TestVerdict Verdict);

/// <summary>The totals a test run printed at its end.</summary>
public sealed record TestRunSummary(int Passed, int Failed, int Skipped, int Total);

/// <summary>
/// What one run of a test target reported: the tests it named, and its totals when it printed them.
///
/// <para><b>Compared by name, and only what was seen twice.</b> A test that PASSED before the work
/// and FAILS now is a regression: the work broke it. A test failing now that was not in the run
/// before is not one - it may be exactly what the work was asked to add, a test written to fail on
/// a known bug - so it is reported, not counted against the run. And a test the output did not name,
/// because it was cut or its runner does not list passing tests, is not guessed about in either
/// direction.</para>
/// </summary>
public sealed record TestRunReport(IReadOnlyList<TestCaseResult> Cases, TestRunSummary? Summary)
{
    /// <summary>Each name once: a test that failed in any of its appearances failed.</summary>
    private Dictionary<string, TestVerdict> ByName()
    {
        var verdicts = new Dictionary<string, TestVerdict>(StringComparer.Ordinal);
        foreach (var test in Cases)
            verdicts[test.Name] = verdicts.TryGetValue(test.Name, out var was) && was == TestVerdict.Failed
                ? TestVerdict.Failed : test.Verdict;
        return verdicts;
    }

    /// <summary>Tests that passed in <paramref name="before"/> and fail now.</summary>
    public IReadOnlyList<string> Regressions(TestRunReport before)
    {
        var was = before.ByName();
        return ByName().Where(t => t.Value == TestVerdict.Failed
                                   && was.TryGetValue(t.Key, out var verdict) && verdict == TestVerdict.Passed)
            .Select(t => t.Key).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Tests failing now that <paramref name="before"/> did not name at all.</summary>
    public IReadOnlyList<string> FailingAndNew(TestRunReport before)
    {
        var was = before.ByName();
        return ByName().Where(t => t.Value == TestVerdict.Failed && !was.ContainsKey(t.Key))
            .Select(t => t.Key).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The totals, or counted from the named tests when the run printed none.</summary>
    public string Describe()
    {
        var (passed, failed) = Summary is { } s
            ? (s.Passed, s.Failed)
            : (ByName().Count(t => t.Value == TestVerdict.Passed), ByName().Count(t => t.Value == TestVerdict.Failed));
        return $"{passed} passed, {failed} failed";
    }
}

/// <summary>
/// One entry of a workspace baseline as data: what one ecosystem's build or tests of one target
/// reported before any work, or why it was not taken. Written with a run's checkpoint, and beside
/// the task, so a run carried on - resumed, or started again after a question - compares against the
/// workspace as it was BEFORE the first attempt, not as the first attempt left it.
/// </summary>
/// <param name="Kind">"Build" or "Test".</param>
public sealed record BaselineSnapshot(
    string Ecosystem,
    string Kind,
    string Target,
    int? ExitCode,
    string? NotTaken,
    IReadOnlyList<BuildDiagnostic>? Diagnostics = null,
    TestRunReport? Tests = null);
