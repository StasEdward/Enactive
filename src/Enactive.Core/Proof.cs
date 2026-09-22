namespace Enactive.Core.Execution;

using Enactive.Core.Tools;

/// <summary>
/// What a proof pass says about a step: whether the evidence SHOWS the step's objective was met,
/// and which calls show it.
/// </summary>
public enum ProofClaimKind
{
    /// <summary>The evidence shows it, and <see cref="ProofClaim.Calls"/> says where.</summary>
    Shown,

    /// <summary>
    /// A call could have shown it and none does. This is the answer the whole pass exists to
    /// collect: a report whose every fact is true and whose conclusion nothing supports.
    /// </summary>
    NotShown,

    /// <summary>
    /// The objective is not the kind of thing a tool call settles - an analysis, a judgement, a
    /// document. Offered prominently and never held against the step.
    ///
    /// <para>Without this answer the pass would be asking "should a command have been run", which is
    /// the exact question the execution reviewer was told in 2026-09-07 20:16 to stop asking: a step
    /// titled "Analyze test coverage" was failed for running no coverage command, and the retry then
    /// ran commands to satisfy the reviewer rather than to learn anything. Which tools reach an
    /// answer are the agent's to choose.</para>
    /// </summary>
    NotByAnyCall,

    /// <summary>
    /// The objective was CONDITIONAL - change something if it has drifted, fix something if it is
    /// broken - and the calls show the condition does not hold. The step is finished and correct:
    /// there was nothing to do.
    ///
    /// <para>Reported 2026-09-08 15:04. A "Documentation Sync" run planned two steps, "Analyze
    /// codebase functionality" and "Update or create README.md". The analysis found no drift, so the
    /// second step read the same files, said so, and changed nothing - which was the right answer.
    /// The pass had three words for it and all three were wrong: <see cref="Shown"/> would be a lie
    /// about what the reads show, <see cref="NotByAnyCall"/> would be false because a call could
    /// settle it, and <see cref="NotShown"/> - the one it chose - failed the run for doing the work
    /// correctly. Ten minutes later the same request passed, because that time the step made no call
    /// at all and the pass was skipped. Identical work, opposite verdicts, decided by an artefact.
    /// </para>
    ///
    /// <para>Unlike <see cref="NotByAnyCall"/> this one is NOT free: "there was nothing to do" is a
    /// FINDING, and a finding rests on having looked. It is audited exactly like <see cref="Shown"/>
    /// - named calls, resolved by number - plus one thing only the engine can see: a step that
    /// changed the workspace did not find nothing to do.</para>
    /// </summary>
    NothingToDo
}

/// <summary>
/// A proof pass's answer, before anything has checked it.
/// </summary>
/// <param name="Calls">
/// The numbers of the calls, as the evidence numbered them, that show the objective was met. A
/// NUMBER rather than a description on purpose: a description can only be judged by another
/// judgement, and a number can be looked up.
/// </param>
public sealed record ProofClaim(ProofClaimKind Kind, IReadOnlyList<int> Calls, string What);

/// <summary>Whether a step's reported success actually follows from what happened.</summary>
public sealed record ProofVerdict(bool Sound, string Reason);

