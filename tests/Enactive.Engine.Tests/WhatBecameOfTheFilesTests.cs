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
    /// A file the run wrote and then removed is no longer "a file it changed" - it is gone, and the
    /// check says which call removed it. It does not fail the run: removing a file can be the job.
    /// </summary>
    [Fact]
    public async Task A_file_written_and_then_deleted_is_reported_gone_and_the_run_is_not_failed_for_it()
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "allow";   // delete_file always asks; refused, the file stays - and the check says PASS
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"draft.md","content":"a first draft"}""", "w1"),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"),
            Turn.Says("Drafted, then tidied up."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "draft and tidy");

        var check = Assert.Single(EngineChecks(events));
        Assert.StartsWith("FAIL — Produced file: draft.md", check.Summary, StringComparison.Ordinal);
        Assert.Contains("removed by delete_file", check.Summary, StringComparison.Ordinal);
        Assert.Contains("a recorded deletion", check.Summary, StringComparison.Ordinal);
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
