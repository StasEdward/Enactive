namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// A step whose work was done but whose review returned no verdict is DONE, NOT VERIFIED - not
/// "incomplete" - and what depends on it still runs.
///
/// <para><b>Run 4b3b7457, 2026-09-27.</b> Step 1 implemented a method and passed review. Step 2 wrote
/// a harness; twenty tests passed, confirmed by an independent re-run. The reviewer's answer for step
/// 2 lacked one field, <c>$.claims[3].requirements[2].verification</c>, so step 2 was recorded
/// Incomplete, step 3 was skipped because "a dependency did not succeed", and the report read
/// "nothing verified this run" - with both files on disk and working.</para>
///
/// <para>Refusing to call step 2 verified was right: the engine cannot confirm what the reviewer did
/// not say. Concluding it had not been done, and stopping everything after it, was the loss. So the
/// two are now separate outcomes, and only the second one blocks.</para>
/// </summary>
public sealed class DoneButNotVerifiedTests
{
    private const string ThreeSteps = """
        {"disposition":"task","title":"Three in a row",
         "steps":[{"title":"Write one","dependsOn":[]},
                  {"title":"Write two","dependsOn":[0]},
                  {"title":"Write three","dependsOn":[1]}]}
        """;

    private static StepOutcomeKind? OutcomeOf(IEnumerable<WorkEvent> events, int step)
        => events.Last(e => e.Kind == EventKind.StepCompleted && e.StepNo() == step).StepOutcome();

    /// <summary>
    /// THE ONE THAT MATTERS - the 27 September run, in miniature. The middle step's review cannot be
    /// read twice running, so there is no verdict on it. It is recorded as done and not verified, the
    /// step after it RUNS and passes, and the run is still not Completed: a run is finished only on
    /// verdicts that were actually given.
    /// </summary>
    [Fact]
    public async Task A_step_with_no_verdict_is_done_not_verified_and_its_dependent_still_runs()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(
            Turn.Says(ThreeSteps),
            Turn.Calls1("write_file", """{"path":"one.txt","content":"1"}""", "w1"),
            Turn.Says("Wrote one."),
            Turn.Calls1("write_file", """{"path":"two.txt","content":"2"}""", "w2"),
            Turn.Says("Wrote two."),
            Turn.Calls1("write_file", """{"path":"three.txt","content":"3"}""", "w3"),
            Turn.Says("Wrote three."));
        var reviewer = new FakeChatProvider(
            Verdicts.Pass(),
            Turn.Says("this is not a verdict"),
            Turn.Says("nor is this"),
            Verdicts.Pass());

        var events = await fx.RunAsync(
            fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "write three files in order");

        Assert.Equal(StepOutcomeKind.Succeeded, OutcomeOf(events, 1));
        Assert.Equal(StepOutcomeKind.DoneUnverified, OutcomeOf(events, 2));

        // What the loss was: the dependent ran, and its work is on disk.
        Assert.Equal(StepOutcomeKind.Succeeded, OutcomeOf(events, 3));
        Assert.Equal("3", fx.Read("three.txt"));

        // And what did NOT change: no verdict is not a pass, so the run is not Completed, and it says
        // why rather than nothing.
        Assert.False(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Contains(events, e => e.Summary.Contains("[2] Write two - done, not verified", StringComparison.Ordinal));

        // The middle card says what it is, with the reason the verdict was missing.
        var middle = events.Last(e => e.Kind == EventKind.StepCompleted && e.StepNo() == 2);
        Assert.Contains("DONE, NOT VERIFIED", middle.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The safety half. A reviewer that DID reach a verdict ending the step - a prohibition the work
    /// violated - must not be read as "no verdict", or a step that deleted what it was told not to
    /// would release its dependents to build on that. Such a verdict also carries an IncompleteReason,
    /// which is exactly why the flag, and not the reason, decides.
    /// </summary>
    [Fact]
    public async Task A_violated_prohibition_is_a_verdict_and_is_never_read_as_a_missing_one()
    {
        var tools = new ToolRegistry(EngineFixture.ShippedTools());
        var journal = new ExecutionJournal();
        var accounting = new ToolResultAccounting(tools, new(tools.Definitions), new(tools.Definitions),
            new(), journal, new(), false, 1, ToolCallOrigin.Native);
        accounting.Record(new("delete", "run_command", """{"command":"del Intervals.cs"}"""),
            new(ToolResults.Ok("operation completed"), 0, 1));
        journal.Record(1, "write_file", "restore", ActionOutcome.Succeeded, "restored");

        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("Restoration makes the deletion acceptable", 2), "run").Text!)!;
        var part = answer["claims"]![0]!["requirements"]![0]!;
        part["requirement"] = "Do not delete files";
        part["global"] = true;
        part["prohibitions"] = new JsonArray("file-deletion");

        var result = await new Reviewer().ReviewWithProofAsync("restore", "done", journal.Describe(), [], [],
            RequestObligations.Create("Do not delete files."), new FakeChatProvider(Turn.Says(answer.ToJsonString())),
            "strong", default);

        Assert.NotNull(result.IncompleteReason);
        Assert.False(result.VerdictUnavailable);
    }

    /// <summary>The other side of the same flag: a reviewer that returned nothing readable IS missing a verdict.</summary>
    [Fact]
    public async Task A_reviewer_that_never_answers_readably_has_no_verdict()
    {
        var reviewer = new FakeChatProvider(Turn.Says("not json"), Turn.Says("still not json"));

        var result = await new Reviewer().ReviewAsync("write", "Wrote it.", "no calls", [],
            reviewer, "review", default);

        Assert.NotNull(result.IncompleteReason);
        Assert.True(result.VerdictUnavailable);
    }

    [Fact]
    public void The_step_card_and_the_run_summary_both_name_it()
    {
        Assert.Equal("Done, not verified — the reviewer did not return a verdict",
            RunOutcomeWords.StepActivity(StepOutcomeKind.DoneUnverified, "the reviewer did not return a verdict"));

        var why = RunOutcomeWords.Explain([StepOutcomeKind.Succeeded, StepOutcomeKind.DoneUnverified],
            ["the reviewer did not return a verdict"], cycle: false);

        // A run held short of Completed by nothing else must still explain itself.
        Assert.NotNull(why);
        Assert.Contains("1 step(s) done but not verified", why, StringComparison.Ordinal);
    }

    [Fact]
    public void The_next_step_is_told_the_work_exists_and_is_unconfirmed()
    {
        var plan = Enactive.Core.Tasks.LinearPlan.FromTitles(["one", "two", "three"]);
        var text = StepBoundary.Describe(plan, plan.Steps[2].Id,
            completed: [plan.Steps[0].Id], unverified: [plan.Steps[1].Id]);

        Assert.Contains("\"state\":\"completed\"", text, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"completed-unverified\"", text, StringComparison.Ordinal);
        Assert.Contains("check what this step relies on from it", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A resumed run must not forget. The outcome is written into the checkpoint by name and read back
    /// by name; were it lost, the restored step would fall back to its status, Done, and be taken for
    /// a pass - and the run could then reach Completed on a verdict never given.
    /// </summary>
    [Fact]
    public void A_checkpoint_keeps_the_outcome_across_a_resume()
        => Assert.Equal(StepOutcomeKind.DoneUnverified,
            Enactive.Core.History.CheckpointNames.OutcomeOf(StepOutcomeKind.DoneUnverified.ToString()));
}
