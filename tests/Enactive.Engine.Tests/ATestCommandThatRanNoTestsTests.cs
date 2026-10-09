namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// A final check that runs the tests and runs none is not a pass. On 2026-10-08 a requested check, 'dotnet test' at a
/// workspace root whose solution holds no test project, printed only that the projects were up to date, exited 0, and
/// was a PASS - the very case the request had said was not one ("if it exits 0 and prints nothing at all it ran no
/// tests"). Which command runs tests, and how its output reads, is the ecosystem's to say (IEcosystem.RunsTests).
/// </summary>
public sealed class ATestCommandThatRanNoTestsTests
{
    private const string NothingRan = "  Determining projects to restore...\n  All projects are up-to-date for restore.";
    private const string ThreeRan = "Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 12 ms - Game.Tests.dll (net8.0)";

    /// <summary>A shell that answers every check with the same output and exit 0.</summary>
    private sealed class Shell(string output) : ITool
    {
        public ToolDefinition Definition => new("run_command", "run", "{\"type\":\"object\"}", Kind: ToolKind.Command, RunsSuccessChecks: true);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
            => Task.FromResult(new ToolResult(true, output, null, [], new Dictionary<string, object?> { ["exitCode"] = 0 }));
    }

    private static async Task<(WorkEvent Check, RunOutcomeKind Outcome)> Run(string output, string command = "dotnet test", bool knowsTests = true)
    {
        using var fx = new EngineFixture { EcosystemsOverride = knowsTests ? [new DotnetEcosystem()] : [] };
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(new Shell(output)).ToArray();
        var events = await fx.RunAsync(fx.Build(new FakeChatProvider(
                Turn.Says("""{"disposition":"quick_action","title":"tests"}"""), Turn.Says("Done.")),
            successCriteria: [new("tests", command)]), "Add a test.");
        return (Assert.Single(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains("tests", StringComparison.Ordinal)
            && !e.Summary.Contains("[engine check]", StringComparison.Ordinal)), events.Last().Outcome()!.Value);
    }

    private static async Task<WorkEvent> Checked(string output, string command = "dotnet test", bool knowsTests = true)
        => (await Run(output, command, knowsTests)).Check;

    /// <summary>Not a pass and not a failure - it found nothing out; a check a person asked for that found nothing out leaves the run incomplete.</summary>
    [Fact]
    public async Task A_test_command_that_ran_no_tests_is_not_a_pass()
    {
        var (check, outcome) = await Run(NothingRan);

        Assert.StartsWith("NOT CHECKED", check.Summary, StringComparison.Ordinal);
        Assert.Contains("runs dotnet's tests and exited 0, but nothing it printed is a test it ran", check.Summary, StringComparison.Ordinal);
        Assert.Equal(RunOutcomeKind.Incomplete, outcome);
    }

    [Fact]
    public async Task One_that_ran_its_tests_passes()
        => Assert.StartsWith("PASS", (await Checked(ThreeRan)).Summary, StringComparison.Ordinal);

    /// <summary>What does not run tests is judged by its exit code, as before - and so is everything where no ecosystem can tell.</summary>
    [Theory]
    [InlineData("dotnet build", true)]
    [InlineData("dotnet test", false)]
    public async Task What_is_not_a_test_run_is_judged_by_its_exit_code(string command, bool knowsTests)
        => Assert.StartsWith("PASS", (await Checked(NothingRan, command, knowsTests)).Summary, StringComparison.Ordinal);

    // ── why it ran none ───────────────────────────────────────────────────

    private const string TestProject = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup></Project>""";
    private const string AppProject = """<Project Sdk="Microsoft.NET.Sdk"></Project>""";

    private static void Workspace(EngineFixture fx, bool listsTheTests)
    {
        fx.Write("Game/Game.csproj", AppProject);
        fx.Write("Game.Tests/Game.Tests.csproj", TestProject);
        fx.Write("Game.sln", "Project(\"{FAE04EC0}\") = \"Game\", \"Game\\Game.csproj\", \"{1}\"\nEndProject\n"
            + (listsTheTests ? "Project(\"{FAE04EC0}\") = \"Game.Tests\", \"Game.Tests\\Game.Tests.csproj\", \"{2}\"\nEndProject\n" : ""));
    }

    /// <summary>
    /// Runs 1549ce and 955111, 2026-10-09: a test project made with `dotnet new` and never added to the solution, so
    /// every `dotnet test` at the root ran nothing - and the check said only that. It now says why, and what to change.
    /// </summary>
    [Fact]
    public async Task A_test_project_the_solution_does_not_list_is_named()
    {
        using var fx = new EngineFixture { EcosystemsOverride = [new DotnetEcosystem()] };
        Workspace(fx, listsTheTests: false);
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(new Shell(NothingRan)).ToArray();

        var events = await fx.RunAsync(fx.Build(new FakeChatProvider(
                Turn.Says("""{"disposition":"quick_action","title":"tests"}"""), Turn.Says("Done.")),
            successCriteria: [new("tests", "dotnet test")]), "Add a test.");

        var check = Assert.Single(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.StartsWith("NOT CHECKED", StringComparison.Ordinal));
        Assert.Contains("Game.Tests/Game.Tests.csproj is a test project the solution Game.sln does not list", check.Summary, StringComparison.Ordinal);
        Assert.Contains("dotnet sln Game.sln add", check.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "dotnet test")]               // the solution lists it: nothing to say
    [InlineData(false, "dotnet test Game.Tests")]   // the command names its own target
    [InlineData(false, "dotnet build")]             // not a test run
    public void Nothing_is_said_where_the_solution_is_not_why(bool listsTheTests, string command)
    {
        using var fx = new EngineFixture();
        Workspace(fx, listsTheTests);

        Assert.Null(new DotnetEcosystem().WhyNoTests(command, fx.Workspace.RootPath));
    }

    [Fact]
    public void The_bare_form_with_options_is_still_the_bare_form()
    {
        using var fx = new EngineFixture();
        Workspace(fx, listsTheTests: false);

        Assert.NotNull(new DotnetEcosystem().WhyNoTests("dotnet test --nologo", fx.Workspace.RootPath));
    }

    [Theory]
    [InlineData("dotnet test", true)]
    [InlineData("  DOTNET   test \"Game.Tests/Game.Tests.csproj\" -nologo", true)]
    [InlineData("dotnet.exe test", true)]
    [InlineData("dotnet build", false)]
    [InlineData("dotnet testing", false)]
    [InlineData("npm test", false)]
    public void Dotnet_knows_its_test_runner(string command, bool runs)
        => Assert.Equal(runs, new DotnetEcosystem().RunsTests(command));
}
