namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Permissions;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Xunit;

public sealed class PlanCheckReviewTests
{
    [Theory]
    [InlineData("python -m pytest --quiet", true)]
    [InlineData("npm test -- --runInBand", false)]
    [InlineData("bash verify.sh 'a b'", true)]
    [InlineData("custom-verifier --strict", false)]
    public async Task Production_planning_recovers_required_checks_and_drops_forbidden_suggestions_before_execution(string command, bool initiallyPresent)
    {
        using var fx = new EngineFixture { PlannerOverride = new Planner() };
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var request = "Run " + command + ".\nDo not run forbidden-check. Write result.txt.";
        var draft = JsonSerializer.Serialize(new { disposition = "quick_action", title = "work", checks = initiallyPresent
            ? new[] { new { name = "required", command }, new { name = "forbidden", command = "forbidden-check" } }
            : [] });
        var planner = new FakeChatProvider(Turn.Says(draft), Turn.Says(Contract(command, "Run " + command + ".", twoSources: true)));
        var worker = new FakeChatProvider(Turn.Calls1("write_file", """{"path":"result.txt","content":"done"}"""), Turn.Says("done"));
        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn()), request);
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(command, Assert.Single(commands.Seen)); // No baseline execution, no forbidden suggestion.
        Assert.Equal(2, planner.Requests.Count);
        Assert.Equal(2, worker.Requests.Count);
        Assert.Contains("Do not run forbidden-check", planner.Requests[1].Messages[1].Content!);
        Assert.Contains(events, e => e.Summary.Contains("Final check (Requested)"));
        Assert.DoesNotContain(events, e => e.Summary.Contains("already passes"));
    }

    [Theory]
    [InlineData("missing-source")]
    [InlineData("invented-quote")]
    [InlineData("truncated")]
    [InlineData("conflict")]
    public async Task Unresolved_contract_never_dispatches_worker_or_baseline(string defect)
    {
        using var fx = new EngineFixture { PlannerOverride = new Planner() };
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var answer = defect switch
        {
            "missing-source" => """{"sources":[],"checks":[],"action_policy":null,"forbidden_effects":[],"unresolved":null}""",
            "invented-quote" => Contract("invented", "Run invented."),
            "conflict" => """{"sources":[{"id":"O001","assessment":"conflicting constraints"}],"checks":[],"action_policy":null,"forbidden_effects":[],"unresolved":"Required action is prohibited"}""",
            _ => Contract("verify", "Run verify.")
        };
        var turn = Turn.Says(answer) with { FinishReason = defect == "truncated" ? "length" : "stop" };
        var planner = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"work","checks":[{"command":"baseline"}]}"""), turn, turn);
        var worker = new FakeChatProvider(Turn.Says("should never execute"));
        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn()), "Run verify.");
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Empty(commands.Seen);
        Assert.Empty(worker.Requests);
        Assert.Equal(defect == "conflict" ? 2 : 3, planner.Requests.Count);
    }

    [Fact]
    public async Task Correction_accounts_usage_and_does_not_treat_examples_as_required_commands()
    {
        var provider = new FakeChatProvider(Turn.Says("{}").Reporting(10, 5),
            Turn.Says("""{"sources":[{"id":"O001","assessment":"The command is only an example; explain it without running"}],"checks":[],"action_policy":null,"forbidden_effects":[],"unresolved":null}""").Reporting(20, 7));
        var result = await Review(provider, "Explain the example `verify --delete`; do not execute it.");
        Assert.Null(result.IncompleteReason);
        Assert.Empty(result.Checks);
        Assert.Equal(30, result.Usage.Prompt);
        Assert.Equal(12, result.Usage.Completion);
    }

    [Fact]
    public async Task Template_conflict_blocks_before_worker_or_any_command()
    {
        using var fx = new EngineFixture { PlannerOverride = new Planner() };
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var planner = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"work"}"""), Turn.Says("""
            {"sources":[{"id":"O001","assessment":"The configured upload violates no network"}],"checks":[],"action_policy":null,"forbidden_effects":[],"unresolved":"Template check needs network"}
            """));
        var worker = new FakeChatProvider(Turn.Says("must not run"));
        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn(), successCriteria: [new("upload", "remote-upload")]), "Work offline. Do not use network.");
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Empty(worker.Requests);
        Assert.Empty(commands.Seen);
        Assert.Contains("LOCKED", planner.Requests[1].Messages[0].Content!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Locked_contract_preserves_template_settings_or_rejects_changes(bool change)
    {
        var original = new SuccessCriterionDefinition("verify", "verify", 7, Required: false, AlreadyPassing: true);
        var answer = JsonSerializer.Serialize(new {
            sources = new[] { new { id = "O001", assessment = "Permitted final verification" } },
            checks = new[] { new { name = "verify", command = change ? "other" : "verify", origin = "declared",
                request_quote = (string?)null, expectedExitCode = 7, reason = "Allowed offline" } }, action_policy = (object?)null, forbidden_effects = Array.Empty<object>(), unresolved = (string?)null
        });
        var provider = new FakeChatProvider(Turn.Says(answer), Turn.Says(answer));
        var result = await PlanCheckReview.RunAsync(new(IntentDisposition.QuickAction, "work", null) { Checks = [original] },
            "Verify offline", new(null, "workspace", null, null, null, [], []), provider, "strong",
            new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default, preserveCriteria: true);
        Assert.Equal(change, result.IncompleteReason is not null);
        var actual = Assert.Single(result.Checks);
        Assert.Equal(original.Command, actual.Command);
        Assert.False(actual.Required);
        Assert.True(actual.AlreadyPassing);
        Assert.Equal(7, actual.ExpectedExitCode);
    }

    /// <summary>
    /// A locked criterion is kept exactly as supplied, so the origin label on the answer's copy of it
    /// decides nothing. Runs ee6cf56a and f8e875bf (2026-09-28) ended before any work because the
    /// planner, shown "Origin":0 for a template's check, called it "proposed" - twice. The four
    /// answers are in plan-check-corpus; this is the same thing, small.
    /// </summary>
    [Theory]
    [InlineData("proposed")]
    [InlineData("requested")]
    [InlineData("declared")]
    public async Task A_locked_criterion_is_kept_whatever_the_answer_calls_its_origin(string label)
    {
        var original = new SuccessCriterionDefinition("verify", "verify", 7, Required: false);
        var answer = JsonSerializer.Serialize(new {
            sources = new[] { new { id = "O001", assessment = "Permitted final verification" } },
            checks = new[] { new { name = "verify", command = "verify", origin = label,
                request_quote = label == "requested" ? "not in the request" : null, expectedExitCode = 7, reason = "Kept" } },
            action_policy = (object?)null, forbidden_effects = Array.Empty<object>(), unresolved = (string?)null
        });
        var result = await PlanCheckReview.RunAsync(new(IntentDisposition.QuickAction, "work", null) { Checks = [original] },
            "Verify offline", new(null, "workspace", null, null, null, [], []), new FakeChatProvider(Turn.Says(answer)), "strong",
            new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default, preserveCriteria: true);

        Assert.Null(result.IncompleteReason);
        var kept = Assert.Single(result.Checks);
        Assert.Equal(CriterionOrigin.Declared, kept.Origin);
        Assert.Null(kept.RequestQuote);
        Assert.Equal(7, kept.ExpectedExitCode);
    }

    /// <summary>The planner is shown each check's origin in the words its answer uses - not an enum's number.</summary>
    [Fact]
    public async Task The_planner_is_shown_a_checks_origin_by_name()
    {
        var provider = new FakeChatProvider(Turn.Says("{}"), Turn.Says("{}"));
        await PlanCheckReview.RunAsync(new(IntentDisposition.QuickAction, "work", null) {
                Checks = [new("Builds", "build"), new("verify", "verify", Origin: CriterionOrigin.Proposed)] },
            "Verify offline", new(null, "workspace", null, null, null, [], []), provider, "strong",
            new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default, preserveCriteria: true);

        var shown = provider.Requests[0].Messages[^1].Content!;
        Assert.Contains("\"origin\":\"declared\"", shown, StringComparison.Ordinal);
        Assert.Contains("\"origin\":\"proposed\"", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Origin\":", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Budget_stops_contract_clarification()
    {
        var provider = new FakeChatProvider(Turn.Says("{}").Reporting(10, 5));
        var result = await Review(provider, "Explain", new(new(MaxTokens: 15), DateTimeOffset.UtcNow));
        Assert.NotNull(result.IncompleteReason);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Already_requested_command_cannot_be_silently_removed()
    {
        var empty = """{"sources":[{"id":"O001","assessment":"No checks"}],"checks":[],"action_policy":null,"forbidden_effects":[],"unresolved":null}""";
        var provider = new FakeChatProvider(Turn.Says(empty), Turn.Says(empty));
        var result = await PlanCheckReview.RunAsync(new(IntentDisposition.QuickAction, "work", null) {
            Checks = [new("verify", "verify", Origin: CriterionOrigin.Requested) { RequestQuote = "Run verify." }]
        }, "Run verify.", new(null, "workspace", null, null, null, [], []), provider, "strong",
            new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default);
        Assert.Contains("omitted or changed", result.IncompleteReason!);
        Assert.Equal("verify", Assert.Single(result.Checks).Command);
    }

    [Fact]
    public async Task Too_many_checks_are_rejected_not_silently_truncated()
    {
        var answer = JsonSerializer.Serialize(new {
            sources = new[] { new { id = "O001", assessment = "Verification" } },
            checks = Enumerable.Range(0, PlanCheckReview.MaxFinalChecks + 1).Select(i => new {
                name = "check", command = "verify" + i, origin = "proposed", request_quote = (string?)null, expectedExitCode = 0, reason = "Check result"
            }), action_policy = (object?)null, forbidden_effects = Array.Empty<object>(), unresolved = (string?)null
        });
        var provider = new FakeChatProvider(Turn.Says(answer), Turn.Says(answer));
        var result = await Review(provider, "Verify result");
        Assert.Contains("Too many", result.IncompleteReason!);
    }

    private static Task<PlanResult> Review(FakeChatProvider provider, string request, RunBudget? budget = null) =>
        PlanCheckReview.RunAsync(new(IntentDisposition.QuickAction, "work", null), request,
            new WorkContext(null, "workspace", null, null, null, [], []), provider, "strong",
            budget ?? new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default);

    private static string Contract(string command, string quote, bool twoSources = false) => JsonSerializer.Serialize(new {
        sources = twoSources ? new[] { new { id = "O001", assessment = "Required final verification" }, new { id = "O002", assessment = "No forbidden-check; worker writes file" } }
            : new[] { new { id = "O001", assessment = "Required final verification" } },
        checks = new[] { new { name = "verify", command, origin = "requested", request_quote = quote, expectedExitCode = 0, reason = "Explicit final check allowed by request" } },
        action_policy = (object?)null, forbidden_effects = Array.Empty<object>(), unresolved = (string?)null
    });

    private sealed class Commands : ITool
    {
        public List<string> Seen { get; } = [];
        public ToolDefinition Definition => new("run_command", "verify", "{\"type\":\"object\"}", Kind: ToolKind.Command, RunsSuccessChecks: true);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            Seen.Add(doc.RootElement.GetProperty("command").GetString()!);
            return Task.FromResult(new ToolResult(true, "passed", null, [], new Dictionary<string, object?> { ["exitCode"] = 0 }));
        }
    }
}
