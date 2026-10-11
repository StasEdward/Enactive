namespace Enactive.Server.Runs;

/// <summary>One line on where a run stands, the same on the list and on its own page.</summary>
internal static class RunState
{
    public static string Title(ServerRun run)
        => run.Session.Read((feed, _, _) => feed.Title) is { Length: > 0 } title ? title : run.Prompt;

    public static string Of(ServerRun run)
    {
        if (run.Refused is { } refused)
            return "Not run: " + refused;
        if (run.Waiting is { } waiting)
            return "Waiting: " + waiting;
        if (run.Ended.IsCompleted)
            return run.CancelledBy is { } who ? $"Cancelled by {who}"
                : run.Session.Failure is { } failure ? "Stopped: " + failure
                : run.Session.Read((feed, _, _) => feed.Phase) ?? "Ended";
        if (run.Questions.Pending is not null)
            return "Waiting for an answer";
        return run.Session.Read((feed, _, _) => feed.Phase) ?? "Running";
    }
}
