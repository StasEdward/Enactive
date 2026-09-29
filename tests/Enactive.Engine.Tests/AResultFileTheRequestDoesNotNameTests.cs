namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Run 68f92f, 2026-09-29: the request named no file for the report; the plan invented "CoverageReport.md", the
/// work wrote "coverage-report.md", and the run failed on the invented name. The check diagnosis then proposed a
/// directory listing in its place - a weaker check - and the engine dropped the correction without a word.
/// Two things follow, and neither weakens the bar: a correction that cannot apply to a criterion the engine decides
/// is said, with the original kept; and a result the request names no file for is checked on the file the step
/// HANDED ON (path_from), with the reviewer shown that file. Deliberately not code: a summary of invoices.
/// </summary>
public sealed class AResultFileTheRequestDoesNotNameTests
{
    // ── part 2: a correction that cannot apply is said ──────────────────────────────────

    private const string QuickPlan = """
        {"disposition":"quick_action","title":"summary","criteria":[{"kind":"file_exists","path":"reports/Summary.md"}]}
        """;

    private const string Diagnosis = """
        {"decisions":[{"index":0,"kind":"check","reason":"The summary exists under another name.","command":"dir /s /b *.md"}]}
        """;

    private sealed class Commands : ITool
    {
        public List<string> Seen { get; } = [];
        public ToolDefinition Definition => new("run_command", "verify", "{\"type\":\"object\"}", Kind: ToolKind.Command, RunsSuccessChecks: true);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            Seen.Add(doc.RootElement.GetProperty("command").GetString()!);
            return Task.FromResult(new ToolResult(true, "exit 0", null, [], new Dictionary<string, object?> { ["exitCode"] = 0 }));
        }
    }

    [Fact]
    public async Task A_command_proposed_in_place_of_a_file_criterion_is_not_applied_and_that_is_said()
    {
        using var fx = new EngineFixture { TypedCriteria = true };
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var planner = new FakeChatProvider(Turn.Says(QuickPlan), Turn.Says(Diagnosis));
        var worker = new FakeChatProvider(Turn.Calls1("write_file", """{"path":"reports/overview.md","content":"Total: 10"}"""), Turn.Says("done"));

        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn(), successRetries: 1), "Add up the invoices and write a summary.");

        Assert.False(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.DoesNotContain("dir /s /b *.md", commands.Seen);
        var said = Assert.Single(events, e => e.Kind == EventKind.ContractRevised);
        Assert.StartsWith("Correction not applied: 'reports/Summary.md exists' is decided by the engine", said.Summary, StringComparison.Ordinal);
        Assert.Contains("The original criterion stands", said.Summary, StringComparison.Ordinal);
        Assert.Contains("The summary exists under another name.", said.Summary, StringComparison.Ordinal);
        Assert.Contains("\"applied\":false", said.PayloadJson!, StringComparison.Ordinal);
    }

    // ── part 3: the file the step hands on ──────────────────────────────────────────────

    private static Plan TwoSteps(StepOutputFieldType type = StepOutputFieldType.Path)
    {
        var add = new PlanStep(Guid.NewGuid(), "add up", StepStatus.Pending, []);
        var write = new PlanStep(Guid.NewGuid(), "write summary", StepStatus.Pending, [add.Id])
            { Output = new StepOutputSchema("s2", 1, [new StepOutputField("summary", type, "the summary file")]) };
        return new Plan(Guid.NewGuid(), [add, write]);
    }

    private static (IReadOnlyList<SuccessCriterionDefinition> Accepted, IReadOnlyList<string> Dropped) Validate(string criteria, Plan plan)
    {
        using var doc = JsonDocument.Parse("{\"criteria\":[" + criteria + "]}");
        return TypedCriteria.Validate(TypedCriteria.Read(doc.RootElement), Path.GetTempPath(), [], plan);
    }

    [Fact]
    public void A_criterion_on_a_handed_path_is_accepted_and_decided_by_the_run()
    {
        var (accepted, dropped) = Validate("""{"kind":"file_exists","path_from":{"step":1,"field":"summary"}}""", TwoSteps());
        Assert.Empty(dropped);
        var c = Assert.Single(accepted);
        Assert.True(c.Typed!.FromRun);
        Assert.Equal("the file step 2 hands on as 'summary' exists", c.Name);
    }

    [Theory]
    [InlineData("""{"kind":"file_exists","path_from":{"step":0,"field":"summary"}}""", "step 0 declares no output field 'summary'")]
    [InlineData("""{"kind":"file_exists","path_from":{"step":5,"field":"summary"}}""", "step 5, which is not in the plan")]
    [InlineData("""{"kind":"file_contains","path_from":{"step":1,"field":"summary"}}""", "it names no text to look for")]
    public void A_handed_path_the_plan_cannot_hand_on_is_dropped_with_the_reason(string criterion, string reason)
    {
        var (accepted, dropped) = Validate(criterion, TwoSteps());
        Assert.Empty(accepted);
        Assert.Contains(reason, Assert.Single(dropped), StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_that_is_not_a_path_cannot_name_the_file()
    {
        var (accepted, dropped) = Validate("""{"kind":"file_exists","path_from":{"step":1,"field":"summary"}}""", TwoSteps(StepOutputFieldType.Text));
        Assert.Empty(accepted);
        Assert.Contains("is not a path", Assert.Single(dropped), StringComparison.Ordinal);
    }

    private static StepOutput Handed(int stepNo, string json)
        => new(stepNo, "write summary", Guid.NewGuid(), "s2", 1, DateTimeOffset.UtcNow, json, [], 1, []);

    [Fact]
    public void The_criterion_follows_the_name_the_step_handed_on_and_fails_when_nothing_was_handed()
    {
        var root = Directory.CreateTempSubdirectory("handed").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "reports"));
            File.WriteAllText(Path.Combine(root, "reports", "invoice-summary.md"), "Total: 10");
            var (accepted, _) = Validate("""{"kind":"file_contains","text":"Total","path_from":{"step":1,"field":"summary"}}""", TwoSteps());
            var c = Assert.Single(accepted);

            var passed = TypedCriteria.EvaluateHanded(c, [Handed(2, """{"summary":"reports/invoice-summary.md"}""")], root);
            Assert.Equal(CriterionOutcome.Passed, passed.Outcome);
            Assert.StartsWith("'reports/invoice-summary.md', handed on by step 2 as 'summary'", passed.Detail, StringComparison.Ordinal);

            Assert.Equal(CriterionOutcome.Failed, TypedCriteria.EvaluateHanded(c, [Handed(2, """{"summary":"reports/missing.md"}""")], root).Outcome);
            var none = TypedCriteria.EvaluateHanded(c, [], root);
            Assert.Equal(CriterionOutcome.Failed, none.Outcome);
            Assert.Contains("step 2 handed on no result", none.Detail, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_long_handed_file_says_how_much_was_not_shown()
    {
        var root = Directory.CreateTempSubdirectory("handed").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "summary.md"), new string('x', TypedCriteria.MaxHandedChars + 500));
            var shown = TypedCriteria.ShowHanded("summary.md", "summary", root);
            Assert.EndsWith("[... 500 more characters not shown]", shown, StringComparison.Ordinal);
            Assert.EndsWith("(no such file)", TypedCriteria.ShowHanded("other.md", "summary", root), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private const string StepsPlan = """
        {"disposition":"task","title":"invoices",
         "steps":[{"title":"Add up the invoices","dependsOn":[]},
                  {"title":"Write the summary","dependsOn":[0],"output":{"summary":{"type":"path","description":"the summary file"}}}],
         "criteria":[{"kind":"file_contains","text":"Total","path_from":{"step":1,"field":"summary"}}]}
        """;

    /// <summary>
    /// THE ONE THAT MATTERS: the step hands on a file an earlier run wrote. The engine checks the name handed on,
    /// not one the plan made up, and the reviewer is shown that file whole - it decides whether it is what was asked.
    /// </summary>
    [Fact]
    public async Task The_handed_file_passes_the_check_and_the_reviewer_is_shown_it()
    {
        using var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true };
        fx.Write("reports/invoice-summary.md", "Total: 10 (from an earlier run)");
        var worker = new FakeChatProvider(
            Turn.Says(StepsPlan),
            Turn.Says("The invoices add up to 10."),
            Turn.Calls1(StepOutputContract.ToolName, """{"summary":"reports/invoice-summary.md"}""", "s1"),
            Turn.Says("The summary is already there."));
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "add up the invoices and write a summary");

        Assert.Contains(events, e => e.IsCheck() && e.Summary.StartsWith("PASS", StringComparison.Ordinal)
                                     && e.Summary.Contains("the file step 2 hands on as 'summary'", StringComparison.Ordinal));
        var shown = string.Join("\n", reviewer.Requests[^1].Messages.Select(m => m.Content));
        Assert.Contains("HANDED ON by this step as its 'summary' - 'reports/invoice-summary.md', how it is NOW:", shown, StringComparison.Ordinal);
        Assert.Contains("Total: 10 (from an earlier run)", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("HANDED ON", string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content)), StringComparison.Ordinal);
    }
}
