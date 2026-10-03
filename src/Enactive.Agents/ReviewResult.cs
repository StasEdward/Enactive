namespace Enactive.Agents;

/// <summary>
/// Reviewer verdict for a step, plus what asking cost.
///
/// <para>The review call sits outside the tool loop, so its tokens were never counted; on a run that
/// reviews content, the reviewer reads whole documents on the most expensive model bound, which made
/// the uncounted share the LARGEST part of some runs. The counts cover the re-ask too, when there
/// was one - two calls were made, and two calls were paid for.</para>
/// </summary>
public sealed record ReviewResult(
    bool Pass, string Notes, int PromptTokens = 0, int CompletionTokens = 0)
{
    public string? BudgetExhausted { get; init; }
    public string? IncompleteReason { get; init; }

    /// <summary>
    /// The reviewer could not return a usable verdict: an error, a response refused after
    /// clarification, no verdict at all, or its own statement that it could not tell. Set only where
    /// that is what happened, and false by default, so any path not marked keeps its old outcome.
    ///
    /// <para>A step whose verdict is missing becomes <see cref="Enactive.Core.Events.StepOutcomeKind.DoneUnverified"/>
    /// and releases its dependents; a step with a damning verdict does not.</para>
    /// </summary>
    public bool VerdictUnavailable { get; init; }

    /// <summary>
    /// The reviewer answered, and its answer was that it could not tell - as opposed to an answer that
    /// could not be used at all. Both leave the verdict missing; they are different reasons.
    /// </summary>
    public bool Undecided { get; init; }

    /// <summary>Concrete semantic defects, distinct from malformed review or unavailable evidence.</summary>
    public string? RepairAdvice { get; init; }

    /// <summary>
    /// The files of the step the reviewer found right as they are - asked for, nothing wrong in them - on a fail that is
    /// somewhere else: the report, the process, another file. A step rejected on such a review keeps these and has the
    /// rest of what it changed put back. Empty by default, so every review that names none keeps the revert it always had.
    ///
    /// <para><b>Measured 2026-09-28, run 3fe4f8.</b> The final review of step 1 said of the seven
    /// tests it wrote "implementation: pass ... 7/7 pass", and failed the step for how it had run its
    /// commands and what its report left out. The step was rejected, and the engine put the test file
    /// back: the only thing the review had found right was the thing that was thrown away.</para>
    ///
    /// <para><b>A list, not "the work stands", since 2026-10-01.</b> One flag for the whole step was the wrong shape:
    /// benchmark build-error added Median rightly and changed another file the request said to leave alone, and the
    /// review said the work stood in 12 of 12 answers, because Median was right - so the forbidden change would have been
    /// kept. Asked to name the files, it named Stats.cs alone, 12 of 12.</para>
    /// </summary>
    public IReadOnlyList<string> Keep { get; init; } = [];
    /// <summary>
    /// The cached share of <see cref="PromptTokens"/>, or null where nobody counted. This is the
    /// phase most likely to have one on a real machine: review is bound to a cloud model, and a
    /// re-ask re-sends the same prefix it just sent.
    /// </summary>
    public int? CachedPromptTokens { get; init; }
    public int? CacheCreationPromptTokens { get; init; }
}

/// <summary>Which review judged a step - named in its events ("PASS (Step review)").</summary>
public enum ReviewMode
{
    /// <summary>Does the step meet the semantic criteria its plan set - those and nothing else (Phase 1.4)?</summary>
    Criteria,

    /// <summary>One short verdict: did the step do what it is for, and is its report true (StepVerdictReview)?</summary>
    Step
}
