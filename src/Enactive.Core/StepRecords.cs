namespace Enactive.Core.Tasks;

using System.Text.Json.Serialization;
using Enactive.Core.Events;

/// <summary>
/// Why a step ended as it did, as a stable code - kept apart from the words shown for it, so a report
/// can be read by code and the wording changed or translated without touching what was recorded.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutcomeCause>))]
public enum OutcomeCause
{
    /// <summary>Nothing held it back.</summary>
    None,

    /// <summary>The step itself did not finish: a limit, an unresolved call, a runaway reply, a full window.</summary>
    StepIncomplete,

    /// <summary>The step ran and failed.</summary>
    StepFailed,

    /// <summary>The step never ran: something it depended on did not succeed, or a limit stopped the run.</summary>
    NotReached,

    /// <summary>A step for each item whose items were given no steps (a limit nobody lifted, or no list).</summary>
    NotExpanded,

    /// <summary>The reviewer reached a verdict, and it was against the work.</summary>
    ReviewRejected,

    /// <summary>
    /// The reviewer's answer could not be used: it failed the review contract after correction, the
    /// review call failed, or its budget ran out. Says nothing about the work either way.
    /// </summary>
    ReviewUnprocessable,

    /// <summary>The reviewer answered, and its answer was that it could not tell.</summary>
    ReviewUndecided,

    /// <summary>A join: some of the steps for its items did not finish.</summary>
    ItemsUnfinished
}

/// <summary>What a step's accepted result is worth - its own field, not folded into the outcome.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResultStanding>))]
public enum ResultStanding
{
    /// <summary>The step handed no result on.</summary>
    NotProvided,

    /// <summary>The step finished and the review passed.</summary>
    Confirmed,

    /// <summary>The step finished and no review was asked for.</summary>
    Unreviewed,

    /// <summary>The step finished; the review could not be processed, or could not tell.</summary>
    Unconfirmed,

    /// <summary>The review found the result wrong.</summary>
    Rejected,

    /// <summary>Handed on by a step that then did not finish: findings so far, not a finished result.</summary>
    Provisional
}

/// <summary>
/// What the ENGINE recorded about a step: how it ended, why (as a code, and in words), and the last
/// result it accepted from it with what that result is worth. Kept for every step, whatever its
/// outcome - a result handed on before a step broke off is not lost, and is not promoted either.
/// </summary>
public sealed record StepRecord(
    StepOutcomeKind Outcome,
    OutcomeCause Cause,
    string? Reason,
    ResultStanding Standing,
    StepOutput? Result)
{
    /// <summary>What the accepted result is worth, from how the step ended and whether it was reviewed.</summary>
    public static ResultStanding StandingOf(StepOutcomeKind outcome, OutcomeCause cause, bool hasResult, bool reviewed)
        => !hasResult ? ResultStanding.NotProvided
            : cause == OutcomeCause.ReviewRejected ? ResultStanding.Rejected
            : outcome switch
            {
                StepOutcomeKind.Succeeded => reviewed ? ResultStanding.Confirmed : ResultStanding.Unreviewed,
                StepOutcomeKind.DoneUnverified => ResultStanding.Unconfirmed,
                StepOutcomeKind.ReviewRejected => ResultStanding.Rejected,
                _ => ResultStanding.Provisional
            };

    /// <summary>The cause an outcome implies when nothing more specific was recorded.</summary>
    public static OutcomeCause CauseOf(StepOutcomeKind outcome) => outcome switch
    {
        StepOutcomeKind.Succeeded => OutcomeCause.None,
        StepOutcomeKind.Failed => OutcomeCause.StepFailed,
        StepOutcomeKind.Skipped => OutcomeCause.NotReached,
        StepOutcomeKind.ReviewRejected => OutcomeCause.ReviewRejected,
        StepOutcomeKind.DoneUnverified => OutcomeCause.ReviewUnprocessable,
        _ => OutcomeCause.StepIncomplete
    };
}
