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
}
