namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Xunit;

/// <summary>
/// A step whose objective was conditional, and whose condition does not hold.
///
/// <para><b>What was reported.</b> Two runs of the same "Documentation Sync" template, on the same
/// workspace, ten minutes apart, from the same two-step plan: "Analyze codebase functionality" then
/// "Update or create README.md". Both times the analysis found no drift and both times the second
/// step changed nothing, which was the right answer. The first run FAILED and the second COMPLETED.
/// </para>
///
/// <para>The first run's second step read five files and said so. The proof pass had three words
/// available and every one of them was wrong for what had happened: "yes" would misdescribe what
/// reads of unchanged files show, "not-by-any-call" would be false because a call could settle it,
/// and "no" - which it chose - fails the step. <i>"The agent only read files to verify the README;
/// it did not write/update it."</i> True, and the run was failed for doing the work correctly.</para>
///
/// <para>The second run's second step made no call at all - it answered from what the first step had
/// read - and <c>ProveAsync</c> skips a step with no calls, so nothing asked the question. Identical
/// work, opposite verdicts, decided by whether the step happened to touch a tool. That second hole
/// is a separate fix; this file is about giving the honest answer a word.</para>
///
/// <para><b>Why "nothing to do" is not free.</b> <see cref="ProofClaimKind.NotByAnyCall"/> is offered
/// freely and never held against a step, because demanding a citation from an analysis is demanding
/// it run a command for the reviewer. This answer is the opposite: it is a FINDING about the
/// workspace, and a finding rests on having looked. So it is audited exactly like
/// <see cref="ProofClaimKind.Shown"/> - by number, against the journal - and carries one check only
/// the engine can make, which is that a step that changed a file did not find nothing to do.</para>
/// </summary>
public sealed class NothingToDoTests
{
    private const string OneStepPlan = """
        {"disposition":"task","title":"sync the README",
         "steps":[{"title":"Update or create README.md","dependsOn":[]}]}
        """;

    private static ExecutedAction Action(ActionOutcome outcome, string tool = "read_file")
        => new(DateTimeOffset.UtcNow, 1, tool, "{}", outcome, "output");

    private static string[] Failures(IEnumerable<WorkEvent> events)
        => events.Where(e => e.Kind == EventKind.ReviewFailed).Select(e => e.Summary).ToArray();

    // -- the audit, on its own ----------------------------------------------

