namespace Enactive.Application;

using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Core.Schedules;
using Enactive.Core.Templates;
using Enactive.Workspace;

/// <summary>A schedule whose time has come, claimed with its workspace and turned into the task it starts.</summary>
/// <param name="Hold">The schedule's claim and the workspace's lease (ScheduledStart), held until the run ends.</param>
internal sealed record DueRun(Schedule Schedule, WorkspaceInfo Workspace, ResolvedTaskSpec Spec, IDisposable Hold, string Why)
    : IDisposable
{
    /// <summary>Lets the schedule and its folder go: the run has ended, or will not start.</summary>
    public void Dispose() => Hold.Dispose();
}

/// <summary>What the schedules came to at a tick.</summary>
/// <param name="Run">The one to run now, or null.</param>
/// <param name="Busy">Why the one due is not run now - claimed by another tick, or its folder in use - or null.</param>
/// <param name="Failed">Why the one due could not be turned into a task - its occurrence spent - or null.</param>
internal sealed record DueTake(DueRun? Run, IReadOnlyList<ScheduleDecision> Decisions, string? Busy, string? Failed)
{
    /// <summary>How many more are due than the one taken: they run on the next tick.</summary>
    public int MoreDue => Math.Max(0, Decisions.Count(d => d.ShouldRun) - 1);
}

/// <summary>
/// Taking a due schedule, in the order it has to happen, for every host that runs schedules: the window while it is
/// open, the console's tick (<c>--due</c>, <c>--due-all</c>) and the service's scheduler (ADR 0002 §10). They must not
/// disagree about which schedule runs, when its occurrence counts as spent, or what a schedule that cannot start leaves
/// behind - and they did: the window's tick and the service's were two copies of these steps (merged 2026-10-12), the
/// window's without the workspace lease, the service's without the check for a folder that is gone.
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
    /// <summary>Every workspace's schedules decided at once - what the window and <c>--due-all</c> take the most overdue of.</summary>
    public static IReadOnlyList<ScheduleDecision> DecideAcross(
        ScheduleStore schedules, RunMarkers markers, DateTimeOffset now, string? globalTemplates = null)
        => schedules.Workspaces()
            .SelectMany(root => ScheduleTick.Decide(schedules.For(root), now, markers.IsRunning, new TemplateStore(root, globalTemplates).Find))
            .ToList();

    /// <summary>
    /// The most overdue schedule anywhere, taken in its own workspace. One a tick: the window runs what it takes in the
    /// background, and a minute later asks again.
    /// </summary>
    public static async Task<DueTake> TakeFirstAsync(
        ScheduleStore schedules, RunMarkers markers, DateTimeOffset now, CancellationToken ct, string? globalTemplates = null)
    {
        var across = DecideAcross(schedules, markers, now, globalTemplates);
        if (ScheduleTick.FirstDue(across) is not { } first)
            return new DueTake(null, across, null, null);
        // Re-decided within its folder, which lands on the same schedule: one decision point for taking, not two.
        var take = await TakeAsync(schedules, markers, first.Schedule.WorkspaceRoot, now, ct, globalTemplates);
        return take with { Decisions = across };
    }

    /// <summary>One workspace's most overdue schedule, taken - the service's tick per folder, and <c>--due</c>.</summary>
    public static async Task<DueTake> TakeAsync(
        ScheduleStore schedules, RunMarkers markers, string workspaceRoot, DateTimeOffset now, CancellationToken ct,
        string? globalTemplates = null)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var templates = new TemplateStore(root, globalTemplates);
        var decisions = ScheduleTick.Decide(schedules.For(root), now, markers.IsRunning, templates.Find);
        if (ScheduleTick.FirstDue(decisions) is not { } due)
            return new DueTake(null, decisions, null, null);

        if (!Directory.Exists(root))
        {
            // A folder that is gone is not made again by a schedule: taking its lease, or adopting it, would create an
            // empty workspace where the project was. Spent under the claim, so it is not tried again every minute;
            // there is no inbox to tell - it lived in that folder.
            if (markers.Claim(due.Schedule.Id) is not { } claim)
                return new DueTake(null, decisions, "claimed by another run just now", null);
            using (claim)
                schedules.Save(due.Schedule with { LastFiredAt = due.Occurrence });
            return new DueTake(null, decisions, null, $"The folder {root} is not there any more; nothing was run.");
        }

        if (ScheduledStart.TryBegin(markers, due.Schedule.Id, root, out var busy) is not { } hold)
            return new DueTake(null, decisions, busy, null);

        try
        {
            // Adopt: a scheduled run is a run, and the folder is taken up as a workspace here as anywhere.
            var workspace = WorkspaceInfo.Adopt(root);
            var resolved = ScheduledSpec.For(due.Schedule, workspace, templates.Find);
            schedules.Save(due.Schedule with { LastFiredAt = due.Occurrence });

            if (resolved.Spec is not { } spec)
            {
                await InboxStoreFactory.Create(workspace).AppendAsync(
                    ScheduledOutcome.CouldNotStart(workspace.Id, due.Schedule.Id, due.Schedule.Name, resolved.Why, DateTimeOffset.UtcNow), ct);
                hold.Dispose();
                return new DueTake(null, decisions, null, resolved.Why);
            }

            return new DueTake(new DueRun(due.Schedule, workspace, spec, hold, due.Why), decisions, null, null);
        }
        catch
        {
            // Whatever went wrong between the claim and the run, the schedule and its folder are not left held.
            hold.Dispose();
            throw;
        }
    }
}
