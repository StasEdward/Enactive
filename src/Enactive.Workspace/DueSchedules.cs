namespace Enactive.Workspace;

using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Core.Schedules;
using Enactive.Core.Templates;

/// <summary>A schedule taken to run now: claimed, marked as fired, and resolved to the task it runs. Dispose to release the claim.</summary>
public sealed record TakenSchedule(Schedule Schedule, WorkspaceInfo Workspace, ResolvedTaskSpec Spec, IDisposable Claim) : IDisposable
{
    public void Dispose() => Claim.Dispose();
}

/// <summary>
/// One tick of the schedules across every workspace: the most overdue one is taken, or none.
///
/// <para><b>Why.</b> The schedules ran only when the console was woken with --due-all by something outside the
/// app - a Windows task nobody had registered (2026-10-11): a schedule made and saved in the app's own window
/// never ran, while the app was open the whole time. The app now ticks the schedules itself, with this; the
/// console's --due-all keeps its own copy of the same steps for now. Both take the same claim (RunMarkers), so
/// the two on one machine do not start the same schedule twice.</para>
/// </summary>
public sealed class DueSchedules(ScheduleStore schedules, RunMarkers markers, string? globalTemplates = null)
{
    public static DueSchedules Default { get; } = new(ScheduleStore.Default, RunMarkers.Default);

    /// <summary>
    /// The schedule to run now, taken - or null when nothing is due, when what is due is claimed elsewhere, or
    /// when it could not be started, in which case its Inbox says why.
    /// </summary>
    public async Task<TakenSchedule?> TakeAsync(DateTimeOffset now, CancellationToken ct)
    {
        TemplateStore Templates(string root) => new(root, globalTemplates);

        var across = schedules.Workspaces()
            .SelectMany(root => ScheduleTick.Decide(schedules.For(root), now, markers.IsRunning, Templates(root).Find))
            .ToArray();
        if (ScheduleTick.FirstDue(across) is not { } due)
            return null;

        // Claimed before anything else, so a tick that overlaps this one - the console's, another window's - sees
        // it as busy. Null is that rule working, not an error.
        if (markers.Claim(due.Schedule.Id) is not { } claim)
            return null;

        try
        {
            var root = Path.GetFullPath(due.Schedule.WorkspaceRoot);
            if (!Directory.Exists(root))
            {
                // A folder that is gone is not made again by a schedule: running would create an empty workspace
                // where the project was. Marked as fired, so it is not tried again every minute.
                schedules.Save(due.Schedule with { LastFiredAt = due.Occurrence });
                claim.Dispose();
                return null;
            }

            var workspace = WorkspaceInfo.Adopt(root);
            var resolved = ScheduledSpec.For(due.Schedule, workspace, Templates(root).Find);

            // Marked as fired NOW rather than when the run ends: an occurrence is considered exactly once, and a
            // machine that loses power mid-run must not repeat it on the way back up.
            schedules.Save(due.Schedule with { LastFiredAt = due.Occurrence });

            if (resolved.Spec is not { } spec)
            {
                // Reported once and not retried every tick - the occurrence is already marked as fired.
                await InboxStoreFactory.Create(workspace).AppendAsync(
                    ScheduledOutcome.CouldNotStart(workspace.Id, due.Schedule.Id, due.Schedule.Name, resolved.Why, DateTimeOffset.UtcNow),
                    ct);
                claim.Dispose();
                return null;
            }

            return new TakenSchedule(due.Schedule, workspace, spec, claim);
        }
        catch
        {
            claim.Dispose();
            throw;
        }
    }
}
