namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Phase 4: after a check fails, the definition of done may tighten on its own and may not loosen
/// without somebody saying so.
///
/// <para>Before: a failing proposed check could be replaced by the planner with any other command -
/// reviewed only against the task's restrictions - and run in its place. A failed check could become
/// one that passes, and the run be called finished, with nobody having agreed to the lower bar.</para>
/// </summary>
public sealed class TheBarDoesNotQuietlyDropTests
{
    // ── the order (4.2) ──────────────────────────────────────────────────────────────────

    private static SuccessCriterionDefinition Command(string command, bool required = true)
        => new("check", command, 0, Required: required, Origin: CriterionOrigin.Proposed);

    private static SuccessCriterionDefinition File(TypedCriterion typed)
        => new("file", "file", Origin: CriterionOrigin.Proposed) { Typed = typed };

    [Theory]
    [InlineData("dotnet test", "dotnet test", CriterionStrength.Same)]
    [InlineData("dotnet test", "dotnet test --filter OneTest", CriterionStrength.Incomparable)]
    [InlineData("dotnet test", "echo ok", CriterionStrength.Incomparable)]
    public void Two_commands_are_the_same_or_they_cannot_be_compared(string was, string now, CriterionStrength expected)
        => Assert.Equal(expected, ContractMonotonicity.Compare(Command(was), Command(now)).Strength);

    [Fact]
    public void No_longer_required_is_weaker_and_newly_required_is_stronger()
    {
        Assert.Equal(CriterionStrength.Weaker, ContractMonotonicity.Compare(Command("x"), Command("x", required: false)).Strength);
        Assert.Equal(CriterionStrength.Stronger, ContractMonotonicity.Compare(Command("x", required: false), Command("x")).Strength);
    }

    [Fact]
    public void A_file_that_may_now_be_empty_is_weaker_and_one_that_may_not_is_stronger()
    {
        var nonEmpty = File(new TypedCriterion(TypedCriterionKind.FileExists, Path: "report.md"));
        var mayBeEmpty = File(new TypedCriterion(TypedCriterionKind.FileExists, Path: "report.md", NonEmpty: false));

        Assert.Equal(CriterionStrength.Weaker, ContractMonotonicity.Compare(nonEmpty, mayBeEmpty).Strength);
        Assert.Equal(CriterionStrength.Stronger, ContractMonotonicity.Compare(mayBeEmpty, nonEmpty).Strength);
        Assert.Equal(CriterionStrength.Incomparable, ContractMonotonicity.Compare(nonEmpty,
            File(new TypedCriterion(TypedCriterionKind.FileExists, Path: "other.md"))).Strength);
    }

    [Fact]
    public void Looking_for_more_of_the_same_text_is_stronger_and_other_text_cannot_be_compared()
    {
        var total = File(new TypedCriterion(TypedCriterionKind.FileContains, Path: "r.md", Text: "Total"));
        Assert.Equal(CriterionStrength.Stronger, ContractMonotonicity.Compare(total,
            File(new TypedCriterion(TypedCriterionKind.FileContains, Path: "r.md", Text: "Total: 3"))).Strength);
        Assert.Equal(CriterionStrength.Incomparable, ContractMonotonicity.Compare(total,
            File(new TypedCriterion(TypedCriterionKind.FileContains, Path: "r.md", Text: "Sum"))).Strength);
    }

    [Fact]
    public void Weaker_in_one_way_and_stronger_in_another_is_not_stronger()
    {
        var was = File(new TypedCriterion(TypedCriterionKind.FileExists, Path: "r.md", NonEmpty: false)) with { Required = true };
        var now = File(new TypedCriterion(TypedCriterionKind.FileExists, Path: "r.md", NonEmpty: true)) with { Required = false };
        Assert.Equal(CriterionStrength.Incomparable, ContractMonotonicity.Compare(was, now).Strength);
    }

    // ── through a run (4.3) ──────────────────────────────────────────────────────────────

    /// <summary>A proposed check "wrong-check" that fails, and a verifier that passes whatever else it is asked.</summary>
    private sealed class Commands : ITool
    {
        public List<string> Seen { get; } = [];
        public ToolDefinition Definition => new("run_command", "verify", "{\"type\":\"object\"}", Kind: ToolKind.Command, RunsSuccessChecks: true);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var command = doc.RootElement.GetProperty("command").GetString()!;
            Seen.Add(command);
            var exit = command == "wrong-check" ? 1 : 0;
            return Task.FromResult(new ToolResult(exit == 0, "exit " + exit, null, [], new Dictionary<string, object?> { ["exitCode"] = exit }));
        }
    }

    private const string Plan = """
        {"disposition":"quick_action","title":"work","checks":[{"name":"proposal","command":"wrong-check"}]}
        """;

    private const string Diagnosis = """
        {"decisions":[{"index":0,"kind":"check","reason":"The check is too strict.","command":"echo ok"}]}
        """;

    /// <summary>
    /// THE ONE THAT MATTERS: the planner replaces a failed check with one that passes; nobody accepts
    /// it; the original stands, and so does its failure. The replacement is never run.
    /// </summary>
    [Fact]
    public async Task A_failed_check_is_not_replaced_by_one_that_passes_without_somebody_accepting_it()
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "deny";
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var planner = new FakeChatProvider(Turn.Says(Plan), Turn.Says(Diagnosis));
        var worker = new FakeChatProvider(Turn.Calls1("write_file", """{"path":"report.txt","content":"done"}"""), Turn.Says("done"));

        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn(), successRetries: 1), "Produce report.txt.");

        Assert.False(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.DoesNotContain("echo ok", commands.Seen);
        var refused = Assert.Single(events, e => e.Kind == EventKind.ContractRevised);
        Assert.Contains("the original stands", refused.Summary, StringComparison.Ordinal);
        Assert.Contains("\"applied\":false", refused.PayloadJson!, StringComparison.Ordinal);
        var question = Assert.Single(fx.Decisions.Requests);
        Assert.Equal("deny", question.RecommendedOptionId);                    // the safe answer is the recommended one
        Assert.Contains("wrong-check", question.FullText, StringComparison.Ordinal);
        Assert.Contains("echo ok", question.FullText, StringComparison.Ordinal);
    }

    /// <summary>Unattended, the answer is no - and a run with nobody to ask never lowers its own bar.</summary>
    [Fact]
    public async Task Unattended_the_bar_stays_where_it_was()
    {
        using var fx = new EngineFixture();
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var planner = new FakeChatProvider(Turn.Says(Plan), Turn.Says(Diagnosis));
        var worker = new FakeChatProvider(Turn.Calls1("write_file", """{"path":"report.txt","content":"done"}"""), Turn.Says("done"));

        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn(), successRetries: 1, decisions: new UnattendedDecisionHandler()), "Produce report.txt.");

        Assert.False(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.DoesNotContain("echo ok", commands.Seen);
    }
}