/// <summary>
/// Checks a proof claim against what the step actually did.
///
/// <para><b>The gap this closes.</b> The execution reviewer asks whether a report is TRUE against
/// the evidence, and a report can be true in every particular while its conclusion follows from
/// none of it. <c>PLAN_v2.md</c> §11 carried the case: a verification step whose every stated fact
/// was in the evidence concluded that a test had been "targeted" — the test was already failing, and
/// stayed failing, and the step passed. Truth is not soundness.</para>
///
/// <para><b>Why this is not another prompt clause.</b> Four were added to the execution reviewer
/// during 2026-09-07 and a fifth was declined, on the grounds that no wording fixes a model that
/// misreads what it is shown. So the pass is asked for something that does not need to be trusted:
/// a POINTER. It names calls by number, and the engine resolves those numbers against the journal it
/// already holds. Every way a claim fails here EXCEPT one is decided by the engine looking things
/// up, not by a model being persuasive.</para>
///
/// <para>That one — <see cref="ProofClaimKind.NotShown"/> — IS the model's own answer, and that is
/// stated rather than dressed up. It is acted on because the question is closed, narrow, and carries
/// two explicit escapes for the cases where a "no" would be wrong:
/// <see cref="ProofClaimKind.NotByAnyCall"/> for the objective no call can settle, which is what
/// made the execution reviewer overreach before, and <see cref="ProofClaimKind.NothingToDo"/> for
/// the conditional objective whose condition does not hold. A model choosing NotShown over both has
/// said the objective was the kind of thing a call could settle, that it did need settling, and that
/// no call settles it.</para>
/// </summary>
public static class ProofAudit
{
    /// <summary>
    /// The verdict on a claim, given the calls the step actually made — in the same order and
    /// numbering the evidence used.
    /// </summary>
    /// <param name="workspaceRoot">
    /// Where the workspace is, so "it changed the workspace" can be answered by the path a call
    /// touched rather than by the tool's name alone. Null keeps the older, blunter answer: every
    /// write counts, including one into the worker's own scratch area.
    /// </param>
    public static ProofVerdict Check(
        ProofClaim claim, IReadOnlyList<ExecutedAction> shown, string? workspaceRoot = null)
    {
        switch (claim.Kind)
        {
            // Never a failure. A step whose work is reading, reasoning or writing has nothing to
            // cite, and demanding a citation from it is demanding it run a command for the reviewer.
            case ProofClaimKind.NotByAnyCall:
                return new ProofVerdict(true,
                    "no tool call could settle this step's objective"
                    + (string.IsNullOrWhiteSpace(claim.What) ? "" : ": " + claim.What));

            case ProofClaimKind.NotShown:
                return new ProofVerdict(false,
                    "the report is consistent with the evidence, but nothing in the evidence shows "
                    + "the step's objective was met"
                    + (string.IsNullOrWhiteSpace(claim.What) ? "" : ": " + claim.What));
        }

        // ── Shown and NothingToDo. Now the pointers get looked up. ───────────
        //
        // Both are citations and both are resolved by the same code on purpose. The difference
        // between them is what the cited calls are said to establish - that the objective was met,
        // or that it did not need meeting - and that is the model's half. Whether the numbers are
        // real is the engine's, and it must not come out differently for the two answers.

        var nothingToDo = claim.Kind == ProofClaimKind.NothingToDo;

        // Claims to point at something and points at nothing. An absence is not an answer, and this
        // is the shape a model reaches for when it wants to agree without having found anything.
        if (claim.Calls.Count == 0)
            return new ProofVerdict(false,
                nothingToDo
                    ? "the step reported that nothing needed doing, and named no call that looked. "
                      + "Nothing needing doing is a finding, and a finding rests on having looked"
                    : "the step's success was said to be shown by the evidence, but no call was "
                      + "named as showing it");

        // Only the engine can see this one, and only the engine should: a step that changed the
        // workspace did not find nothing to do. Checked before the citations because it settles the
        // answer whatever they say - a write that happened is not undone by pointing at a read.
        if (nothingToDo)
        {
            // By the PATH, not by the tool's name, when the caller can say where the workspace is.
            // A step that wrote itself a helper script under .enactive/scratch/ on the way to
            // finding that nothing needed doing has not changed the project - the stores do not
            // journal that write, the reviewer is not shown it, and a rejection does not undo it -
            // and refusing its report over it would be this check calling the agent's own notes
            // "the workspace". Null root keeps the old answer, which counts every write.
            var changed = shown
                .Where(a => a.Outcome == ActionOutcome.Succeeded)
                .Where(a => workspaceRoot is null
                    ? MutatingTools.Changes(a.Tool)
                    : MutatingTools.ChangedTheWorkspace(a.Tool, a.Arguments, workspaceRoot))
                .Select(a => a.Tool)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (changed.Length > 0)
                return new ProofVerdict(false,
                    "the step reported that nothing needed doing, but it changed the workspace ("
                    + string.Join(", ", changed) + "). Whatever it did, it was not nothing");
        }

        var missing = claim.Calls.Where(n => n < 1 || n > shown.Count).ToArray();
        if (missing.Length > 0)
            return new ProofVerdict(false,
                $"the proof cites call(s) {string.Join(", ", missing)}, and this step made "
                + $"{shown.Count}. A call that was never made cannot show anything");

        // Every call it points at went wrong. This is the documented case: the conclusion rests on
        // work that did not work, and every sentence about it can still be true.
        //
        // Answered counts as support - "the file is not there" is a result, and a step whose
        // objective was to check that is proven by it. Refused does not: nothing ran.
        var cited = claim.Calls.Select(n => shown[n - 1]).ToArray();
        if (cited.All(a => a.Outcome is ActionOutcome.Failed or ActionOutcome.Refused))
            return new ProofVerdict(false,
                "the only call(s) named as proof are " + Describe(cited)
                + (nothingToDo
                    ? " — they do not show that there was nothing to do"
                    : " — the step's success does not follow from them"));

        return new ProofVerdict(true,
            (nothingToDo ? "nothing needed doing, shown by " : "shown by ") + Numbers(claim.Calls)
            + (string.IsNullOrWhiteSpace(claim.What) ? "" : ": " + claim.What));
    }

    private static string Numbers(IReadOnlyList<int> calls)
        => calls.Count == 1 ? $"call {calls[0]}" : "calls " + string.Join(", ", calls);

    private static string Describe(IReadOnlyList<ExecutedAction> cited)
    {
        var failed = cited.Count(a => a.Outcome == ActionOutcome.Failed);
        var refused = cited.Count(a => a.Outcome == ActionOutcome.Refused);

        if (failed > 0 && refused > 0)
            return $"{failed} that failed and {refused} that never ran";
        return failed > 0
            ? (failed == 1 ? "a call that failed" : $"{failed} calls that failed")
            : (refused == 1 ? "a call that never ran" : $"{refused} calls that never ran");
    }
}
