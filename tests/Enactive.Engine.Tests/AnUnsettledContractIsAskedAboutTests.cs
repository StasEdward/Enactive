namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
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

    private static async Task<(List<WorkEvent> Events, Commands Commands, EngineFixture Fx)> Run(string answer,
        IReadOnlyList<SuccessCriterionDefinition>? template = null, string? because = null)
    {
        var fx = new EngineFixture { PlannerOverride = new Planner() };
        fx.Decisions.Answer = answer;
        fx.Decisions.Because = because;
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var planner = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"report","checks":[{"name":"uploaded","command":"check-upload"}]}"""),
            Turn.Says(Unsettled));
        var worker = new FakeChatProvider(Turn.Calls1("write_file", """{"path":"report.txt","content":"C: 120 GB free"}"""), Turn.Says("Written."));
        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn(), successCriteria: template), "Write a disk report.");
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

    /// <summary>
    /// "Without" is the contract from then on - a template's checks included. The card says none of the commands runs;
    /// a template's check was added back by the criteria the run is judged by, and ran (code review, 2026-09-29).
    /// </summary>
    [Fact]
    public async Task Without_final_checks_a_templates_check_does_not_run_either()
    {
        var (events, commands, fx) = await Run("without", [new("uploaded by the template", "template-upload-check")]);
        using var _ = fx;

        Assert.Contains("template-upload-check", Assert.Single(fx.Decisions.Requests).FullDetail, StringComparison.Ordinal);
        Assert.True(fx.Exists("report.txt"));
        Assert.DoesNotContain("template-upload-check", commands.Seen);
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
        // An answer nobody gave just now - a standing "--approve deny", an unattended run.
        var (events, commands, fx) = await Run("deny", because: "--approve deny");
        using var _ = fx;

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.False(fx.Exists("report.txt"));
        Assert.Empty(commands.Seen);
    }

    // ── a locked list whose review cannot be read back ─────────────────────

    /// <summary>A review of a template's locked list that keeps the list and adds a check of its own - refused, every time.</summary>
    private const string AddsOne = """
        {"sources":[{"id":"O001","assessment":"a disk report"}],
         "checks":[{"name":"Builds","command":"verify-build","origin":"declared","request_quote":null,"expectedExitCode":0,"reason":"kept"},
                   {"name":"extra","command":"extra-check","origin":"proposed","request_quote":null,"expectedExitCode":0,"reason":"added"}],
         "action_policy":null,"forbidden_effects":[],"unresolved":null}
        """;

    private static async Task<(List<WorkEvent> Events, Commands Commands, EngineFixture Fx, FakeChatProvider Planner)> Locked(
        string answer, string? because = null)
    {
        var fx = new EngineFixture { PlannerOverride = new Planner() };
        fx.Decisions.Answer = answer;
        fx.Decisions.Because = because;
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var planner = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"report"}"""), Turn.Says(AddsOne), Turn.Says(AddsOne));
        var worker = new FakeChatProvider(Turn.Calls1("write_file", """{"path":"report.txt","content":"C: 120 GB free"}"""), Turn.Says("Written."));
        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn(), successCriteria: [new("Builds", "verify-build")]), "Write a disk report.");
        return (events, commands, fx, planner);
    }

    /// <summary>
    /// A template's locked criteria whose review could not be read back even after the correction are a question for the
    /// person, not the end of the run: on 2026-10-08 a review that kept adding the request's test command to a template's
    /// locked list ended a run at "Locked review cannot add criteria" before its first step. Allowed, the work goes on
    /// with the template's criteria as supplied.
    /// </summary>
    [Fact]
    public async Task A_locked_list_the_review_cannot_settle_is_asked_about_and_kept()
    {
        var (events, commands, fx, planner) = await Locked("allow");
        using var _ = fx;

        var asked = Assert.Single(fx.Decisions.Requests);
        Assert.Contains("verify-build", asked.FullDetail, StringComparison.Ordinal);
        Assert.True(fx.Exists("report.txt"));
        Assert.Contains("verify-build", commands.Seen);
        Assert.DoesNotContain("extra-check", commands.Seen);
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());

        // The correction said the whole rule - keep these, add none - where each refusal said only the half it broke.
        var correction = planner.Requests[2].Messages[^1].Content ?? "";
        Assert.Contains("This list is fixed: return exactly these criteria - 'Builds' (verify-build, exit 0) - each unchanged, and add none.",
            correction, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the run already carries a restriction - a resumed run's ban on deleting files - an unreadable review of its
    /// locked list stays a stop: the person is not asked to wave through criteria nobody has checked against the ban.
    /// </summary>
    [Fact]
    public async Task A_locked_list_under_a_restriction_is_not_put_to_a_person()
    {
        var plan = new PlanResult(Enactive.Core.Tasks.IntentDisposition.QuickAction, "report", null)
        {
            Checks = [new("Builds", "verify-build") { Origin = CriterionOrigin.Declared }],
            Restrictions = [new(ForbiddenTaskEffect.FileDeletion, "do not delete any file")]
        };

        var result = await PlanCheckReview.RunAsync(plan, "Write a disk report and do not delete any file.",
            new Enactive.Core.Context.WorkContext(null, "workspace", null, null, null, [], []),
            new FakeChatProvider(Turn.Says(AddsOne), Turn.Says(AddsOne)), "plan-model",
            new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default, preserveCriteria: true, askWhenUnsettled: true);

        Assert.Null(result.Unsettled);
        Assert.Contains("could not be used", result.IncompleteReason, StringComparison.Ordinal);
    }

    /// <summary>With nobody to say yes, it is still a stop - as an unresolved contract is.</summary>
    [Fact]
    public async Task A_locked_list_the_review_cannot_settle_starts_no_work_with_no_one_to_ask()
    {
        var (events, commands, fx, _) = await Locked("deny", because: "--approve deny");
        using var _f = fx;

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.False(fx.Exists("report.txt"));
        Assert.Empty(commands.Seen);
    }

    /// <summary>
    /// Run 52bc38, 2026-10-03: the request had a typing mistake in it, the question came up, the person pressed
    /// Stop to type it again - and the run went into the history as "Incomplete: Unresolved verification
    /// contract", with an error beside it. Nothing had gone wrong and nothing was left unfinished: somebody
    /// stopped it. That is the outcome the engine has a name for.
    /// </summary>
    [Fact]
    public async Task A_person_who_answers_stop_has_cancelled_the_run_and_nothing_failed()
    {
        var (events, commands, fx) = await Run("deny");
        using var _ = fx;

        Assert.Equal(RunOutcomeKind.Cancelled, events.Last().Outcome());
        Assert.False(events.Has(EventKind.ErrorObserved), events.Text());
        Assert.False(fx.Exists("report.txt"));
        Assert.Empty(commands.Seen);
    }
}
