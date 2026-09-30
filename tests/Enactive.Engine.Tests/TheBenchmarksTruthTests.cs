namespace Enactive.Engine.Tests;

using Enactive.Bench;
using Xunit;

/// <summary>
/// The live benchmark (plan phase 0) judges a run by the files it left, not by the engine's word - so its own reading of
/// those files is what these check: which files a mask names, a number read from a runner's summary, what a coverage
/// check counts, and that a file changed is told from one left alone. Whether each scenario's truth fails on its fixture
/// and holds on its reference solution is checked by the bench itself (--fixtures, --solutions), since that builds the
/// fixtures' projects.
/// </summary>
public sealed class TheBenchmarksTruthTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("enactive-bench-truth-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Theory]
    [InlineData("**/*.{md,txt}", "report.md", true)]
    [InlineData("**/*.{md,txt}", "Docs/notes/drift.txt", true)]
    [InlineData("**/*.{md,txt}", "report.json", false)]
    [InlineData("Docs/*.md", "Docs/wiki/Page.md", false)]
    [InlineData("Shelf.Tests/**/*.cs", "Shelf.Tests/InventoryTests.cs", true)]
    [InlineData("**/obj/**", "Shelf.Tests/obj/Debug/x.cs", true)]
    public void A_mask_names_the_files_it_should(string glob, string path, bool named)
        => Assert.Equal(named, Truth.GlobRegex(glob).IsMatch(path));

    [Fact]
    public void Files_leave_out_what_is_excluded_and_never_look_in_git()
    {
        Write("report.md", "r");
        Write("README.md", "r");
        Write(".enactive/scratch/notes.md", "n");
        Write(".git/info.md", "g");

        Assert.Equal(["report.md"], Truth.Files(_root, "**/*.md", ["README.md", ".enactive/**"]));
    }

    [Fact]
    public void A_number_is_read_from_the_last_summary_line()
        => Assert.Equal(6, Truth.Number("Passed: 2 of the first run\n...\nPassed!  - Failed: 0, Passed: 6, Skipped: 0", @"Passed:\s*(\d+)"));

    [Fact]
    public async Task A_coverage_check_counts_what_was_found_and_names_what_was_missed()
    {
        var fixture = Directory.CreateTempSubdirectory("enactive-bench-fixture-").FullName;
        try
        {
            Write("Docs/DRIFT.md", "The page says 60 s; the code's timeout is 30. The flag is --debug, not --verbose.");
            var scenario = new Scenario("s", "r", "deny", new Expectation("Completed"),
                [new Check("found", "coverage", Glob: "Docs/DRIFT.md", Min: 3, Items:
                    [new CoverageItem("timeout", @"(?is)timeout.{0,300}\b30\b"), new CoverageItem("debug", "--debug"),
                     new CoverageItem("env", "SYNC_HOME")])]) { Folder = Path.GetDirectoryName(fixture)! };

            var result = Assert.Single(await Truth.CheckAsync(scenario, _root, CancellationToken.None));

            Assert.False(result.Passed);
            Assert.Equal((2, 3), (result.Found, result.OutOf));
            Assert.Contains("missed: env", result.Detail, StringComparison.Ordinal);
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Fact]
    public async Task A_file_changed_is_told_from_one_left_alone()
    {
        var scenarioFolder = Directory.CreateTempSubdirectory("enactive-bench-scenario-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(scenarioFolder, "fixture"));
            File.WriteAllText(Path.Combine(scenarioFolder, "fixture", "a.txt"), "as it was");
            File.WriteAllText(Path.Combine(scenarioFolder, "fixture", "b.txt"), "as it was");
            Write("a.txt", "as it was");
            Write("b.txt", "changed");
            var scenario = new Scenario("s", "r", "deny", new Expectation("Completed"),
                [new Check("a", "unchanged", Path: "a.txt"), new Check("b", "unchanged", Path: "b.txt")]) { Folder = scenarioFolder };

            var results = await Truth.CheckAsync(scenario, _root, CancellationToken.None);

            Assert.True(results[0].Passed);
            Assert.False(results[1].Passed);
        }
        finally { Directory.Delete(scenarioFolder, true); }
    }

    /// <summary>Every scenario in the repository reads, and has a request and something to judge it by.</summary>
    [Fact]
    public void Every_scenario_in_the_repository_loads()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "bench", "scenarios"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var scenarios = Directory.GetDirectories(Path.Combine(root!, "bench", "scenarios")).Select(Scenario.Load).ToArray();

        Assert.True(scenarios.Length >= 4);
        Assert.All(scenarios, s => Assert.True(Directory.Exists(s.FixtureFolder), s.Name));
    }
}
