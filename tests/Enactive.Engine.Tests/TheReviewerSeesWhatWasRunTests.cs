namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Execution;
using Enactive.Core.Intents;
using Enactive.Core.Context;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Run 148e77, 2026-09-29: a step read ten files, ran the test suite (129 of 129 passed), then searched sixteen
/// times. The evidence budget dropped "the 19 oldest calls" - the test run among them - and the reviewer failed the
/// step twice: once for a test run "not among the displayed calls", once for "129 tests" against a count of 103 test
/// methods, when the engine's own run before the work had said 129 passed. And the contract review, shown
/// path_from {step:3} beside steps without numbers, "corrected" it to a step that does not exist.
/// Deliberately not code: a wiki whose link checks are its tests.
/// </summary>
public sealed class TheReviewerSeesWhatWasRunTests
{
    // ── A: a command that ran is kept longest ───────────────────────────────────────────

    private static ExecutionJournal StepWithAnOldCommand()
    {
        var journal = new ExecutionJournal();
        for (var i = 0; i < 10; i++)
            journal.Record(1, "read_file", $$"""{"path":"pages/p{{i}}.page"}""", ActionOutcome.Succeeded, new string('r', 400), WorkspaceEffect.None);
        journal.Record(1, "run_command", """{"command":"check-links"}""", ActionOutcome.Succeeded, "PASS a\nPASS b\n129 passed", WorkspaceEffect.None,
            exitCode: 0);
        for (var i = 0; i < 10; i++)
            journal.Record(1, "search_files", $$"""{"pattern":"link{{i}}"}""", ActionOutcome.Succeeded, new string('s', 400), WorkspaceEffect.None);
        return journal;
    }

    [Fact]
    public void An_old_command_outlives_newer_reads_and_searches_when_the_budget_is_short()
    {
        var view = StepWithAnOldCommand().Describe(maxChars: 2500);

        Assert.True(view.ActionsOmitted);
        Assert.Contains(11, view.VisibleActionIds);                                     // the command, call [11]
        Assert.Contains("[11] [process exit=0] -> run_command", view.Text, StringComparison.Ordinal);
        Assert.Contains("129 passed", view.Text, StringComparison.Ordinal);
        Assert.Matches(@"… \d+ call\(s\) of this step are not shown here \(\[1\]-\[\d+\]", view.Text);
        Assert.Contains("a command that ran, and what the engine observed itself, are kept longest", view.Text, StringComparison.Ordinal);
        Assert.Contains("\"omittedCommands\":0", view.CommandHistory(), StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_command_among_what_is_dropped_the_note_is_as_it_was()
    {
        var journal = new ExecutionJournal();
        for (var i = 0; i < 20; i++)
            journal.Record(1, "read_file", $$"""{"path":"pages/p{{i}}.page"}""", ActionOutcome.Succeeded, new string('r', 400), WorkspaceEffect.None);

        var view = journal.Describe(maxChars: 2500);

        Assert.Matches(@"… the \d+ oldest call\(s\) of this step are not shown here\.", view.Text);
        Assert.Contains(20, view.VisibleActionIds);
    }

    // ── B: what the engine measured before the work ─────────────────────────────────────

    [Fact]
    public async Task The_reviewer_is_shown_what_the_engine_measured_before_the_work_once_per_step()
    {
        using var fx = new EngineFixture { EcosystemsOverride = [new NoNewBuildErrorsTests.WikiLint()] };
        fx.Write("wiki.lint", "rules");
        fx.Write("lint-report.txt", "");
        fx.Write("links.txt", "PASS /home\nPASS /about\nPASS /news\n");
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"check the links"}"""),
            Turn.Calls1("read_file", """{"path":"links.txt"}""", "r1"),
            Turn.Says("All 3 links pass."),
            Turn.Says("All 3 links pass - as the engine's own run shows."));
        var reviewer = new FakeChatProvider(Verdicts.Fail("where is the 3 from?"), Verdicts.Pass());

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "check that the wiki's links work");

        var first = string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content));
        Assert.Contains(Orchestrator.MeasuredBeforeTool, first, StringComparison.Ordinal);
        Assert.Contains("Tests before the work (wikilint, links): exit 0, 3 passed", first, StringComparison.Ordinal);
        var last = string.Join("\n", reviewer.Requests[^1].Messages.Select(m => m.Content));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(last, "Measured by the engine itself before any work"));
    }

    // ── C: a step is named by its index and its title ──────────────────────────────────

    [Fact]
    public async Task The_contract_review_sees_each_step_by_index_and_title_and_so_does_path_from()
    {
        var report = new PlanStep(Guid.NewGuid(), "Write the link report", StepStatus.Pending, [])
            { Output = new StepOutputSchema("s", 1, [new StepOutputField("report", StepOutputFieldType.Path, "the report")]) };
        var plan = new Plan(Guid.NewGuid(), [new PlanStep(Guid.NewGuid(), "Check the links", StepStatus.Pending, []), report]);
        var typed = new TypedCriterion(TypedCriterionKind.FileExists, NonEmpty: true, PathFromStep: 1, PathFromField: "report");
        var criterion = new SuccessCriterionDefinition("the file step 2 hands on as 'report' exists", "file_exists <step 2's report>", 0,
            Origin: CriterionOrigin.Proposed) { Typed = typed, Step = 1 };
        var planner = new FakeChatProvider(Turn.Says(
            """{"sources":[{"id":"O001","assessment":"a report"}],"checks":[],"forbidden_effects":[],"action_policy":null,"unresolved":null}"""));

        await PlanCheckReview.RunAsync(new PlanResult(IntentDisposition.Task, "links", plan) { Checks = [criterion] },
            "check the links and write a report", new WorkContext(null, "workspace", null, null, null, [], []),
            planner, "strong", new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default, workspaceRoot: Path.GetTempPath());

        var sent = planner.Requests[0].Messages;
        Assert.Contains("counted from 0", sent[0].Content, StringComparison.Ordinal);
        var body = sent[^1].Content!;
        using var doc = JsonDocument.Parse(body[body.IndexOf('{', body.IndexOf("Plan and draft final criteria:", StringComparison.Ordinal))..]);
        var steps = doc.RootElement.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(1, steps[1].GetProperty("index").GetInt32());
        var shown = doc.RootElement.GetProperty("engineCriteria")[0];
        Assert.Equal("Write the link report", shown.GetProperty("path_from").GetProperty("stepTitle").GetString());
        Assert.Equal("handed on by the step at index 1 ('Write the link report') as 'report'", shown.GetProperty("provenance").GetString());
    }
}
