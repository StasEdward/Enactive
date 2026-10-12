namespace Enactive.Application.Projections;

using Enactive.Core.History;

/// <summary>
/// The lines of a run's row in the runs column - a finished run, an unfinished one - in the words the window has always
/// shown them. Moved out of the window's row view models when the service drew the same column: a second copy of "a
/// run that did not complete says how, else what it produced" is a second place for the two to stop agreeing.
/// </summary>
internal static class RunRows
{
    /// <summary>A finished run's second line: when, and what came of it.</summary>
    public static string Meta(RunSummary record) => $"{Ago(record.StartedAt)} · {Outcome(record)}";

    public static string Tooltip(RunSummary record)
        => $"{RunTitle.For(record)}\n{record.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {record.Status} · {RunDigest.Duration(record.FinishedAt - record.StartedAt)}";

    /// <summary>An unfinished run's second line: when it stopped, and how far it got.</summary>
    public static string Meta(RunCheckpoint checkpoint)
        => $"stopped {checkpoint.At.ToLocalTime():MMM d, HH:mm} · {checkpoint.Finished} of {checkpoint.Steps.Count} steps done";

    public static string Tooltip(RunCheckpoint checkpoint)
        => $"{checkpoint.Request}\n\nResume runs the {checkpoint.Remaining} step(s) that are left, "
           + "under the permissions this run started with. A step that was in progress when it "
           + "stopped is done again from its beginning.";

    /// <summary>The resume button is an icon beside the delete one, as on every other row - so what it does is said on it.</summary>
    public static string ResumeTip(RunCheckpoint checkpoint) => $"Resume: run the {checkpoint.Remaining} step(s) that are left";

    /// <summary>
    /// How long ago, the way a person would say it. Days are counted on the CALENDAR, not in
    /// 24-hour blocks: a run at 23:50 was "yesterday" by breakfast, not "9 h ago".
    /// </summary>
    public static string Ago(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var days = (DateTime.Today - local.Date).Days;

        if (days == 1)
            return "yesterday";
        if (days > 1 && days < 7)
            return $"{days} days ago";
        if (days >= 7)
            return local.ToString("MMM d");

        var span = DateTimeOffset.Now - at;
        if (span.TotalMinutes < 1)
            return "just now";
        if (span.TotalMinutes < 60)
            return $"{(int)span.TotalMinutes} min ago";
        return $"{(int)span.TotalHours} h ago";
    }

    /// <summary>
    /// What came of it, in two or three words. A run that did not complete says how - failed,
    /// blocked, waiting, not finished (RunStanding, in Core, where the words are tested); otherwise
    /// what it produced is more use than how long it took, and the duration is in the run's own
    /// header anyway.
    /// </summary>
    public static string Outcome(RunSummary record)
    {
        if (RunStanding.Word(record.Status) is { } word)
            return word;

        if (record.Artifacts.Count > 0)
            return $"{record.Artifacts.Count} artifact{(record.Artifacts.Count == 1 ? "" : "s")}";

        return RunDigest.Duration(record.FinishedAt - record.StartedAt);
    }
}