    /// <summary>
    /// The reported case, at the level where it is decided. A read that found the file already
    /// correct is what shows there was nothing to do, and the step is not failed for it.
    /// </summary>
    [Fact]
    public void A_conditional_objective_whose_condition_does_not_hold_is_not_a_failure()
    {
        var calls = new[] { Action(ActionOutcome.Succeeded), Action(ActionOutcome.Succeeded) };

        var verdict = EvidenceFixture.Check(
            new ProofClaim(ProofClaimKind.NothingToDo, new[] { 1, 2 },
                           "the README already matches the code"), calls);

        Assert.True(verdict.Sound);
        Assert.Contains("nothing needed doing", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("calls 1, 2", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole reason this answer is audited rather than waved through. "There was nothing to do"
    /// with no call behind it is a step that did nothing, wearing the coat of a step that checked.
    /// </summary>
    [Fact]
    public void Nothing_to_do_that_names_no_call_is_not_believed()
    {
        var verdict = EvidenceFixture.Check(
            new ProofClaim(ProofClaimKind.NothingToDo, Array.Empty<int>(), "it all looked fine"),
            new[] { Action(ActionOutcome.Succeeded) });

        Assert.False(verdict.Sound);
        Assert.Contains("named no call that looked", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A contradiction the engine can see without judging anything: the step says nothing needed
    /// doing and the journal says it wrote a file. Checked before the citations, because a write
    /// that happened is not undone by pointing at a read.
    /// </summary>
    [Fact]
    public void Nothing_to_do_from_a_step_that_changed_a_file_is_not_believed()
    {
        var calls = new[]
        {
            Action(ActionOutcome.Succeeded),
            Action(ActionOutcome.Succeeded, "write_file")
        };

        var verdict = EvidenceFixture.Check(
            new ProofClaim(ProofClaimKind.NothingToDo, new[] { 1 }, "nothing had drifted"), calls);

        Assert.False(verdict.Sound);
        Assert.Contains("changed the workspace", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("write_file", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A write that did NOT work leaves the workspace as it was, so it is not a contradiction. The
    /// check is about what happened, not about what was attempted - the same rule the citations
    /// follow one line below.
    /// </summary>
    [Fact]
    public void A_write_that_failed_does_not_contradict_nothing_to_do()
    {
        var calls = new[]
        {
            Action(ActionOutcome.Succeeded),
            Action(ActionOutcome.Failed, "write_file")
        };

        Assert.True(EvidenceFixture.Check(
            new ProofClaim(ProofClaimKind.NothingToDo, new[] { 1 }, "nothing had drifted"),
            calls).Sound);
    }

    /// <summary>The citation is resolved the same way it is for "yes". A number nobody made is not one.</summary>
    [Fact]
    public void Nothing_to_do_citing_a_call_nobody_made_is_not_believed()
    {
        var verdict = EvidenceFixture.Check(
            new ProofClaim(ProofClaimKind.NothingToDo, new[] { 9 }, "call nine says so"),
            new[] { Action(ActionOutcome.Succeeded) });

        Assert.False(verdict.Sound);
        Assert.Contains("cites call(s) 9", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A read that failed did not read anything, so it cannot be what showed the file was already
    /// right. Same rule as a fix reported over a command that did not run.
    /// </summary>
    [Fact]
    public void Nothing_to_do_resting_only_on_calls_that_did_not_work_is_not_believed()
    {
        var calls = new[] { Action(ActionOutcome.Failed), Action(ActionOutcome.Refused) };

        var verdict = EvidenceFixture.Check(
            new ProofClaim(ProofClaimKind.NothingToDo, new[] { 1, 2 }, "nothing to change"), calls);

        Assert.False(verdict.Sound);
        Assert.Contains("nothing to do", verdict.Reason, StringComparison.Ordinal);
    }

    // -- reading the answer -------------------------------------------------

    [Theory]
    [InlineData("""{"shown":"nothing-to-do","calls":[1],"what":"already correct"}""")]
    [InlineData("""{"shown":"NOTHING_TO_DO","calls":[1],"what":"already correct"}""")]
    [InlineData("""{"shown":"nothingtodo","calls":[1],"what":"already correct"}""")]
    public void The_fourth_answer_is_read(string json)
        => Assert.Equal(ProofClaimKind.NothingToDo, Reviewer.ParseProof(json)!.Kind);

    // -- through the engine -------------------------------------------------

    /// <summary>
    /// The reported run, end to end. The step reads the file, finds it already correct, changes
    /// nothing and says so; the run completes and the timeline says why rather than saying "done".
    /// </summary>
    [Fact]
    public async Task A_step_with_nothing_to_change_no_longer_fails_the_run()
    {
        using var fx = new EngineFixture();
        fx.Write("README.md", "# The project\nIt does what the code does.\n");

        var worker = new FakeChatProvider(
            Turn.Says(OneStepPlan),
            Turn.Calls1("read_file", """{"path":"README.md"}""", "r1"))
        {
            WhenExhausted = Turn.Says("The README already matches the code. I changed nothing.")
        };

        var reviewer = new FakeChatProvider(
            Verdicts.Combined(Verdicts.NothingToDo("the README already matches the code", 1)));

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                     reviewRetries: 0, checkSoundness: true),
            "Check README.md against the code and correct what has drifted");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Empty(Failures(events));
        Assert.Contains(events, e => e.Kind == EventKind.ReviewPassed
                                     && e.Summary.Contains("nothing needed doing", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the escape does not become a way out of the work. The step runs a command, writes the
    /// file, and then claims there was nothing to do; the engine sees the write and rejects it.
    /// Without this the new answer would be a better version of the hole it was added to close.
    ///
    /// <para>The command is there to reach the check at all: a step that only writes gets a CONTENT
    /// review, and the soundness pass is asked of execution reviews. One that also ran something is
    /// judged on its calls, which is the case where this claim could be made and be false.</para>
    ///
    /// <para>The failure names the tool, and it can only name it because the journal recorded that
    /// write as SUCCEEDED - so a run in which the write had been refused would complete here and
    /// fail this test, rather than pass it for the wrong reason.</para>
    /// </summary>
    [Fact]
    public async Task A_step_that_changed_a_file_cannot_claim_there_was_nothing_to_do()
    {
        using var fx = new EngineFixture();
        fx.Write("README.md", "# The project\nstale\n");

        var worker = new FakeChatProvider(
            Turn.Says(OneStepPlan),
            Turn.Calls1("run_command", """{"command":"echo looking"}""", "c1"),
            Turn.Calls1("write_file", """{"path":"README.md","content":"# fresh"}""", "w1"))
        {
            WhenExhausted = Turn.Says("Nothing needed changing.")
        };

        var reviewer = new FakeChatProvider(
            Verdicts.Combined(Verdicts.NothingToDo("it was already right", 1)));

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                     reviewRetries: 0, checkSoundness: true),
            "Check README.md against the code and correct what has drifted");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(Failures(events), s => s.Contains("changed the workspace", StringComparison.Ordinal)
                                               && s.Contains("write_file", StringComparison.Ordinal));
    }
}
