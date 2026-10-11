namespace Enactive.Engine.Tests;

using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Schedules;
using Enactive.Core.Storage;
using Enactive.Core.Templates;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Taking a workspace's due schedule (DueSchedules), as both the console's tick and the service's scheduler do it: they
/// must not disagree about which runs, when its occurrence is spent, or what a schedule that cannot start leaves.
/// </summary>
public sealed class DueSchedulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-due", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly ScheduleStore _schedules;
    private readonly RunMarkers _markers;

    public DueSchedulesTests()
    {
        _workspace = Path.Combine(_root, "project");
        Directory.CreateDirectory(_workspace);
        _schedules = new ScheduleStore(Path.Combine(_root, "schedules.json"));
        _markers = new RunMarkers(Path.Combine(_root, "running"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private static readonly PermissionPolicy Autonomous = new(PermissionLevel.Autonomous, ["*"], []);

    /// <summary>A schedule repeating a past run, due a minute ago.</summary>
    private Schedule Due(string goal = "digest the inbox", string? template = null)
    {
        var spec = new ResolvedTaskSpec("digest", 1, "Digest", Guid.NewGuid(), "project", _workspace, goal,
            new Dictionary<string, string>(), Autonomous, [], ExecutionLimits.None, null, false);
        var schedule = new Schedule(Guid.NewGuid(), _workspace, "morning digest",
            template is null ? ScheduledWork.FromPastRun(spec.Snapshot()) : ScheduledWork.FromTemplate(template),
            ScheduleTiming.Once(DateTimeOffset.Now.AddMinutes(-1)), Autonomous, CreatedAt: DateTimeOffset.Now.AddHours(-1));
        _schedules.Save(schedule);
        return schedule;
    }

    [Fact]
    public async Task A_due_schedule_is_taken_claimed_with_its_folder_and_its_occurrence_spent()
    {
        var schedule = Due();

        var take = await DueSchedules.TakeAsync(_schedules, _markers, _workspace, DateTimeOffset.Now, CancellationToken.None);

        Assert.NotNull(take.Run);
        Assert.Equal("digest the inbox", take.Run.Spec.Goal);
        Assert.True(_markers.IsRunning(schedule.Id));
        Assert.NotNull(_schedules.For(_workspace).Single().LastFiredAt);
        // Held by this process: the run's own lease counts; another process would be refused (WorkspaceLeaseTests).
        Assert.True(File.Exists(WorkspaceLease.PathFor(_workspace)), "The workspace was not held for the run.");

        take.Run.Hold.Dispose();
        Assert.False(_markers.IsRunning(schedule.Id));
    }

    /// <summary>Refused its folder - another process runs there - it stays due for the next tick, unclaimed and unspent.</summary>
    [Fact]
    public async Task A_schedule_whose_folder_is_busy_stays_due_and_unspent()
    {
        var schedule = Due();
        using var other = WorkspaceLeaseTests.ForeignHolder(_workspace);

        var take = await DueSchedules.TakeAsync(_schedules, _markers, _workspace, DateTimeOffset.Now, CancellationToken.None);

        Assert.Null(take.Run);
        Assert.Contains("next tick", take.Busy);
        Assert.Null(_schedules.For(_workspace).Single().LastFiredAt);
        Assert.False(_markers.IsRunning(schedule.Id));
    }

    /// <summary>A schedule that cannot become a task is said in the inbox, once: its occurrence is spent all the same.</summary>
    [Fact]
    public async Task A_schedule_that_cannot_start_is_reported_once_in_the_inbox()
    {
        var schedule = Due(template: "gone-template");

        var take = await DueSchedules.TakeAsync(_schedules, _markers, _workspace, DateTimeOffset.Now, CancellationToken.None);

        Assert.Null(take.Run);
        Assert.Contains("no template 'gone-template'", take.Failed);
        Assert.NotNull(_schedules.For(_workspace).Single().LastFiredAt);
        Assert.False(_markers.IsRunning(schedule.Id));
        var item = Assert.Single(await InboxStoreFactory.Create(WorkspaceInfo.For(_workspace)).LoadAllAsync(CancellationToken.None));
        Assert.Equal(schedule.Id, item.ScheduleId);

        var again = await DueSchedules.TakeAsync(_schedules, _markers, _workspace, DateTimeOffset.Now, CancellationToken.None);
        Assert.Null(again.Failed);
    }

    [Fact]
    public async Task Nothing_due_takes_nothing()
    {
        var take = await DueSchedules.TakeAsync(_schedules, _markers, _workspace, DateTimeOffset.Now, CancellationToken.None);

        Assert.Equal((null, null, null), (take.Run, take.Busy, take.Failed));
        Assert.Empty(take.Decisions);
    }
}
