namespace Enactive.Server.Runs;

using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Core.Schedules;
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

    // The workspaces whose schedules were last passed over as forgotten, said once each: told every minute, the log was a
    // line a minute per forgotten folder for as long as nobody added it again.
    private readonly HashSet<string> _forgottenSaid = new(StringComparer.OrdinalIgnoreCase);

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
        // Before anything is decided, as the console tick stamps it: the pulse says the schedules are being asked, not
        // that one ran - a minute with nothing due is still a minute somebody looked.
        Heartbeat.Stamp(now, engine.TickFile);
        var started = new List<ServerRun>();
        foreach (var root in engine.Schedules.Workspaces())
        {
            // A workspace somebody forgot keeps its schedules in their store, untaken: none of its occurrences is spent,
            // no run starts, nothing goes in its inbox, and adding the folder again brings them back. Taken, a run put the
            // folder back in everybody's switcher on its own (RunControl.NotAWorkspace), and refused, each occurrence
            // would have left an inbox item about a workspace nobody shows any more.
            if (engine.Workspaces.Find(root) is null)
            {
                if (_forgottenSaid.Add(root))
                    log.LogInformation("The schedules of {Workspace} wait: the service does not know the folder as a workspace now. "
                                       + "Adding it again brings them back.", root);
                continue;
            }
            _forgottenSaid.Remove(root);

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
