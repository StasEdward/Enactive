namespace Enactive.Application.Projections;

using System.Globalization;
using Enactive.Core.History;

/// <summary>
/// The lines of a run's row in the runs column - a finished run, an unfinished one - in the words the window has always
/// shown them. Moved out of the window's row view models when the service drew the same column: a second copy of "a
/// run that did not complete says how, else what it produced" is a second place for the two to stop agreeing.
/// </summary>
internal static class RunRows
{
    /// <summary>
    /// The filter's names, in the order of <see cref="RunFilter"/> - indexed by it, by the window's combo box and the
    /// Runs screen's select alike.
    /// </summary>
    public static readonly IReadOnlyList<string> FilterNames = ["All runs", "Unfinished", "Completed", "Older than a week"];

    /// <summary>
    /// Named with the COUNT, so the button says how much it destroys before it is pressed rather than in a dialog after it.
    /// </summary>
    public static string DeleteShown(int count) => $"Delete these {count}";

    /// <summary>
    /// The RUNS line: "No runs recorded in this workspace.", "3 runs", or both numbers under a filter - "3 of 37 runs":
    /// "3 runs" over a list that holds 37 is a true sentence that reads as the whole truth. Every host's column says it so.
    /// </summary>
    public static string Status(int shown, int total)
        => total == 0
            ? "No runs recorded in this workspace."
            : shown == total
                ? string.Create(CultureInfo.InvariantCulture, $"{shown} run{(shown == 1 ? "" : "s")}")
                : string.Create(CultureInfo.InvariantCulture, $"{shown} of {total} runs");

    /// <summary>A finished run's second line: when, and what came of it.</summary>
    public static string Meta(RunSummary record) => $"{Ago(record.StartedAt)} · {Outcome(record)}";

    public static string Tooltip(RunSummary record)
        => $"{RunTitle.For(record)}\n{Times.Minute(record.StartedAt)} · {record.Status} · {RunDigest.Duration(record.FinishedAt - record.StartedAt)}";

    /// <summary>An unfinished run's second line: when it stopped, and how far it got.</summary>
    public static string Meta(RunCheckpoint checkpoint)
        => $"stopped {Times.Day(checkpoint.At)}, {Times.Clock(checkpoint.At)} · {checkpoint.Finished} of {checkpoint.Steps.Count} steps done";

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
            return Times.Day(at);

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
