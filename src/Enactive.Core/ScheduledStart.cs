namespace Enactive.Core.Schedules;

using Enactive.Core.Storage;

/// <summary>
/// What a scheduled run takes BEFORE its occurrence is marked fired: the schedule (no second copy of it) and its
/// workspace (no other process in that folder).
///
/// <para><b>Both first, and together.</b> The occurrence is marked fired before the run is composed, so a machine
/// that loses power mid-run does not repeat it. Anything refused after that mark loses the occurrence: a schedule
/// whose folder was busy with the window's task skipped its day without a word, when all it had to do was wait for
/// the next tick. And a schedule claimed but refused its folder must give the claim back, or the next tick reads it
/// as still running.</para>
/// </summary>
public static class ScheduledStart
{
    /// <summary>Takes the schedule and its workspace, or returns null and why it is busy.</summary>
    public static IDisposable? TryBegin(RunMarkers markers, Guid scheduleId, string workspaceRoot, out string? busy)
    {
        // Null means somebody claimed it between the decision and here, which is not an error - it is the
        // overlap rule working.
        if (markers.Claim(scheduleId) is not { } claim)
        {
            busy = "claimed by another run just now";
            return null;
        }

        if (WorkspaceLease.TryTake(workspaceRoot, out var holder) is not { } lease)
        {
            claim.Dispose();
            busy = WorkspaceInUseException.Describe(workspaceRoot, holder)
                   + " It is still due, and runs on the next tick the folder is free.";
            return null;
        }

        busy = null;
        return new Both(claim, lease);
    }

    private sealed class Both(IDisposable claim, IDisposable lease) : IDisposable
    {
        public void Dispose()
        {
            try { lease.Dispose(); }
            finally { claim.Dispose(); }
        }
    }
}
