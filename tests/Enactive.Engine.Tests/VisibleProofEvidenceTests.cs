namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Tools;

public sealed class VisibleProofEvidenceTests
{
    private static ExecutionJournal LongJournal()
    {
        var journal = new ExecutionJournal();
        for (var i = 1; i <= 100; i++)
            journal.Record(1, "read_file", $"file-{i}.txt", ActionOutcome.Succeeded, $"result {i}");
        return journal;
    }

    [Theory]
    [InlineData(ProofClaimKind.Shown)]
    [InlineData(ProofClaimKind.NothingToDo)]
    public void A_hidden_success_is_not_valid_proof(ProofClaimKind kind)
    {
        var view = LongJournal().Describe(maxChars: 1200);

        Assert.DoesNotContain("[1] ->", view.Text);
        Assert.DoesNotContain(1, view.VisibleActionIds);
        Assert.True(view.ActionsOmitted);
        Assert.True(view.IsTruncated);
        Assert.Null(view.Cited(1));
        var verdict = ProofAudit.Check(new(kind, new[] { 1 }, "hidden result"), view);
        Assert.False(verdict.Sound);
        Assert.Contains("not shown", verdict.Reason);
    }

    [Theory]
    [InlineData(ActionOutcome.Succeeded, true)]
    [InlineData(ActionOutcome.Answered, true)]
    [InlineData(ActionOutcome.Failed, false)]
    [InlineData(ActionOutcome.Refused, false)]
    public void Visible_calls_keep_their_original_numbers_and_outcomes(ActionOutcome outcome, bool sound)
    {
        var journal = LongJournal();
        journal.Record(1, "run_command", "test", outcome, "last result");
        var view = journal.Describe(maxChars: 1200);

        Assert.Contains("[101] -> run_command", view.Text);
        Assert.Equal(outcome, view.Cited(101)!.Outcome);
        Assert.Equal(sound, ProofAudit.Check(new(ProofClaimKind.Shown, new[] { 101 }, "last"), view).Sound);
        // A visible success does not legitimize another citation that was hidden.
        Assert.False(ProofAudit.Check(new(ProofClaimKind.Shown, new[] { 1, 101 }, "both"), view).Sound);
    }

    [Fact]
    public void A_slice_and_its_mapping_remain_stable_after_the_journal_changes()
    {
        var journal = LongJournal();
        var mark = journal.Mark();
        journal.Record(2, "run_command", "test", ActionOutcome.Succeeded, "passed");
        var view = journal.Describe(mark);
        journal.Discard(mark);
        journal.Record(2, "run_command", "other", ActionOutcome.Failed, "failed");

        Assert.Equal(new[] { 1 }, view.VisibleActionIds);
        Assert.Contains("[1] -> run_command test", view.Text);
        Assert.Equal("test", view.Cited(1)!.Arguments);
        Assert.True(ProofAudit.Check(new(ProofClaimKind.Shown, new[] { 1 }, "test"), view).Sound);
        Assert.False(ProofAudit.Check(new(ProofClaimKind.Shown, new[] { 2 }, "other"), view).Sound);
    }

    [Fact]
    public void Hidden_writes_still_refute_nothing_to_do()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "write_file", "{}", ActionOutcome.Succeeded, "created", Enactive.Core.Tools.WorkspaceEffect.Changed);
        for (var i = 0; i < 100; i++)
            journal.Record(1, "read_file", "a.txt", ActionOutcome.Succeeded, "content");
        var view = journal.Describe(maxChars: 1200);

        Assert.DoesNotContain(1, view.VisibleActionIds);
        var verdict = ProofAudit.Check(new(ProofClaimKind.NothingToDo, new[] { 101 }, "unchanged"), view);
        Assert.False(verdict.Sound);
        Assert.Contains("changed the workspace", verdict.Reason);
    }

    [Fact]
    public void Truncation_flags_describe_the_rendered_view()
    {
        var journal = new ExecutionJournal();
        var empty = journal.Describe();
        Assert.Empty(empty.VisibleActionIds);
        Assert.False(empty.IsTruncated);
        Assert.Null(empty.Cited(1));

        journal.Record(1, "read_file", "a.txt", ActionOutcome.Succeeded, "short");
        Assert.False(journal.Describe().IsTruncated);
        journal.Record(1, "run_command", new string('a', 500), ActionOutcome.Succeeded, new string('x', 10000));
        journal.NotePriorTranscript();
        var view = journal.Describe();
        Assert.False(view.ActionsOmitted);
        Assert.True(view.OutputsTruncated);
        Assert.True(view.ArgumentsTruncated);
        Assert.True(view.HasPriorTranscript);
        Assert.True(view.IsTruncated);
        Assert.Equal(new[] { 1, 2 }, view.VisibleActionIds.Order());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(100, true)]
    public async Task The_orchestrator_audits_the_same_evidence_that_the_reviewer_received(int citation, bool sound)
    {
        using var fx = new EngineFixture { ShortReview = false };
        // A hundred DIFFERENT reads: the same read a hundred times in one turn is now run once.
        for (var i = 1; i <= 100; i++) fx.Write($"a{i}.txt", "content");
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"task","title":"inspect","steps":[{"title":"inspect files","dependsOn":[]}]}"""),
            new Turn(Calls: Enumerable.Range(1, 100)
                .Select(i => new ToolCall($"r{i}", "read_file", $$"""{"path":"a{{i}}.txt"}""")).ToArray()),
            Turn.Says("Inspected."));
        var verdict = Verdicts.Combined(Verdicts.Shown("the file was read", citation));
        var reviewer = new FakeChatProvider(verdict, verdict);

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                reviewRetries: 0, checkSoundness: true, evidenceBudget: 1500), "inspect files");

        Assert.Equal(sound ? 1 : 2, reviewer.Requests.Count);
        var prompt = string.Join("\n", reviewer.Requests.Last().Messages.Select(m => m.Content));
        Assert.DoesNotContain("[1] ->", prompt);
        Assert.Contains("[100] ->", prompt);
        Assert.Equal(sound, events.Last().Outcome() == RunOutcomeKind.Completed);
        if (!sound)
        {
            Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
            Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved && e.Summary.Contains("not shown"));
            Assert.DoesNotContain(events, e => e.Kind == EventKind.ReviewFailed);
        }
    }
}
