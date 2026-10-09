namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The review of the final checks could give a check one passing exit code only, while the engine judges by a list
/// (SuccessCriterionDefinition.ExpectedExitCodes). A request saying its test command's exit 1 is the finding, not a
/// failure, was "unresolved" every time - asked of a person on 2026-09-29 and again on 2026-10-08. A requested check
/// may now pass on the codes the request itself names, and only on those.
/// </summary>
public sealed class AFinalCheckPassesOnTheCodesTheRequestNamesTests
{
    private const string Request = "Add a test. Run the tests with dotnet test; pass \"expectedExitCodes\": [0, 1], since a failing test is the finding.";

    private static string Answer(string origin, object? quote, object codes, string command = "dotnet test") => JsonSerializer.Serialize(new
    {
        sources = new[] { new { id = "O001", assessment = "the test command and its exit codes" } },
        checks = new[] { new Dictionary<string, object?>
        {
            ["name"] = "tests ran", ["command"] = command, ["origin"] = origin, ["request_quote"] = quote,
            ["expectedExitCodes"] = codes, ["reason"] = "the request's own test command"
        } },
        forbidden_effects = Array.Empty<object>(), action_policy = (object?)null, unresolved = (string?)null
    });

    private static PlanContract Read(string answer, IReadOnlyList<SuccessCriterionDefinition>? locked = null)
        => PlanCheckContract.Validate(answer, complete: true,
            new PlanCheckInputs(Request, locked ?? [], [], null, null, locked is not null));

    /// <summary>The plan's own draft of the check: what the review is checked against when the list is not locked.</summary>
    private static PlanContract ReadAgainst(string answer, SuccessCriterionDefinition drafted)
        => PlanCheckContract.Validate(answer, complete: true, new PlanCheckInputs(Request, [drafted], [], null, null, false));

    private static SuccessCriterionDefinition Drafted(int exit)
        => new("tests ran", "dotnet test", exit) { Origin = CriterionOrigin.Requested, RequestQuote = "Run the tests with dotnet test" };

    [Fact]
    public void A_requested_check_passes_on_the_codes_the_request_names()
    {
        var check = Assert.Single(Read(Answer("requested", "Run the tests with dotnet test", new[] { 0, 1 })).Checks);

        Assert.Equal([0, 1], check.PassingExitCodes);
        Assert.True(check.PassesOn(1));
        Assert.False(check.PassesOn(2));
    }

