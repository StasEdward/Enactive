namespace Enactive.Core.History;

/// <summary>Which runs a person is looking at. The order is the order they are offered in.</summary>
public enum RunFilter
{
    /// <summary>Everything this workspace has recorded.</summary>
    All,

    /// <summary>The ones that did not finish their work: failed, incomplete, cancelled, interrupted.</summary>
    Unfinished,

    /// <summary>The ones that did.</summary>
    Completed,

    /// <summary>Started more than seven days ago, whatever happened to them.</summary>
    OlderThanAWeek
}

/// <summary>
/// Choosing runs to look at, and runs to forget.
///
/// <para><b>Why a library and not the window.</b> A predicate that decides what a button called
/// "Delete the 23 shown" will destroy is not a display detail. It is the definition of what gets
/// destroyed, and App.Ui is a WinExe that no test project references — a rule kept there is a rule
/// nothing can check. That has bitten this repository three times already.</para>
///
/// <para><b>Why any of this exists.</b> Nothing ever removed a run, so the list only grew: 37 of
/// them in a workspace used for a few days. The gateway solved the same problem for itself with a
/// retention window trimmed hourly, and said so in the panel. The desktop kept everything for
/// ever.</para>
/// </summary>
public static class RunHousekeeping
{
    /// <summary>
    /// A run that stopped without doing its work.
    ///
    /// <para>Matched on the status STRING because that is what a stored run carries, and history
    /// written by an older build has to keep meaning what it meant. Unknown statuses are treated as
    /// finished — the safe direction, since this list is the one a bulk delete acts on and a status
    /// nobody recognises must not be swept up by a filter named "unfinished".</para>
    /// </summary>
    private static readonly HashSet<string> DidNotFinish = new(StringComparer.OrdinalIgnoreCase)
    {
        "Failed", "Incomplete", "Cancelled", "Interrupted"
    };

    public static readonly TimeSpan AWeek = TimeSpan.FromDays(7);

    public static bool Matches(RunSummary run, RunFilter filter, DateTimeOffset now) => filter switch
    {
        RunFilter.All => true,
        RunFilter.Unfinished => DidNotFinish.Contains(run.Status),
        RunFilter.Completed => !DidNotFinish.Contains(run.Status),
        RunFilter.OlderThanAWeek => now - run.StartedAt > AWeek,
        _ => true
    };

    public static IReadOnlyList<RunSummary> Where(
        IReadOnlyList<RunSummary> runs, RunFilter filter, DateTimeOffset now)
        => runs.Where(r => Matches(r, filter, now)).ToArray();

    /// <summary>
    /// One line of the runs list: a run, and whether it leads a group of attempts at the same task.
    /// </summary>
    /// <param name="Attempts">
    /// How many attempts this task has, on the LEAD row. Zero on the rows underneath it, which are
    /// the older attempts of a group already counted.
    /// </param>
    public sealed record RunRow(RunSummary Run, int Attempts, bool IsLead);

    /// <summary>
    /// The list as rows, with repeat attempts at one task folded into the newest of them.
    ///
    /// <para><b>Why.</b> A task tried a dozen times took a dozen rows, and they read as a dozen
    /// pieces of work. A workspace used for a few days showed twelve consecutive lines of "WSFC
    /// Workgroup Cluster Set…", which is one thing that took twelve goes. No filter fixes that -
    /// every one of those rows is a real run, and each is legitimately in every filter.</para>
    ///
    /// <para>Grouping is applied AFTER filtering, over whatever the filter left. That matters for
    /// the case the filter is for: a task that failed twelve times and succeeded on the thirteenth,
    /// filtered to Unfinished, shows the twelve - which are the ones somebody wants to be rid of -
    /// and not the success. Grouping by task first and then filtering would have to decide what a
    /// half-matching group means, and there is no honest answer to that.</para>
    ///
    /// <para>The lead row is the NEWEST attempt, because what somebody looks for is how it ended
    /// up, and groups are ordered by their newest attempt so a task worked on today does not sit
    /// below one abandoned last week for having started earlier.</para>
    /// </summary>
    /// <param name="expanded">Task ids the person has opened. Everything else shows its lead only.</param>
    public static IReadOnlyList<RunRow> Rows(
        IReadOnlyList<RunSummary> runs, IReadOnlySet<Guid>? expanded = null)
    {
        var rows = new List<RunRow>();

        var groups = runs
            .GroupBy(r => r.TaskId)
            .Select(g => g.OrderByDescending(r => r.StartedAt).ToArray())
            .OrderByDescending(g => g[0].StartedAt);

        foreach (var attempts in groups)
        {
            rows.Add(new RunRow(attempts[0], attempts.Length, IsLead: true));

            // A single attempt is not a group and gets no expander: "1 attempt" on every ordinary
            // run is noise on the common case to serve the rare one.
            if (attempts.Length == 1 || expanded is null || !expanded.Contains(attempts[0].TaskId))
                continue;

            for (var i = 1; i < attempts.Length; i++)
                rows.Add(new RunRow(attempts[i], 0, IsLead: false));
        }

        return rows;
    }

    /// <summary>
    /// The runs a retention setting would remove: everything past the newest <paramref name="keep"/>.
    ///
    /// <para>A COUNT and not an age, because what makes this list unusable is how many rows are in
    /// it, and a week of heavy use puts more in it than a month of light use. An age-based rule
    /// would leave the busy workspace — the one with the problem — untouched.</para>
    ///
    /// <para>Newest by when a run STARTED. A run that was interrupted has no meaningful finish, and
    /// ordering by a finish time that is default(DateTimeOffset) would put the interrupted runs at
    /// the far end and delete them first — which is precisely backwards, since those are the ones
    /// somebody may still want to resume.</para>
    ///
    /// <para>Zero or less keeps everything. "Keep none" is not a setting anybody means, and reading
    /// an empty or mistyped value as "delete the lot" is the kind of default that ends a session
    /// badly.</para>
    /// </summary>
    public static IReadOnlyList<RunSummary> BeyondTheNewest(IReadOnlyList<RunSummary> runs, int keep)
        => keep <= 0
            ? []
            : runs.OrderByDescending(r => r.StartedAt).Skip(keep).ToArray();
}
