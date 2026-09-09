namespace Enactive.Core.History;

/// <summary>
/// A run that is happening RIGHT NOW, as the run column has to show it.
///
/// <para><see cref="RunId"/> is <see cref="Guid.Empty"/> until the engine has emitted its first
/// event: the id is the orchestrator's to mint, and the window starts drawing the row before it
/// knows one. An unidentified row is still a true row - something is running - it just cannot be
/// matched against the history yet. See <see cref="RunColumn.Live"/>.</para>
/// </summary>
/// <param name="RowId">
/// The window's own handle on this row, minted when the run is started. Needed because RunId is not
/// known yet at that moment and a headless run never tells the window one at all.
/// </param>
/// <param name="Headless">
/// Started with "Run in background": there is no live feed to go back to, so the row must not offer
/// one. Its result arrives in the Inbox.
/// </param>
/// <param name="Waiting">
/// The run has asked a question and is stopped until it is answered. Its own state, and not a
/// detail of the card that asks: the card lives in the middle column, and the middle column can now
/// be showing another workspace entirely - so the row is the only thing that can say a run is
/// blocked on you rather than working.
/// </param>
public sealed record LiveRun(
    Guid RowId,
    Guid RunId,
    string Title,
    bool Headless,
    int StepsDone,
    int StepsTotal,
    DateTimeOffset StartedAt,
    bool Waiting = false);

/// <summary>
/// What the run column shows, when some of what it is about has not finished happening.
///
/// <para><b>The defect.</b> The column answered "what has happened in this workspace" and said
/// nothing at all about what was happening in it. A run got a row only when it ENDED - the store
/// has one call, <c>SaveAsync(RunRecord)</c>, and a record is a finished thing - so between Run and
/// the end there was no trace of the run anywhere except the middle column. Open a past run to
/// compare something and the only way back was an 11px "← Back" in the corner of the header, with
/// nothing anywhere saying there was something to go back TO. People started a run, looked at
/// something else, and could not find the run they had just started.</para>
///
/// <para><b>Why a library and not the window.</b> App.Ui is a WinExe no test project references.
/// The rule below is small but it is the kind that goes wrong silently and looks fine in a
/// screenshot.</para>
/// </summary>
public static class RunColumn
{
    /// <summary>
    /// The live rows to show, newest first, minus any run the history already knows about.
    ///
    /// <para><b>The rule: a run is never on screen twice.</b> The window learns that a run ended by
    /// two independent paths - the call that was awaiting it returns, and the run list is re-read
    /// from the store - and those two land in whatever order they land in. Without this, the
    /// overlap shows the same run as "running" above and as a finished card below, which is a
    /// column contradicting itself.</para>
    ///
    /// <para>A row whose <see cref="LiveRun.RunId"/> is empty is never dropped here: nothing is
    /// known about it to match, and dropping rows we cannot identify would hide real work. Those
    /// rows go when whoever started them says so.</para>
    /// </summary>
    public static IReadOnlyList<LiveRun> Live(
        IEnumerable<LiveRun> running, IReadOnlyList<RunSummary> history)
    {
        var finished = history.Select(h => h.RunId).ToHashSet();

        return running
            .Where(r => r.RunId == Guid.Empty || !finished.Contains(r.RunId))
            .OrderByDescending(r => r.StartedAt)
            .ToArray();
    }

    /// <summary>
    /// The row's second line: what this run is doing, in the words the rest of the app uses.
    ///
    /// <para>No elapsed time on purpose. It would have to tick, which means rebuilding these rows
    /// every second under somebody's pointer, and the run's own elapsed clock is already in the
    /// middle column - which is one click away and is where you go to watch it.</para>
    /// </summary>
    public static string Meta(LiveRun run)
    {
        if (run.Headless)
            return "in background · the result goes to the Inbox";

        // First, because it is the only state that is about the READER. Everything else here says
        // what the run is doing; this says the run is doing nothing and is waiting on them - and
        // the card it is waiting on may be behind another workspace.
        if (run.Waiting)
            return "waiting for your answer";

        // No plan yet. Saying "step 0 of 0" would be a progress report about work that has not been
        // decided on; planning is a real phase and is what is actually happening.
        if (run.StepsTotal <= 0)
            return "running · planning";

        // Finishing rather than "step 6 of 5": every step is done and the run is in its closing
        // work - the review, the summary - which is not a step and has no number.
        return run.StepsDone >= run.StepsTotal
            ? "running · finishing"
            : $"running · step {run.StepsDone + 1} of {run.StepsTotal}";
    }
}
