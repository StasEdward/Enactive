namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// A test run is answered with what it came to - the totals and each failure, with its message and where - not with the
/// log. Run 97de74b1, 2026-10-09: a model ran its tests through run_command and read 30,000 characters of MSBuild and
/// xUnit log in three pieces to find seven failures; a third of its prompt was that log. run_tests runs the workspace's
/// tests the way its ecosystem runs them and reads the result for the model; a run through the shell is read the same.
/// </summary>
public sealed class TestsRunAreDescribedTests
{
    // Captured on this machine on 2026-10-09: `dotnet test Demo.Tests.csproj -nologo --logger "console;verbosity=normal"`,
    // xUnit, one test passing and two failing - one an assertion, one an exception with a two-line message. Paths shortened.
    private const string TwoFailed = """
          Determining projects to restore...
          All projects are up-to-date for restore.
          Demo.Tests -> C:\src\Demo.Tests\bin\Debug\net10.0\Demo.Tests.dll
        Test run for C:\src\Demo.Tests\bin\Debug\net10.0\Demo.Tests.dll (.NETCoreApp,Version=v10.0)
        A total of 1 test files matched the specified pattern.
        [xUnit.net 00:00:00.00] xUnit.net VSTest Adapter v3.1.4+50e68bbb8b (64-bit .NET 10.0.12)
        [xUnit.net 00:00:00.26]     Demo.Tests.BoardTests.Winner_is_found_on_a_row [FAIL]
        [xUnit.net 00:00:00.26]       Assert.Equal() Failure: Values differ
        [xUnit.net 00:00:00.26]   Finished:    Demo.Tests
          Passed Demo.Tests.BoardTests.Empty_board_has_nine_cells [49 ms]
          Failed Demo.Tests.BoardTests.Winner_is_found_on_a_row [1 ms]
          Error Message:
           Assert.Equal() Failure: Values differ
        Expected: 1
        Actual:   2
          Stack Trace:
             at Demo.Tests.BoardTests.Winner_is_found_on_a_row() in C:\src\Demo.Tests\UnitTest1.cs:line 9
           at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
          Failed Demo.Tests.BoardTests.A_full_board_is_a_draw [< 1 ms]
          Error Message:
           System.InvalidOperationException : line one
        line two
          Stack Trace:
             at Demo.Tests.BoardTests.A_full_board_is_a_draw() in C:\src\Demo.Tests\UnitTest1.cs:line 12
           at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)

        Test Run Failed.
        Total tests: 3
             Passed: 1
             Failed: 2
         Total time: 0.7260 Seconds
        """;

    private const string BuildFailed = """
          Determining projects to restore...
        C:\src\Demo.Tests\UnitTest1.cs(9,40): error CS0103: The name 'Board' does not exist in the current context [C:\src\Demo.Tests\Demo.Tests.csproj]

        Build FAILED.
        """;

    // ── the ecosystem reads a run ────────────────────────────────────────

    [Fact]
    public void A_run_is_described_by_its_totals_and_each_failure_with_its_message_and_place()
    {
        var described = new DotnetEcosystem().DescribeTests(TwoFailed);

        Assert.Equal("""
            3 tests: 1 passed, 2 failed.
            Failed:
            - Demo.Tests.BoardTests.Winner_is_found_on_a_row: Assert.Equal() Failure: Values differ / Expected: 1 / Actual:   2 (UnitTest1.cs:9)
            - Demo.Tests.BoardTests.A_full_board_is_a_draw: System.InvalidOperationException : line one / line two (UnitTest1.cs:12)
            """.Replace("\r", ""), described);
    }

