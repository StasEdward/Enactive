namespace Enactive.Core.History;

/// <summary>Where a run stands, as far as a list of runs needs to tell them apart at a glance.</summary>
public enum RunStandingKind
{
    /// <summary>Still going - and any word this build does not know, which is a phase of a run in progress.</summary>
    Running,

    /// <summary>Finished, with its work done.</summary>
    Done,

    /// <summary>Finished, and something went wrong.</summary>
    Failed,

    /// <summary>Stopped short of its work and can be taken up again: incomplete, blocked, waiting for a person.</summary>
    Open,

    /// <summary>Nothing is happening and nothing is owed: cancelled, or not started.</summary>
    Idle
}

/// <summary>
/// How a stored status reads in a list of runs: which pill it gets, and what the row says came of it.
///
/// <para>Kept here, away from the colours, so that it can be tested against the outcomes the engine records.
/// It was two switches in the desktop, each written before NeedsUser and Blocked existed: a run waiting for an
/// answer fell through to the pill of a run IN PROGRESS, and the row under it gave an artifact count or a
/// duration - what a completed run shows - for a run that was blocked, waiting or unfinished.</para>
/// </summary>
public static class RunStanding
{
    /// <summary>The standing of a phase or a stored status.</summary>
    public static RunStandingKind Of(string status) => status.ToLowerInvariant() switch
    {
        "completed" or "succeeded" or "ok" => RunStandingKind.Done,
        "failed" or "error" => RunStandingKind.Failed,
        // Never wrote a final status: the run stopped somewhere nobody watched.
        "incomplete" => RunStandingKind.Open,
        // Waiting for its cause to be put right, then resumed (Phase 7).
        "blocked" => RunStandingKind.Open,
        // Waiting for an answer, then carried on. Without its own line it fell to the last one and
        // was drawn as a run in progress, which is the one thing it is not: nothing moves until
        // somebody answers.
        "needsuser" => RunStandingKind.Open,
        // What older history calls a run that was cut off. It is over, and it too read as going.
        "interrupted" => RunStandingKind.Open,
        "cancelled" or "canceled" or "idle" or "" => RunStandingKind.Idle,
        _ => RunStandingKind.Running
    };

    /// <summary>
    /// What came of a run that did not complete, in a word or two - or null for one that did, or is still going,
    /// where what it produced says more.
    /// </summary>
    public static string? Word(string status) => status.ToLowerInvariant() switch
    {
        "failed" or "error" => "failed",
        "cancelled" or "canceled" => "cancelled",
        "blocked" => "blocked",
        "needsuser" => "waiting for you",
        "incomplete" or "interrupted" => "not finished",
        _ => null
    };
}
