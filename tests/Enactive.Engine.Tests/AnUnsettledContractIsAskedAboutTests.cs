namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// 2026-09-29: twice a run was stopped before its first step by the review of its final checks - "sending an email
/// cannot be verified by a command", and a test command's two allowed exit codes did not fit one field. Where nothing it
/// says is about a restriction of the request, an unsettled contract is now a question for the person (Phase 1.8): go on
/// with the checks as planned, go on without them, or stop. With nobody to ask, it is still a stop.
/// Deliberately not code: a disk report and a check that would upload it.
/// </summary>
public sealed class AnUnsettledContractIsAskedAboutTests
{
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

    private const string Unsettled = """
        {"sources":[{"id":"O001","assessment":"the report and its upload"}],"checks":[],"action_policy":null,"forbidden_effects":[],
         "unresolved":"Uploading the report cannot be verified by a command"}
        """;

    private static async Task<(List<WorkEvent> Events, Commands Commands, EngineFixture Fx)> Run(string answer)
    {
        var fx = new EngineFixture { PlannerOverride = new Planner() };
        fx.Decisions.Answer = answer;
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var planner = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"report","checks":[{"name":"uploaded","command":"check-upload"}]}"""),
            Turn.Says(Unsettled));
        var worker = new FakeChatProvider(Turn.Calls1("write_file", """{"path":"report.txt","content":"C: 120 GB free"}"""), Turn.Says("Written."));
        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn()), "Write a disk report.");
        return (events, commands, fx);
    }

    [Fact]
    public async Task Asked_the_person_may_let_the_work_go_on_without_the_checks()
    {
        var (events, commands, fx) = await Run("without");
        using var _ = fx;

        var asked = Assert.Single(fx.Decisions.Requests);
        Assert.Equal("without", asked.RecommendedOptionId);
        Assert.Contains("Uploading the report cannot be verified by a command", asked.Detail, StringComparison.Ordinal);
        Assert.True(fx.Exists("report.txt"));
        Assert.DoesNotContain("check-upload", commands.Seen);
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    [Fact]
    public async Task Or_with_the_checks_as_planned()
    {
        var (events, commands, fx) = await Run("allow");
        using var _ = fx;

        Assert.True(fx.Exists("report.txt"));
        Assert.Contains("check-upload", commands.Seen);
    }

    [Fact]
    public async Task With_no_one_to_say_yes_no_work_starts()
    {
        var (events, commands, fx) = await Run("deny");
        using var _ = fx;

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.False(fx.Exists("report.txt"));
        Assert.Empty(commands.Seen);
    }
}
