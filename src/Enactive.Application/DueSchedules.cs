namespace Enactive.Application;

using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Core.Schedules;
using Enactive.Core.Templates;
using Enactive.Workspace;

/// <summary>A schedule whose time has come, claimed with its workspace and turned into the task it starts.</summary>
/// <param name="Hold">The schedule's claim and the workspace's lease (ScheduledStart), held until the run ends.</param>
internal sealed record DueRun(Schedule Schedule, ResolvedTaskSpec Spec, IDisposable Hold, string Why);

/// <summary>What one workspace's schedules came to at a tick.</summary>
/// <param name="Run">The one to run now, or null.</param>
/// <param name="Busy">Why the one due is not run now - claimed by another tick, or its folder in use - or null.</param>
/// <param name="Failed">Why the one due could not be turned into a task - reported to the inbox, its occurrence spent.</param>
internal sealed record DueTake(DueRun? Run, IReadOnlyList<ScheduleDecision> Decisions, string? Busy, string? Failed)
{
    /// <summary>How many more are due than the one taken: they run on the next tick.</summary>
    public int MoreDue => Math.Max(0, Decisions.Count(d => d.ShouldRun) - 1);
}

/// <summary>
/// Taking a workspace's due schedule, in the order it has to happen, for every host that runs schedules: the console's
/// tick (<c>--due</c>) and the service's scheduler (ADR 0002 §10). The two must not disagree about which schedule runs,
/// when its occurrence counts as spent, or what a schedule that cannot start leaves behind - which is what two copies
/// of these lines would do.
///
/// <list type="number">
/// <item>Decide (ScheduleTick): the most overdue one that should run.</item>
/// <item>Claim it and its workspace together (ScheduledStart) BEFORE marking anything: refused here, it stays due for
/// the next tick.</item>
/// <item>Turn it into a task (ScheduledSpec), and mark the occurrence fired - also when it cannot be turned into one,
/// so a broken schedule is reported once a day, not every tick. A machine that loses power mid-run does not repeat
/// the occurrence on the way back up; the run is in the history either way.</item>
/// <item>One that cannot start is said in the inbox: nobody is at the machine at 3am.</item>
/// </list>
/// </summary>
internal static class DueSchedules
{
    public static async Task<DueTake> TakeAsync(
        ScheduleStore schedules, RunMarkers markers, string workspaceRoot, DateTimeOffset now, CancellationToken ct)
    {
        var templates = new TemplateStore(workspaceRoot);
        var decisions = ScheduleTick.Decide(schedules.For(workspaceRoot), now, markers.IsRunning, templates.Find);
        if (decisions.Where(d => d.ShouldRun).OrderBy(d => d.Occurrence).FirstOrDefault() is not { } due)
            return new DueTake(null, decisions, null, null);

        if (ScheduledStart.TryBegin(markers, due.Schedule.Id, workspaceRoot, out var busy) is not { } hold)
            return new DueTake(null, decisions, busy, null);

        // Adopt: a scheduled run is a run, and the folder is taken up as a workspace here as anywhere.
        var workspace = WorkspaceInfo.Adopt(workspaceRoot);
        var resolved = ScheduledSpec.For(due.Schedule, workspace, templates.Find);
        schedules.Save(due.Schedule with { LastFiredAt = due.Occurrence });

        if (resolved.Spec is not { } spec)
        {
            hold.Dispose();
            await InboxStoreFactory.Create(workspace).AppendAsync(
                ScheduledOutcome.CouldNotStart(workspace.Id, due.Schedule.Id, due.Schedule.Name, resolved.Why, DateTimeOffset.UtcNow), ct);
            return new DueTake(null, decisions, null, resolved.Why);
        }

        return new DueTake(new DueRun(due.Schedule, spec, hold, due.Why), decisions, null, null);
    }
}