    /// <summary>A theory's case has a name with spaces in it: `Is_positive(n: 2)`. Read only up to the space, it was not read at all.</summary>
    [Fact]
    public void A_failed_case_whose_name_has_spaces_is_named_whole()
    {
        var output = "  Failed Probe.Sums.Is_positive(n: 2) [3 ms]\n  Error Message:\n   Assert.True() Failure\n"
                   + "  Stack Trace:\n     at Probe.Sums.Is_positive(Int32 n) in C:\\src\\Sums.cs:line 7\n"
                   + "Total tests: 1\n     Failed: 1\n";

        Assert.Contains("- Probe.Sums.Is_positive(n: 2): Assert.True() Failure (Sums.cs:7)", new DotnetEcosystem().DescribeTests(output));
    }

    [Fact]
    public void What_is_not_a_test_run_is_not_described()
        => Assert.Null(new DotnetEcosystem().DescribeTests(BuildFailed));

    /// <summary>DotnetEcosystem.FailuresDescribed: a run where everything failed names the first ones and says how many more.</summary>
    [Fact]
    public void Past_the_failures_described_the_rest_are_counted_and_said_to_be_in_the_whole_output()
    {
        var n = DotnetEcosystem.FailuresDescribed + 5;
        var output = string.Concat(Enumerable.Range(1, n).Select(i =>
                         $"  Failed T.Case{i} [1 ms]\n  Error Message:\n   boom {i}\n  Stack Trace:\n     at T.Case{i}() in C:\\src\\T.cs:line {i}\n"))
                     + $"Total tests: {n}\n     Failed: {n}\n";

        var described = new DotnetEcosystem().DescribeTests(output)!;

        Assert.Contains($"- T.Case{DotnetEcosystem.FailuresDescribed}: boom", described);
        Assert.DoesNotContain($"- T.Case{DotnetEcosystem.FailuresDescribed + 1}:", described);
        Assert.Contains("... and 5 more failed; the whole output names them.", described);
    }

    /// <summary>DotnetEcosystem.FailureMessageChars: a message as long as a dumped object is cut, and says so.</summary>
    [Fact]
    public void A_long_failure_message_is_cut_and_says_so()
    {
        var output = $"  Failed T.Long [1 ms]\n  Error Message:\n   {new string('x', DotnetEcosystem.FailureMessageChars + 100)}\n"
                   + "  Stack Trace:\n     at T.Long() in C:\\src\\T.cs:line 3\nTotal tests: 1\n     Failed: 1\n";

        var described = new DotnetEcosystem().DescribeTests(output)!;

        Assert.Contains(new string('x', DotnetEcosystem.FailureMessageChars) + " …(cut) (T.cs:3)", described);
        Assert.DoesNotContain(new string('x', DotnetEcosystem.FailureMessageChars + 1), described);
    }

    [Fact]
    public void A_filter_runs_the_tests_whose_name_contains_it()
        => Assert.Equal("dotnet test \"T/T.csproj\" -nologo --logger \"console;verbosity=normal\" --filter \"FullyQualifiedName~Board\"",
            new DotnetEcosystem().TestCommand("T/T.csproj", "Bo\"ard"));

    // ── run_tests ─────────────────────────────────────────────────────────

