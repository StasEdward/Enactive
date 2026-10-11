namespace Enactive.Server.Runs;

using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Workspace;

/// <summary>
/// The schedules, run by the service (ADR 0002 §10): it is up all the time, which the window and a console tick from
/// Task Scheduler only approximated. Every minute it asks each workspace's schedules what is due and takes the most
/// overdue one (DueSchedules - the console's tick takes them the same way), and hands it to the line of runs.
///
/// <para>A minute because a tick is not continuous and need not be: an occurrence seen up to a quarter of an hour late
/// is still that occurrence (ScheduleTick.Grace). A console tick of the same account may keep running beside it -
/// they share the schedules and the running markers, so a schedule runs once whichever takes it - but it is no longer
/// needed on that machine.</para>
/// </summary>
internal sealed class ServerScheduler(ServerEngine engine, IRunControl runs, ILogger<ServerScheduler> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try { await TickAsync(DateTimeOffset.Now, stopping); }
            catch (Exception ex) when (!stopping.IsCancellationRequested)
            {
                // One bad tick is not the end of the schedules: said, and the next minute asks again.
                log.LogError(ex, "The schedules could not be asked this minute.");
            }
        }
        while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false));
    }

    /// <summary>One tick: each workspace's most overdue schedule into the line. What started, for a test to read.</summary>
    public async Task<IReadOnlyList<ServerRun>> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var started = new List<ServerRun>();
        foreach (var root in engine.Schedules.Workspaces())
        {
            var take = await DueSchedules.TakeAsync(engine.Schedules, engine.Markers, root, now, ct);
            if (take.Busy is { } busy)
                log.LogInformation("A schedule in {Workspace} waits for the next tick: {Busy}", root, busy);
            if (take.Failed is { } failed)
                log.LogWarning("A schedule in {Workspace} could not start: {Why}", root, failed);
            if (take.Run is not { } due)
                continue;

            var (run, refusal) = runs.StartScheduled(due);
            if (run is not null)
            {
                started.Add(run);
                continue;
            }

            // Its occurrence is spent (DueSchedules); a schedule that did not start says so where its owner looks.
            var workspace = WorkspaceInfo.For(root);
            await InboxStoreFactory.Create(workspace).AppendAsync(ScheduledOutcome.CouldNotStart(
                workspace.Id, due.Schedule.Id, due.Schedule.Name, refusal ?? "it was not started", DateTimeOffset.UtcNow), ct);
        }
        return started;
    }
}
