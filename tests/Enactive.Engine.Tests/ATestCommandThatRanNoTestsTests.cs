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
