namespace Enactive.Engine.Tests;

using Enactive.Bench;
using Xunit;

/// <summary>
/// A live run is judged by its scenario's truth, and a truth that turns down the scenario's own reference solution on
/// this machine cannot judge it: on 2026-10-28 the C# scenarios' tests could not start for want of the .NET 8 runtime,
/// the truth failed on the reference solution as on the run, and the bench scored a FALSE PASS that was no engine's -
/// while spending a live run's tokens on it. So the solution is judged first, and such a scenario is not run.
/// </summary>
public sealed class TheBenchmarksReadinessTests : IDisposable
{
    private readonly string _scenarioFolder = Directory.CreateTempSubdirectory("enactive-bench-scenario-").FullName;
    private readonly string _scratch = Directory.CreateTempSubdirectory("enactive-bench-scratch-").FullName;

    public void Dispose()
    {
        foreach (var dir in new[] { _scenarioFolder, _scratch })
            try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_scenarioFolder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>A scenario whose truth asks report.md to say "done"; the solution's says what it is given.</summary>
    private Scenario WithSolutionSaying(string? solutionText)
    {
        Write("fixture/readme.txt", "the project");
        if (solutionText is not null) Write("solution/report.md", solutionText);
        return new Scenario("s", "r", "deny", new Expectation("Completed"),
            [new Check("report-done", "file_contains", Glob: "report.md", Patterns: ["done"])]) { Folder = _scenarioFolder };
    }

    [Fact]
    public async Task A_scenario_whose_truth_turns_down_its_solution_is_not_run()
    {
        var result = await Readiness.NotRunAsync(WithSolutionSaying("not finished"), _scratch, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(Readiness.NotRun, result.Verdict);
        Assert.False(result.Verdict.StartsWith("FALSE", StringComparison.Ordinal));
        Assert.Contains("reference solution", result.Note, StringComparison.Ordinal);
        Assert.Contains(result.Checks, c => c.Id == "report-done" && !c.Passed);
    }

    [Fact]
    public async Task A_scenario_whose_truth_holds_on_its_solution_is_run()
        => Assert.Null(await Readiness.NotRunAsync(WithSolutionSaying("done"), _scratch, CancellationToken.None));

    [Fact]
    public async Task A_scenario_with_no_solution_is_run()
        => Assert.Null(await Readiness.NotRunAsync(WithSolutionSaying(null), _scratch, CancellationToken.None));

    [Fact]
    public async Task The_solution_is_judged_on_a_copy_and_the_scenario_is_left_as_it_was()
    {
        var scenario = WithSolutionSaying("not finished");

        await Readiness.NotRunAsync(scenario, _scratch, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(scenario.FixtureFolder, "report.md")));
    }

    [Fact]
    public async Task The_summary_counts_a_scenario_not_run_neither_as_a_false_verdict_nor_as_a_task()
    {
        var notRun = (await Readiness.NotRunAsync(WithSolutionSaying("not finished"), _scratch, CancellationToken.None))!;
        var ran = new ScenarioResult("other", "Completed", "Completed", true, "right PASS", 0, 1, null, null, [], "%TEMP%\\x");

        var markdown = Summary.Render(new BenchSummary("t", "c", false, DateTimeOffset.Now, [notRun, ran]), null);

        Assert.Contains("False PASS: 0, false FAIL: 0, task success (truth holds): 1 of 1.", markdown, StringComparison.Ordinal);
        Assert.Contains("Not run here: 1", markdown, StringComparison.Ordinal);
        Assert.Contains(notRun.Note!, markdown, StringComparison.Ordinal);
    }
}
