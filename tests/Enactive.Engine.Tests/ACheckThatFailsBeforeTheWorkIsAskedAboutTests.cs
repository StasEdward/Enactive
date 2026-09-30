namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Benchmark scenario build-error, 2026-09-30: a request to add one method and leave the rest alone, in a project that did
/// not build before the work. The planner proposed "the build passes"; to satisfy it the worker fixed another file, the
/// review let it through, and the run was Completed against the request's own ban. A proposed check that already fails
/// before any work is now put to the planning model once: does the request ask for what would make it pass? Where it
/// does not, the check is dropped. Deliberately not code: a report, and a check that its upload is live.
/// </summary>
public sealed class ACheckThatFailsBeforeTheWorkIsAskedAboutTests
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
            return Task.FromResult(ToolResults.Fail("exit 1: the report site answered 404 - its upload has been broken since yesterday",
                metadata: new Dictionary<string, object?> { ["exitCode"] = 1 }));
        }
    }

    private const string Contract = """
        {"sources":[{"id":"O001","assessment":"write the report; the site is someone else's"}],
         "checks":[{"name":"site shows the report","command":"check-site","origin":"proposed","request_quote":null,"expectedExitCode":0,"reason":"the report is published"}],
         "action_policy":null,"forbidden_effects":[],"unresolved":null}
        """;

    private static async Task<(List<WorkEvent> Events, Commands Commands, EngineFixture Fx, FakeChatProvider Planner)> Run(params Turn[] decision)
    {
        var fx = new EngineFixture { PlannerOverride = new Planner() };
        var commands = new Commands();
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(commands).ToArray();
        var planner = new FakeChatProvider(
            [Turn.Says("""{"disposition":"quick_action","title":"report","checks":[{"name":"site shows the report","command":"check-site"}]}"""),
             Turn.Says(Contract), .. decision]);
        var worker = new FakeChatProvider(Turn.Calls1("write_file", """{"path":"report.txt","content":"C: 120 GB free"}"""), Turn.Says("Written."));
        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn()), "Write a disk report into report.txt. Do not touch the report site.");
        return (events, commands, fx, planner);
    }

    /// <summary>THE ONE THAT MATTERS: the request does not ask for what would make the check pass - it is dropped, and not run again.</summary>
    [Fact]
    public async Task A_check_the_request_does_not_ask_to_make_pass_is_dropped()
    {
        var (events, commands, fx, planner) = await Run(Turn.Says(
            """{"checks":[{"name":"site shows the report","keep":false,"reason":"the site's upload is broken and the request says not to touch it"}]}"""));
        using var _ = fx;

        Assert.Equal(["check-site"], commands.Seen);                                    // tried before the work, never after
        Assert.Contains(events, e => e.Summary.Contains("already fails before any work, and the request does not ask for what would make it pass - dropped",
            StringComparison.Ordinal));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        var asked = string.Join("\n", planner.Requests[2].Messages.Select(m => m.Content));
        Assert.Contains("answered 404", asked, StringComparison.Ordinal);                 // the decision is shown the check's own output
        Assert.Contains("Do not touch the report site", asked, StringComparison.Ordinal);
    }

    /// <summary>The request asks for it: the check stays, runs after the work, and still holds the run to it.</summary>
    [Fact]
    public async Task A_check_the_request_asks_to_make_pass_stays()
    {
        var (events, commands, fx, _) = await Run(Turn.Says(
            """{"checks":[{"name":"site shows the report","keep":true,"reason":"publishing is what was asked"}]}"""));
        using var _ = fx;

        Assert.Equal(2, commands.Seen.Count(c => c == "check-site"));
        Assert.False(events.Has(EventKind.TaskCompleted));
    }

    /// <summary>A long failing output is shown by its end, where a runner says what failed, and the cut says so.</summary>
    [Fact]
    public async Task A_long_output_is_shown_by_its_end_and_says_it_was_cut()
    {
        var provider = new FakeChatProvider(Turn.Says("""{"checks":[]}"""));
        var check = new Enactive.Core.Templates.SuccessCriterionDefinition("site", "check-site");
        var output = new string('.', FailingCheckReview.OutputTailChars * 2) + "FINAL: the upload is broken";
        var before = new Enactive.Core.Templates.CriterionResult("site", "check-site", true, Enactive.Core.Templates.CriterionOutcome.Failed, 1, null)
            { Output = output };

        await FailingCheckReview.RunAsync("write a report", [(check, before)], provider, "strong",
            new Enactive.Core.Execution.RunBudget(null, DateTimeOffset.UtcNow), 1000, CancellationToken.None);

        var asked = provider.Requests.Single().Messages.Last().Content!;
        Assert.Contains($"({output.Length - FailingCheckReview.OutputTailChars} characters of the start not shown; the end follows)", asked, StringComparison.Ordinal);
        Assert.Contains("FINAL: the upload is broken", asked, StringComparison.Ordinal);
    }

    /// <summary>No usable answer: the check stays as it was.</summary>
    [Fact]
    public async Task Without_a_usable_answer_the_check_stays()
    {
        var (events, commands, fx, _) = await Run(Turn.Says("I think it is fine."));
        using var _ = fx;

        Assert.Equal(2, commands.Seen.Count(c => c == "check-site"));
        Assert.Contains(events, e => e.Summary.Contains("Checks that already fail before the work were kept as planned", StringComparison.Ordinal));
    }
}
