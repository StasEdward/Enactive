namespace Enactive.Agents;

using Enactive.Core.Events;
using Enactive.Core.Tasks;

/// <summary>
/// What a reviewer said of a step - one of five things, each with what goes with it.
///
/// <para><b>Why one value.</b> The verdict used to be five overlapping fields - Pass, IncompleteReason,
/// VerdictUnavailable, Undecided, BudgetExhausted, and a second BudgetExhausted beside the result - and what became of
/// the step was decided by reading them in the right order. One combination (a reason without an unavailable verdict,
/// "a prohibition the work broke") had no producer left after the old reviews went (ed6fe7c), and was still handled.</para>
/// </summary>
public abstract record ReviewVerdict(string Notes)
{
    /// <summary>The step is done, on what the reviewer was shown.</summary>
    public sealed record Pass(string Notes) : ReviewVerdict(Notes);

    /// <summary>
    /// The step is not done, and why - with what to do about it.
    /// </summary>
    /// <param name="Keep">
    /// The files of the step the reviewer found right as they are, on a fail that is somewhere else: the report, the
    /// process, another file. A step rejected on such a review keeps these and has the rest put back.
    ///
    /// <para><b>Measured 2026-09-28, run 3fe4f8.</b> The final review of step 1 said of the seven tests it wrote
    /// "implementation: pass ... 7/7 pass", and failed the step for how it had run its commands and what its report left
    /// out. The step was rejected, and the engine put the test file back: the only thing the review had found right was
    /// the thing that was thrown away. A list, not "the work stands", since 2026-10-01: one flag for the whole step kept
    /// a forbidden change beside a right one (benchmark build-error, 12 of 12).</para>
    /// </param>
    public sealed record Fail(string Notes, string RepairAdvice, IReadOnlyList<string> Keep) : ReviewVerdict(Notes);

    /// <summary>The reviewer answered, and its answer was that it could not tell.</summary>
    public sealed record Undecided(string Notes) : ReviewVerdict(Notes);

    /// <summary>No usable verdict came back: the provider failed, or no answer could be used even after correction.</summary>
    public sealed record Unavailable(string Notes) : ReviewVerdict(Notes);

    /// <summary>The run's budget was spent before the reviewer could be asked (again).</summary>
    public sealed record OutOfBudget(string Notes) : ReviewVerdict(Notes);

    /// <summary>
    /// What a step comes to when this verdict leaves it without one - or null for a pass or a fail, which the attempt
    /// loop acts on itself (the step stands, or is tried again and then rejected).
    ///
    /// <para>A review ran at all only because the worker finished, so a verdict that is MISSING leaves the work done
    /// and only unconfirmed: DoneUnverified, and its dependents run. The cause says why it is missing - a reviewer
    /// that said it could not tell is not a review that failed to be processed.</para>
    /// </summary>
    public (StepOutcomeKind Kind, OutcomeCause Cause)? Missing => this switch
    {
        Undecided => (StepOutcomeKind.DoneUnverified, OutcomeCause.ReviewUndecided),
        Unavailable or OutOfBudget => (StepOutcomeKind.DoneUnverified, OutcomeCause.ReviewUnprocessable),
        _ => null
    };
}

/// <summary>
/// A reviewer's verdict for a step, plus what asking cost.
///
/// <para>The review call sits outside the tool loop, so its tokens were never counted; on a run that reviews content,
/// the reviewer reads whole documents on the most expensive model bound, which made the uncounted share the LARGEST
/// part of some runs. The counts cover the re-ask too, when there was one - two calls were made, and two calls were
/// paid for.</para>
/// </summary>
public sealed record ReviewResult(ReviewVerdict Verdict, int PromptTokens = 0, int CompletionTokens = 0)
{
    /// <summary>
    /// The cached share of <see cref="PromptTokens"/>, or null where nobody counted. This is the phase most likely to
    /// have one on a real machine: review is bound to a cloud model, and a re-ask re-sends the same prefix it just sent.
    /// </summary>
    public int? CachedPromptTokens { get; init; }
    public int? CacheCreationPromptTokens { get; init; }

    /// <summary>A reviewer's verdict with what its round cost.</summary>
    internal static ReviewResult Of<T>(ReviewVerdict verdict, AnswerRound<T> round)
        => new(verdict, round.PromptTokens, round.CompletionTokens)
        { CachedPromptTokens = round.CachedPromptTokens, CacheCreationPromptTokens = round.CacheCreationPromptTokens };
}

/// <summary>Which review judged a step - named in its events ("PASS (Step review)").</summary>
public enum ReviewMode
{
    /// <summary>Does the step meet the semantic criteria its plan set - those and nothing else (Phase 1.4)?</summary>
    Criteria,

    /// <summary>One short verdict: did the step do what it is for, and is its report true (StepVerdictReview)?</summary>
    Step
}
