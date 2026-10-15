namespace Enactive.Server.Runs;

using Enactive.Core.History;

/// <summary>One line on where a run stands, the same on the list and on its own page.</summary>
internal static class RunState
{
    public static string Title(ServerRun run)
        => run.Session.Read((feed, _, _, _) => feed.Title) is { Length: > 0 } title ? title : run.Prompt;

    public static string Of(ServerRun run)
    {
        if (run.Refused is { } refused)
            return "Not run: " + refused;
        if (run.Waiting is { } waiting)
            return "Waiting: " + waiting;
        if (run.Ended.IsCompleted)
            return run.CancelledBy is { } who ? $"Cancelled by {who}"
                : run.Session.Failure is { } failure ? "Stopped: " + failure
                : run.Session.Read((feed, _, _, _) => feed.Phase) ?? "Ended";
        if (run.Questions.Pending is not null)
            return "Waiting for an answer";
        return run.Session.Read((feed, _, _, _) => feed.Phase) ?? "Running";
    }

    /// <summary>
    /// The word on the live run's pill: a phase, as the window's (the feed's - Understanding, Planning, Executing, how it
    /// ended - or Cancelled, Error) and the service's own (Waiting in the line, Waiting for an answer, Not started). Why it
    /// is so, when there is more to say than the word, is <see cref="Why"/>'s - a pill holds a word, not a reason.
    /// </summary>
    /// <param name="feedPhase">The feed's phase, read with the rest of the page (RunFeed.Phase).</param>
    public static string Phase(ServerRun run, string? feedPhase)
    {
        if (run.Refused is not null)
            return "Not started";
        if (run.Waiting is not null)
            return "Waiting";
        if (run.Ended.IsCompleted)
            return run.CancelledBy is not null ? "Cancelled" : run.Session.Failure is not null ? "Error" : feedPhase ?? "Ended";
        if (run.Questions.Pending is not null)
            return "Waiting for an answer";
        return feedPhase ?? "Running";
    }

    /// <summary>
    /// What the pill's colour says (Looks.Phase): a run going, waiting on a person, done, failed or idle. A question up is
    /// a person's to answer - amber, as the window's decision card - and a run waiting its turn in the line is not going.
    /// </summary>
    public static RunStandingKind Standing(ServerRun run, string? feedPhase)
    {
        if (run.Refused is not null)
            return RunStandingKind.Failed;
        if (run.Waiting is not null)
            return RunStandingKind.Idle;
        if (run.Ended.IsCompleted)
            return run.CancelledBy is not null ? RunStandingKind.Idle
                : run.Session.Failure is not null ? RunStandingKind.Failed
                : feedPhase is null ? RunStandingKind.Idle
                : RunStanding.Of(feedPhase);
        if (run.Questions.Pending is not null)
            return RunStandingKind.Open;
        return RunStanding.Of(feedPhase ?? "Running");
    }

    /// <summary>
    /// Why the run stands where it does, when the service knows more than the run's own last action: why it waits in the
    /// line, why it never started, who cancelled it, what stopped it. Null when the run's current action says it all.
    /// </summary>
    public static string? Why(ServerRun run)
    {
        if (run.Refused is { } refused)
            return "Not run: " + refused;
        if (run.Waiting is { } waiting)
            return "Waiting: " + waiting;
        if (!run.Ended.IsCompleted)
            return null;
        return run.CancelledBy is { } who ? $"Cancelled by {who}"
            : run.Session.Failure is { } failure ? "Stopped: " + failure
            : null;
    }
}
