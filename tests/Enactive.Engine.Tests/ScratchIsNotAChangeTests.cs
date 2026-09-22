namespace Enactive.Engine.Tests;

using Enactive.Core.Execution;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// "It changed the workspace" is asked in several places, and the scratch area made them stop
/// meaning the same thing.
///
/// <para><c>ProofAudit</c> refuses to believe a step that reports there was nothing to do if the
/// step wrote something — and it decided that by the TOOL'S NAME. A step that wrote itself a
/// helper script under <c>.enactive/scratch/</c> on the way to finding that nothing had drifted
/// was therefore told "whatever it did, it was not nothing", over a file the stores do not
/// journal, the reviewer is not shown, and a rejection does not undo.</para>
///
/// <para>The stall guard asks a DIFFERENT question with the same words — "did this step get
/// anywhere" — and for it a scratch write really is progress. One answer was serving both.</para>
/// </summary>
public sealed class ScratchIsNotAChangeTests
{
    private const string Root = @"C:\ws";

    private static ExecutedAction Did(string tool, string arguments)
        => new(DateTimeOffset.UtcNow, 1, tool, arguments, ActionOutcome.Succeeded, "ok");

    private static ProofVerdict Verdict(params ExecutedAction[] calls)
        => ProofAudit.Check(
            new ProofClaim(ProofClaimKind.NothingToDo, new[] { 1 }, "nothing had drifted"),
            calls, Root);

    /// <summary>The reported case: a helper written to scratch is not the project changing.</summary>
    [Fact]
    public void A_helper_written_to_scratch_does_not_refute_nothing_to_do()
    {
        var verdict = Verdict(
            Did("read_file", """{"path":"config.json"}"""),
            Did("write_file", """{"path":".enactive/scratch/check.ps1","content":"..."}"""));

        Assert.True(verdict.Sound, verdict.Reason);
    }

    /// <summary>The guard against it: a write to the project still refutes the claim.</summary>
    [Fact]
    public void A_write_to_the_project_still_refutes_nothing_to_do()
    {
        var verdict = Verdict(
            Did("read_file", """{"path":"config.json"}"""),
            Did("write_file", """{"path":"config.json","content":"..."}"""));

        Assert.False(verdict.Sound);
        Assert.Contains("it was not nothing", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hole that predates the scratch area: the name list this check used was written before
    /// <c>delete_file</c> and <c>copy_file</c> existed, so a step that REMOVED a file could report
    /// that nothing needed doing and be believed.
    /// </summary>
    [Theory]
    [InlineData("delete_file", """{"path":"stale.txt"}""")]
    [InlineData("copy_file", """{"from":"a.txt","to":"b.txt"}""")]
    public void A_tool_added_after_the_old_list_still_refutes_nothing_to_do(string tool, string args)
    {
        var verdict = Verdict(Did("read_file", """{"path":"a.txt"}"""), Did(tool, args));

        Assert.False(verdict.Sound);
    }

    /// <summary>
    /// A move OUT of scratch changes the project, because the file arrives in it. Only a move with
    /// both ends in the working area leaves the project alone.
    /// </summary>
    [Theory]
    [InlineData(""".enactive/scratch/a.txt""", "report.md", false)]
    [InlineData("report.md", """.enactive/scratch/a.txt""", false)]
    [InlineData(""".enactive/scratch/a.txt""", """.enactive/scratch/b.txt""", true)]
    public void A_move_counts_unless_both_ends_are_scratch(string from, string to, bool sound)
    {
        var verdict = Verdict(
            Did("read_file", """{"path":"a.txt"}"""),
            Did("move_file", $$"""{"from":"{{from}}","to":"{{to}}"}"""));

        Assert.Equal(sound, verdict.Sound);
    }

    /// <summary>
    /// Arguments it cannot read count as a change. "I could not tell" must not become "nothing
    /// happened" in the one check standing between a step and being believed.
    /// </summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"content":"no path here"}""")]
    public void An_unreadable_path_counts_as_a_change(string args)
    {
        Assert.False(Verdict(Did("read_file", "{}"), Did("write_file", args)).Sound);
    }

    /// <summary>
    /// Without a workspace root nothing has changed: the caller that cannot say where the
    /// workspace is gets the older, blunter answer rather than a guess.
    /// </summary>
    [Fact]
    public void With_no_root_every_write_still_counts()
    {
        var verdict = ProofAudit.Check(
            new ProofClaim(ProofClaimKind.NothingToDo, new[] { 1 }, "nothing had drifted"),
            new[]
            {
                Did("read_file", """{"path":"config.json"}"""),
                Did("write_file", """{"path":".enactive/scratch/check.ps1","content":"..."}""")
            });

        Assert.False(verdict.Sound);
    }

    /// <summary>
    /// And the other question keeps its own answer: the stall guard counts a scratch write as
    /// progress, because the step did something it had not done before.
    /// </summary>
    [Fact]
    public void The_stall_guard_still_counts_a_scratch_write_as_progress()
    {
        Assert.True(MutatingTools.Changes("write_file"));
    }
}

/// <summary>
/// Both reviewers read the same shortened evidence, and only one of them was told what that means.
///
/// <para><c>ExecutionJournal.HeadAndTail</c> carries the incident: a reviewer read "1,645
/// characters cut from the middle" as proof the file did not contain what the agent had quoted,
/// and failed the step twice over a value inside those characters. Its comment says the rule
/// "belongs in the reviewer's instructions, where it is said ONCE" — and it was said once, in the
/// execution reviewer's, while the proof pass was added later without it.</para>
///
/// <para>Text assertions, which are weak, on a defect that was an OMISSION, which they do catch:
/// the way this went wrong was a prompt not having the clause at all.</para>
/// </summary>
public sealed class ShortenedEvidenceTests
{
    [Fact]
    public void The_execution_reviewer_is_told_a_cut_is_not_an_absence()
    {
        Assert.Contains("CUT, not absent", Enactive.Agents.Reviewer.ExecutionSystemPrompt,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void The_proof_reviewer_is_told_the_same()
    {
        var prompt = Enactive.Agents.Reviewer.ProofSystemPrompt;

        Assert.Contains("Neither is an absence", prompt, StringComparison.Ordinal);
        Assert.Contains("a part you were not shown", prompt, StringComparison.Ordinal);
    }
}
