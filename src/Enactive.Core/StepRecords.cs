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
    ItemsUnfinished,

    // Blocked (Phase 7), by what. The first three the ENGINE found; the last the step itself said, and it is advisory.

    /// <summary>A call the step needed was refused - by a person, or by this run's permission policy - and nothing replaced it.</summary>
    BlockedPermission,

    /// <summary>What the step needed to work from is not there: a result a step it depends on did not hand on, or every thing it looked for.</summary>
    BlockedInput,

    /// <summary>A step it depends on is blocked.</summary>
    BlockedDependency,

    /// <summary>The step reported that it cannot go on, and why (report_blocked). Its word, recorded as its word.</summary>
    BlockedReported,

    /// <summary>
    /// The reviewer found the step had done what can be done within the request, and the rest of its purpose not reachable
    /// here without going beyond it. Not tried again: a retry can only go beyond the request. Last, so no stored value moves.
    /// </summary>
    ReviewUnreachable,

    /// <summary>
    /// The items of a list the step consumes were covered only by being mentioned - named in a call that succeeded - and no
    /// review approved the work: done, not confirmed. One call naming every path covers every item. Last, so no stored
    /// value moves.
    /// </summary>
    CoverageMentionedOnly,

    /// <summary>
    /// The engine's coverage check at the step's gate found items of a list it consumes not covered, on every attempt
    /// its budget allowed (StepCoverageGate): rejected with no reviewer asked. Kept apart from ReviewRejected so nothing
    /// says a review rejected a step no reviewer saw. Last, so no stored value moves.
    /// </summary>
    CoverageGateRejected,

    /// <summary>
    /// A step that filters left items of a list it consumes out, each with a reason, and no review approved it: done, not
    /// confirmed. A reason is the step's word; whether the request permits the omission is a reviewer's to judge (Plan
    /// Phase 1.2). Last, so no stored value moves.
    /// </summary>
    CoverageExclusionsUnapproved,

    /// <summary>
    /// A join: every step for its items finished, and some items are still not covered as the plan requires - a result
    /// handed on without the evidence it owes (Plan Phase 1.3). Done, not confirmed: never rejected wholesale, so what the
    /// items handed on still goes on. Items whose steps did not finish are ItemsUnfinished. Last, so no stored value moves.
    /// </summary>
    ItemsNotCovered,

    /// <summary>
    /// A blocking workspace check (Plan Phase 3.2) owed for what the step changed failed at the step's gate, on every
    /// attempt the gate's budget allowed: rejected with no reviewer asked, as the coverage check rejects - and told apart
    /// from it, so nothing says a coverage count or a review rejected a step a declared check did. Last, so no stored
    /// value moves.
    /// </summary>
    CheckGateRejected,

    /// <summary>
    /// A blocking workspace check owed for what the step changed did not run - the policy would have had to ask, it timed
    /// out, it ran no tests: done, not confirmed. A check that did not run has verified nothing, and a blocking one is what
    /// a person said this kind of change is confirmed by. Last, so no stored value moves.
    /// </summary>
    CheckNotRun,

    /// <summary>
    /// A blocking workspace check failed after the step and had failed on the workspace as the step found it too, and what
    /// it printed cannot be compared to tell whether the step made it worse (Agents.StepStartCheck): done, not confirmed.
    /// Not sent back for a failure that was there before it began - and not confirmed by a check that said nothing about
    /// it. Last, so no stored value moves.
    /// </summary>
    CheckFailedBefore,

    /// <summary>
    /// Items of a list the step consumes were to be shown by a change to each file, and the step changed other files
    /// instead - tests written for a source file - and no review approved the work: done, not confirmed. Whether that is
    /// the work on the item is a reviewer's to judge (StepCoverageGate.Outcome.WrittenElsewhere). Last, so no stored
    /// value moves.
    /// </summary>
    CoverageWrittenElsewhere
}

/// <summary>
/// One time a step was blocked (Phase 7): when, by what, and why. Kept across a resume, so the attempts a step
/// has had are not lost when the run carries on - and the step, done again, is told what stopped it before.
/// </summary>
public sealed record StepBlock(DateTimeOffset At, OutcomeCause Cause, string Reason)
{
    /// <summary>Found by the engine, rather than said by the step.</summary>
    [JsonIgnore]
    public bool ByEngine => Cause != OutcomeCause.BlockedReported;
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
