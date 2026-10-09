namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Builds;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// "The tests pass" is about the test projects there are when the final checks run, not when the plan is read. Run
/// 89aa8d1b, 2026-10-09: "write tests for the project" - the plan's step 2 made the test project, and the planner's
/// tests_pass was dropped before step 1 began, with a warning, because there were no tests yet. The run was checked only
/// because the contract review happened to add a test command of its own.
/// </summary>
public sealed class TheTestsAreFoundWhenTheyAreCheckedTests
{
    /// <summary>An ecosystem whose test projects are the *.tests files at the root; it runs one with "run &lt;file&gt;".</summary>
    private sealed class Probe : IEcosystem
    {
        public string Name => "probe";
        public EcosystemTargets? Detect(string root)
        {
            var tests = Directory.GetFiles(root, "*.tests").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
            return tests.Length == 0 ? null : new(Name, [], tests);
        }
        public bool Owns(string relativePath) => false;
        public string BuildCommand(string target) => "build";
        public string TestCommand(string target) => $"run {target}";
        public bool RunsTests(string command) => command.StartsWith("run ", StringComparison.Ordinal);
        public TestRunReport? ParseTests(string output)
        {
            var cases = output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("PASS ", StringComparison.Ordinal) || l.StartsWith("FAIL ", StringComparison.Ordinal))
                .Select(l => new TestCaseResult(l[5..], l.StartsWith("PASS", StringComparison.Ordinal) ? TestVerdict.Passed : TestVerdict.Failed)).ToArray();
            return cases.Length == 0 ? null : new TestRunReport(cases, null);
        }
        public IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string output, string workspaceRoot) => [];
    }

    /// <summary>The check runner: a test project's file holds what running it prints; it exits 1 where that says FAIL.</summary>
    private sealed class Shell(EngineFixture fx) : ITool
    {
        public List<string> Ran { get; } = [];
        public ToolDefinition Definition => new("run_command", "run", "{\"type\":\"object\"}", Kind: ToolKind.Command, RunsSuccessChecks: true);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var command = doc.RootElement.GetProperty("command").GetString()!;
            Ran.Add(command);
            var file = Path.Combine(fx.Root, command["run ".Length..]);
            var output = command.StartsWith("run ", StringComparison.Ordinal) && File.Exists(file) ? File.ReadAllText(file) : "";
            var exit = output.Contains("FAIL ", StringComparison.Ordinal) ? 1 : 0;
            return Task.FromResult(new ToolResult(exit == 0, output, exit == 0 ? null : $"exited with code {exit}", [],
                new Dictionary<string, object?> { ["exitCode"] = exit }));
        }
    }

    private static string Plan(string criterion = """{"kind":"tests_pass"}""")
        => $$"""{"disposition":"quick_action","title":"tests","criteria":[{{criterion}}]}""";

    private static async Task<(WorkEvent[] Events, Shell Shell)> Run(EngineFixture fx, string plan, params Turn[] work)
    {
        var shell = new Shell(fx);
        fx.EcosystemsOverride = [new Probe()];
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(shell).ToArray();
        var events = await fx.RunAsync(fx.Build(new FakeChatProvider([Turn.Says(plan), .. work, Turn.Says("Done.")]),
            EngineFixture.Role("developer")), "write tests for the project");
        return (events.ToArray(), shell);
    }

    private static WorkEvent TestsCheck(IEnumerable<WorkEvent> events)
        => Assert.Single(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains("Tests pass", StringComparison.Ordinal));

    private static Turn Writes(string path, string content, string id = "w1")
        => Turn.Calls1("write_file", JsonSerializer.Serialize(new { path, content }), id);

    [Fact]
    public async Task A_plan_whose_work_makes_the_tests_is_checked_against_them()
    {
        using var fx = new EngineFixture { TypedCriteria = true };

        var (events, shell) = await Run(fx, Plan(), Writes("game.tests", "PASS wins\nPASS draws\n"));

        Assert.DoesNotContain(events, e => e.Summary.Contains("Planner criterion dropped", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == EventKind.ContextAssembled
            && e.Summary == "Tests pass: no test project yet - the test projects are found when the final checks run.");
        Assert.StartsWith("PASS", TestsCheck(events).Summary, StringComparison.Ordinal);
        Assert.Contains("run game.tests", shell.Ran);
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    [Fact]
    public async Task A_test_project_the_run_adds_beside_one_already_there_is_run_too()
    {
        using var fx = new EngineFixture { TypedCriteria = true };
        fx.Write("old.tests", "PASS old\n");

        var (events, shell) = await Run(fx, Plan(), Writes("new.tests", "FAIL broken\n"));

        Assert.Contains("run new.tests", shell.Ran);
        var check = TestsCheck(events);
        Assert.StartsWith("FAIL", check.Summary, StringComparison.Ordinal);
        Assert.Contains("old.tests (run old.tests): passed", check.Summary, StringComparison.Ordinal);
        Assert.Contains("new.tests (run new.tests): FAILED", check.Summary, StringComparison.Ordinal);
        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
    }

    /// <summary>No test project at all when the checks run: nothing was checked - said so, not passed and not failed.</summary>
    [Fact]
    public async Task With_no_test_project_at_the_end_the_check_is_not_checked()
    {
        using var fx = new EngineFixture { TypedCriteria = true };

        var (events, _) = await Run(fx, Plan(), Writes("notes.md", "nothing"));

        var check = TestsCheck(events);
        Assert.StartsWith("NOT CHECKED", check.Summary, StringComparison.Ordinal);
        Assert.Contains("no test project is in the workspace, so there were no tests to run", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_named_test_project_that_is_not_there_fails_and_says_which_are()
    {
        using var fx = new EngineFixture { TypedCriteria = true };

        var (events, _) = await Run(fx, Plan("""{"kind":"tests_pass","target":"other.tests"}"""), Writes("game.tests", "PASS wins\n"));

        var check = TestsCheck(events);
        Assert.StartsWith("FAIL", check.Summary, StringComparison.Ordinal);
        Assert.Contains("'other.tests' is not a test project here; the test projects are: game.tests.", check.Summary, StringComparison.Ordinal);
    }

    /// <summary>The contract review sees that the plan checks the tests, and what that runs - so it adds no second test run.</summary>
    [Fact]
    public void The_contract_review_is_shown_what_the_tests_check_runs()
    {
        var (accepted, dropped) = TypedCriteria.Validate(
            TypedCriteria.Read(JsonDocument.Parse("""{"criteria":[{"kind":"tests_pass"}]}""").RootElement), Path.GetTempPath(), []);

        Assert.Empty(dropped);
        var shown = JsonSerializer.Serialize(EngineCriteriaReview.Show(accepted, "write tests", null));
        Assert.Contains("every test project found when the final checks run", shown, StringComparison.Ordinal);
        Assert.Contains("propose no other check that only runs the same tests", EngineCriteriaReview.Prompt, StringComparison.Ordinal);
    }
}