    private const string TestProject = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup></Project>""";

    /// <summary>The shell run_tests hands its command to: answers with what it was given, and keeps the commands.</summary>
    private sealed class Runner(string output, int exitCode = 1, string? kept = null) : ITool
    {
        public List<string> Commands { get; } = [];
        public ToolDefinition Definition => new("run_command", "run", "{\"type\":\"object\"}", Kind: ToolKind.Command);
        public PermissionLevel RequiredLevel => PermissionLevel.Execute;

        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            Commands.Add(doc.RootElement.GetProperty("command").GetString()!);
            var metadata = new Dictionary<string, object?> { ["exitCode"] = exitCode };
            if (kept is not null) metadata[CommandOutput.KeptKey] = kept;
            return Task.FromResult(new ToolResult(true, $"exit code {exitCode}\n{CommandOutput.Marker}{output}", null, [], metadata));
        }
    }

    private static async Task<(ToolResult Result, Runner Runner)> RunTests(EngineFixture fx, Runner runner, string args = "{}")
    {
        var context = new ToolContext(Guid.NewGuid(), Guid.NewGuid(), fx.Workspace.Id, null!,
            PermissionPolicy.PermissiveDefault, fx.Root, new StagingArtifactStore(fx.Root).BeginStep());
        return (await new RunTestsTool([new DotnetEcosystem()], runner).InvokeAsync(args, context, default), runner);
    }

    [Fact]
    public async Task Run_tests_answers_with_the_failures_and_failing_tests_are_its_finding_not_its_failure()
    {
        using var fx = new EngineFixture();
        fx.Write("Demo.Tests/Demo.Tests.csproj", TestProject);

        var (result, runner) = await RunTests(fx, new Runner(TwoFailed));

        Assert.True(result.Success);
        Assert.Equal(true, result.Metadata["testsFailed"]);
        Assert.Contains("3 tests: 1 passed, 2 failed.", result.Output);
        Assert.Contains("Winner_is_found_on_a_row: Assert.Equal() Failure", result.Output);
        Assert.DoesNotContain("Determining projects", result.Output);
        Assert.Contains("dotnet test", Assert.Single(runner.Commands));
        Assert.Contains("Demo.Tests", runner.Commands[0]);
    }

    /// <summary>Where the output was too long to show, the failures are read from the file it was kept in, not from the cut.</summary>
    [Fact]
    public async Task A_run_too_long_to_show_is_read_from_where_its_output_is_kept()
    {
        using var fx = new EngineFixture();
        fx.Write("Demo.Tests/Demo.Tests.csproj", TestProject);
        fx.Write(".enactive/scratch/output/run.txt", TwoFailed);

        var (result, _) = await RunTests(fx, new Runner("… (cut)", kept: ".enactive/scratch/output/run.txt"));

        Assert.Contains("A_full_board_is_a_draw: System.InvalidOperationException", result.Output);
        Assert.Contains("The whole output is kept in .enactive/scratch/output/run.txt.", result.Output);
    }

    [Fact]
    public async Task A_filter_reaches_the_command()
    {
        using var fx = new EngineFixture();
        fx.Write("Demo.Tests/Demo.Tests.csproj", TestProject);

        var (_, runner) = await RunTests(fx, new Runner(TwoFailed), """{"filter":"Board"}""");

        Assert.EndsWith("--filter \"FullyQualifiedName~Board\"", Assert.Single(runner.Commands));
    }

    /// <summary>A build that failed ran no tests: that is a failure, and it says which errors stopped it.</summary>
    [Fact]
    public async Task A_build_that_failed_is_a_failure_that_names_its_errors()
    {
        using var fx = new EngineFixture();
        fx.Write("Demo.Tests/Demo.Tests.csproj", TestProject);

        var (result, _) = await RunTests(fx, new Runner(BuildFailed));

        Assert.False(result.Success);
        Assert.Contains("The tests did not run: the build failed.", result.Error);
        Assert.Contains("CS0103 The name 'Board' does not exist in the current context", result.Error);
    }

    [Fact]
    public async Task Where_no_tests_are_known_it_says_so_and_runs_nothing()
    {
        using var fx = new EngineFixture();

        var (result, runner) = await RunTests(fx, new Runner(TwoFailed));

        Assert.False(result.Success);
        Assert.Contains("No test project is known here", result.Error);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task A_target_that_is_not_a_test_project_is_refused_with_the_ones_that_are()
    {
        using var fx = new EngineFixture();
        fx.Write("Demo.Tests/Demo.Tests.csproj", TestProject);

        var (result, runner) = await RunTests(fx, new Runner(TwoFailed), """{"target":"Other.Tests"}""");

        Assert.False(result.Success);
        Assert.Contains("'Other.Tests' is not a test project here", result.Error);
        Assert.Contains("Demo.Tests", result.Error);
        Assert.Empty(runner.Commands);
    }

    // ── where it is offered ──────────────────────────────────────────────

    [Fact]
    public void A_worker_that_may_run_commands_is_offered_run_tests_and_told_when_to_use_it()
    {
        Assert.Contains(EngineFixture.ShippedTools(), t => t.Definition.Name == "run_tests");
        Assert.Contains("run_tests", WorkerTools.WithImplied(["run_command"]), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Run the workspace's tests with run_tests", DefaultWorkers.Augment("x", tools: ["run_command", "run_tests"]));
        Assert.DoesNotContain("run_tests", DefaultWorkers.Augment("x", tools: ["run_command"]));
    }

    /// <summary>The tests it runs are the workspace's own code, run on this machine: a policy that keeps the shells from a run keeps it too.</summary>
    [Fact]
    public void It_is_kept_from_a_run_with_the_shells()
    {
        Assert.True(ShellTools.IsShell("run_tests"));
        Assert.Contains("run_tests", AutonomyTiers.PolicyFor(2).AskBefore);
        Assert.Contains(Enactive.Core.Templates.BuiltinTemplates.All,
            t => t.Permissions?.Deny is { } deny && deny.Contains("run_command") && deny.Contains("run_tests"));
    }

    // ── a run through the shell ──────────────────────────────────────────

    /// <summary>run_command answering with the start and the end of a long test log, the whole of it kept in the scratch.</summary>
    private sealed class Shell(EngineFixture fx, string whole, bool keep) : ITool
    {
        public ToolDefinition Definition => new("run_command", "run", "{\"type\":\"object\"}", Kind: ToolKind.Command);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;

        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            var metadata = new Dictionary<string, object?> { ["exitCode"] = 0 };
            if (keep)
            {
                fx.Write(".enactive/scratch/output/log.txt", whole);
                metadata[CommandOutput.KeptKey] = ".enactive/scratch/output/log.txt";
            }
            var shown = keep ? whole[..120] + "\n… (cut) …\n" + whole[^60..] : whole;
            return Task.FromResult(new ToolResult(true, $"exit code 0\n{CommandOutput.Marker}{shown}", null, [], metadata));
        }
    }

    private static async Task<(string Reply, List<Enactive.Core.Events.WorkEvent> Events)> ThroughTheShell(string command, bool keep)
    {
        using var fx = new EngineFixture { EcosystemsOverride = [new DotnetEcosystem()] };
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command")
            .Append(new Shell(fx, TwoFailed, keep)).ToArray();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"tests"}"""),
            Turn.Calls1("run_command", JsonSerializer.Serialize(new { command })),
            Turn.Says("Done."));
        var events = await fx.RunAsync(fx.Build(provider), "Run the tests.");
        var reply = provider.Requests.SelectMany(r => r.Messages).Last(m => m.Role == ChatRole.Tool).Content;
        return (reply, events.ToList());
    }

    /// <summary>
    /// The model is answered with the failures; the result itself - what the run's checks and the reviewer read - keeps
    /// everything the command printed.
    /// </summary>
    [Fact]
    public async Task Tests_run_through_the_shell_and_cut_are_answered_with_what_they_came_to()
    {
        var (reply, events) = await ThroughTheShell("dotnet test Demo.Tests", keep: true);

        Assert.StartsWith("exit code 0\n3 tests: 1 passed, 2 failed.", reply.Replace("\r", ""));
        Assert.Contains("A_full_board_is_a_draw: System.InvalidOperationException : line one / line two (UnitTest1.cs:12)", reply);
        Assert.Contains("The whole output is kept in .enactive/scratch/output/log.txt.", reply);
        Assert.Contains(events, e => e.Kind == Enactive.Core.Events.EventKind.ToolResult && e.Summary.Contains("Determining projects", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("dotnet test Demo.Tests", false)]   // shown whole: all there is to read is already there
    [InlineData("dotnet build", true)]              // not a test run
    public async Task Any_other_command_is_answered_with_its_own_output(string command, bool keep)
        => Assert.Contains("Determining projects", (await ThroughTheShell(command, keep)).Reply);
}