    /// <summary>A code the request does not name is not the review's to allow: any command could otherwise pass on anything.</summary>
    [Fact]
    public void A_code_the_request_does_not_name_is_refused()
    {
        var refused = Assert.ThrowsAny<JsonException>(() => Read(Answer("requested", "Run the tests with dotnet test", new[] { 0, 2 })));

        Assert.Contains("Exit code 2 is not in the request", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_proposed_check_passes_on_0_alone()
        => Assert.ThrowsAny<JsonException>(() => Read(Answer("proposed", null, new[] { 0, 1 })));

    /// <summary>A locked list is matched on the whole set of codes, in any order - and a different set is a change.</summary>
    [Fact]
    public void A_locked_list_is_matched_on_its_codes()
    {
        SuccessCriterionDefinition[] locked = [new("tests ran", "dotnet test", 0) { ExpectedExitCodes = [0, 1], Origin = CriterionOrigin.Declared }];

        Assert.Single(Read(Answer("declared", null, new[] { 1, 0 }), locked).Checks);
        Assert.ThrowsAny<JsonException>(() => Read(Answer("declared", null, new[] { 0 }), locked));
    }

    /// <summary>
    /// The plan can give a check one code only, so the request's [0, 1] reaches the review as exit 0 - and the review's
    /// [0, 1] was refused as a change to a requested check (2026-10-08). Widening to codes the request names is kept.
    /// </summary>
    [Fact]
    public void The_review_widens_the_plans_code_to_the_codes_the_request_names()
    {
        var check = Assert.Single(ReadAgainst(Answer("requested", "Run the tests with dotnet test", new[] { 0, 1 }), Drafted(0)).Checks);

        Assert.Equal([0, 1], check.PassingExitCodes);
    }

    /// <summary>A code the plan passed on that stops passing is the requirement changed, not widened.</summary>
    [Fact]
    public void The_review_does_not_narrow_the_plans_codes()
    {
        var refused = Assert.ThrowsAny<JsonException>(() =>
            ReadAgainst(Answer("requested", "Run the tests with dotnet test", new[] { 0 }), Drafted(1)));

        Assert.Contains("every exit code it passes on", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>An answer that leaves "unresolved" out declined nothing - it was refused for that, and the review's first answer spent.</summary>
    [Fact]
    public void An_answer_without_unresolved_declined_nothing()
    {
        using var doc = JsonDocument.Parse(Answer("requested", "Run the tests with dotnet test", new[] { 0, 1 }));
        var withoutUnresolved = JsonSerializer.Serialize(doc.RootElement.EnumerateObject()
            .Where(p => p.Name != "unresolved").ToDictionary(p => p.Name, p => p.Value));

        var contract = Read(withoutUnresolved);

        Assert.Null(contract.Unresolved);
        Assert.Single(contract.Checks);
    }

    // ── a run ───────────────────────────────────────────────────────────────

    /// <summary>A test run with a failing test: exit 1, and the tests it ran printed.</summary>
    private sealed class FailingTests : ITool
    {
        public List<string> Seen { get; } = [];
        public ToolDefinition Definition => new("run_command", "run", "{\"type\":\"object\"}", Kind: ToolKind.Command, RunsSuccessChecks: true);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            Seen.Add(doc.RootElement.GetProperty("command").GetString()!);
            return Task.FromResult(new ToolResult(false, "Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3", "exit code 1",
                [], new Dictionary<string, object?> { ["exitCode"] = 1 }));
        }
    }

    /// <summary>
    /// Reviewed with the codes the request names, the contract is settled - nobody is asked - and the final check passes
    /// on exit 1, having run its tests.
    /// </summary>
    [Fact]
    public async Task A_request_that_names_its_codes_is_settled_and_its_check_passes_on_them()
    {
        using var fx = new EngineFixture { PlannerOverride = new Planner(), EcosystemsOverride = [new DotnetEcosystem()] };
        var shell = new FailingTests();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(shell).ToArray();
        var planner = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"test"}"""),
            Turn.Says(Answer("requested", "Run the tests with dotnet test", new[] { 0, 1 })));
        var worker = new FakeChatProvider(Turn.Says("Added."));

        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn()), Request);

        Assert.Empty(fx.Decisions.Requests);
        Assert.Contains("dotnet test", shell.Seen);
        Assert.Contains(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.StartsWith("PASS (exit 1) — tests ran", StringComparison.Ordinal));
    }

    /// <summary>
    /// The run of 2026-10-08 as it went: the plan drafts the request's test command on exit 0, the review gives it the
    /// request's [0, 1] and leaves "unresolved" out. It ended there, before its first step; now it runs, and the check
    /// passes on exit 1.
    /// </summary>
    [Fact]
    public async Task A_plan_drafting_exit_0_is_widened_by_the_review_and_the_run_goes_on()
    {
        using var fx = new EngineFixture { PlannerOverride = new Planner(), EcosystemsOverride = [new DotnetEcosystem()] };
        var shell = new FailingTests();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(shell).ToArray();
        using var doc = JsonDocument.Parse(Answer("requested", "Run the tests with dotnet test", new[] { 0, 1 }));
        var review = JsonSerializer.Serialize(doc.RootElement.EnumerateObject()
            .Where(p => p.Name != "unresolved").ToDictionary(p => p.Name, p => p.Value));
        var planner = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"test","checks":[{"name":"tests ran","command":"dotnet test","request_quote":"Run the tests with dotnet test"}]}"""),
            Turn.Says(review));
        var worker = new FakeChatProvider(Turn.Says("Added."));

        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn()), Request);

        Assert.DoesNotContain(events, e => e.Kind == EventKind.ErrorObserved && e.Summary.Contains("verification contract", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.StartsWith("PASS (exit 1) — tests ran", StringComparison.Ordinal));
    }
}
