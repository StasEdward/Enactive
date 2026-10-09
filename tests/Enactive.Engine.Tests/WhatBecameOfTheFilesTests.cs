namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Tools;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// The engine checks, itself, what became of every file a run produced - for any kind of task.
///
/// <para>A run that ran no command reported "CHECKS: none - nothing verified this run beyond the
/// model's own report": every wiki task, every report, every piece of writing. And its FILES line was
/// what the run had WRITTEN, not what was there, because the list is cleared only on a revert. Both
/// said less than the engine could see.</para>
///
/// <para>What these checks are not allowed to be is proof. A run was once called finished on "the
/// file exists and is not empty" about a file the run had merely begun, on 2026-09-21. So they are
/// reports: not required, never blocking, never the reason a run is let through.</para>
/// </summary>
public sealed class WhatBecameOfTheFilesTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write it up"}""";

    private static WorkEvent[] EngineChecks(IEnumerable<WorkEvent> events)
        => events.Where(e => e.Kind == EventKind.CriterionEvaluated
                             && e.Summary.Contains("[engine check]", StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// THE ONE THAT MATTERS, and deliberately not a code project: a written summary, the kind of task
    /// that used to end with "nothing verified". The engine now says, from the file system, that the
    /// file is there and how big it is.
    /// </summary>
    [Fact]
    public async Task A_writing_task_ends_with_the_engine_having_checked_its_file()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"notes/summary.md","content":"# Summary\nThree findings.\n"}""", "w1"),
            Turn.Says("Wrote the summary."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "summarise the meeting");

        var check = Assert.Single(EngineChecks(events));
        Assert.StartsWith("PASS — Produced file: notes/summary.md", check.Summary, StringComparison.Ordinal);
        Assert.Contains("on disk", check.Summary, StringComparison.Ordinal);
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>
    /// A file the run wrote and the same step then removed is that step's draft: not listed among what the run produced,
    /// and the run is not failed for it. It used to be listed as FAIL, "removed by delete_file" - on 2026-10-09 (run 1549ce)
    /// two such drafts stood in the report beside the work that stood. A result another step removed is still said
    /// (A_draft_one_step_made_and_took_away_is_not_listed_and_a_lost_result_still_is).
    /// </summary>
    [Fact]
    public async Task A_file_written_and_then_deleted_by_the_same_step_is_not_listed_and_the_run_is_not_failed_for_it()
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "allow";   // delete_file always asks; refused, the file stays - and the check says PASS
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"draft.md","content":"a first draft"}""", "w1"),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"),
            Turn.Calls1("write_file", """{"path":"notes.md","content":"done"}""", "w2"),
            Turn.Says("Drafted, tidied up, wrote the notes."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "draft and tidy");

        var check = Assert.Single(EngineChecks(events));
        Assert.StartsWith("PASS — Produced file: notes.md", check.Summary, StringComparison.Ordinal);
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>
    /// The safety half of the 2026-09-21 lesson, now that a step can be done and unconfirmed. Its
    /// dependents no longer skip, so the guard that used to stop a check from promoting the run no
    /// longer fired. A declared check that runs and passes must still not turn a run with a missing
    /// verdict into Completed - it is reported, and it could still have failed the run.
    /// </summary>
    [Fact]
    public async Task A_passing_check_does_not_promote_a_run_whose_verdict_is_missing()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"result.txt","content":"done"}""", "w1"),
            Turn.Says("Wrote the result."));
        var reviewer = new FakeChatProvider(Turn.Says("not a verdict"), Turn.Says("nor this"));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"),
            router: Routers.WithReviewer(), reviewProvider: reviewer,
            successCriteria: [new("check", "echo verified")]), "write the result");

        // The check ran and passed - it is not being ignored.
        Assert.Contains(events, e => e.Kind == EventKind.CriterionEvaluated
                                     && e.Summary.StartsWith("PASS", StringComparison.Ordinal)
                                     && e.Summary.Contains("echo verified", StringComparison.Ordinal));
        // And it did not make the run Completed on a step nobody confirmed.
        Assert.False(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    // ── the check itself ──────────────────────────────────────────────────────────────────

    private static ArtifactRef Ref(string path) => new(Guid.NewGuid(), ArtifactKind.FileSet, path, path);

    [Fact]
    public void A_staged_file_is_present_an_empty_one_is_fine_and_a_rewritten_one_is_one_file()
    {
        var root = Directory.CreateTempSubdirectory("produced-files").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "__init__.py"), "");
            File.WriteAllText(Path.Combine(root, "report.md"), "twice written");

            var results = ProducedFiles.Check(
                [Ref("__init__.py"), Ref("report.md"), Ref("report.md"), Ref("pending.md")],
                pending: ["pending.md"], root, actions: []);

            Assert.Equal(3, results.Count);
            // Empty is correct for a Python package marker, and this engine is not for one kind of project.
            var marker = results.Single(r => r.Command == "__init__.py");
            Assert.Equal(CriterionOutcome.Passed, marker.Outcome);
            Assert.Equal("on disk, empty", marker.Detail);
            Assert.Contains("staged", results.Single(r => r.Command == "pending.md").Detail!, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_file_gone_with_no_call_that_removed_it_says_so()
    {
        var root = Directory.CreateTempSubdirectory("produced-files").FullName;
        try
        {
            var edit = new ExecutedAction(DateTimeOffset.UtcNow, 2, "write_file", "{}", ActionOutcome.Succeeded, "ok",
                WorkspaceEffect.Changed, ["vanished.md"]);

            var gone = Assert.Single(ProducedFiles.Check([Ref("vanished.md")], [], root, [edit]));

            Assert.Equal(CriterionOutcome.Failed, gone.Outcome);
            Assert.Contains("the last call to change it was write_file in step 2, which is not a deletion",
                gone.Detail!, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// A file the run made and the same step took away is that step's draft - not listed. Run 1549ce, 2026-10-09: two
    /// test files written and deleted in step 3 were in the report as FAIL beside the work that stood. One that another
    /// step took away is still said, and so is one that was there before the run.
    /// </summary>
    [Fact]
    public void A_draft_one_step_made_and_took_away_is_not_listed_and_a_lost_result_still_is()
    {
        var root = Directory.CreateTempSubdirectory("produced-files").FullName;
        try
        {
            var at = DateTimeOffset.UtcNow;
            ExecutedAction Write(int step, string path, int s) => new(at.AddSeconds(s), step, "write_file", "{}", ActionOutcome.Succeeded, "ok",
                WorkspaceEffect.Changed, [path]);
            ExecutedAction Delete(int step, string path, int s) => new(at.AddSeconds(s), step, "delete_file", "{}", ActionOutcome.Succeeded, "ok",
                WorkspaceEffect.Changed, [path], FileDeletion: true);
            ExecutedAction[] actions =
            [
                Write(3, "draft.cs", 1), Delete(3, "draft.cs", 2),           // a draft of one step
                Write(1, "report.md", 3), Delete(3, "report.md", 4),         // a result another step took away
                Write(3, "found.cs", 5), Delete(3, "found.cs", 6)            // a file that was there before the run
            ];

            var results = ProducedFiles.Check([Ref("draft.cs"), Ref("report.md"), Ref("found.cs")], [], root, actions,
                madeByRun: new HashSet<string> { "draft.cs", "report.md" });

            Assert.DoesNotContain(results, r => r.Command == "draft.cs");
            Assert.Equal(CriterionOutcome.Failed, results.Single(r => r.Command == "report.md").Outcome);
            Assert.Equal(CriterionOutcome.Failed, results.Single(r => r.Command == "found.cs").Outcome);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>What makes these safe to add to every run: they are never required, so never proof and never a block.</summary>
    [Fact]
    public void An_engine_check_can_neither_block_a_run_nor_prove_one()
    {
        var root = Directory.CreateTempSubdirectory("produced-files").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "there.md"), "x");
            var results = ProducedFiles.Check([Ref("there.md"), Ref("missing.md")], [], root, []);

            Assert.All(results, r => Assert.Equal(CriterionOrigin.System, r.Origin));
            var report = new SuccessReport(results);
            Assert.Empty(report.Blocking);
            Assert.False(report.Proved);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// The run's evidence is rebuilt from each step's journal, and it copied every field but one:
    /// how the call arrived. Everything read from the run-wide journal saw only native calls.
    /// </summary>
    [Fact]
    public void The_run_wide_evidence_keeps_how_each_call_arrived()
    {
        using var fx = new EngineFixture();
        var session = new RunSession(new RunScope(Guid.NewGuid(), Guid.NewGuid(),
            new RunBudget(ExecutionLimits.None, DateTimeOffset.UtcNow), []), []);
        session.ConfigurePlan(false, null);
        var step = session.BeginStep([], fx.Artifacts.BeginStep());
        step.Journal.Record(1, "read_file", "a.txt", ActionOutcome.Succeeded, "x", origin: ToolCallOrigin.Healed);

        Assert.Equal(ToolCallOrigin.Healed, Assert.Single(session.RunEvidence().Actions).Origin);
    }
}
