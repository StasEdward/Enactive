namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Schedules;
using Enactive.Core.Storage;
using Enactive.Core.Templates;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Taking a due schedule (DueSchedules), as the window, the console's tick and the service's scheduler all do it: they
/// must not disagree about which runs, when its occurrence is spent, or what a schedule that cannot start leaves.
/// </summary>
public sealed class DueSchedulesTests : IDisposable
{
    // 07:02 UTC, two minutes after a daily 07:00 - due, and well inside the grace.
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 7, 2, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-due", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly string _globalTemplates;
    private readonly ScheduleStore _schedules;
    private readonly RunMarkers _markers;

    public DueSchedulesTests()
    {
        _workspace = Path.Combine(_root, "project");
        Directory.CreateDirectory(_workspace);
        _globalTemplates = Path.Combine(_root, "global-templates");
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

    /// <summary>A daily 07:00 schedule of a template, due at <see cref="Now"/>.</summary>
    private Schedule Daily(string templateId, string name = "nightly", string? workspace = null)
    {
        var schedule = new Schedule(Guid.NewGuid(), workspace ?? _workspace, name, ScheduledWork.FromTemplate(templateId),
            ScheduleTiming.Daily(new TimeOnly(7, 0), "UTC"), AutonomyTiers.PolicyFor(3), Now.AddDays(-10));
        _schedules.Save(schedule);
        return schedule;
    }

    private Task<DueTake> TakeFirstAsync(DateTimeOffset now)
        => DueSchedules.TakeFirstAsync(_schedules, _markers, now, CancellationToken.None, _globalTemplates);

    // ── one workspace: the service's tick, the console's --due ──────────────

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

        take.Run.Dispose();
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

    /// <summary>
    /// While this process runs "Run checks now" in the folder, a due schedule waits for the next tick, unclaimed and
    /// unspent - the lease counts this process's own takes, so it was taken, its occurrence spent, and the run then refused
    /// by the composer: the day skipped. After the press it fires.
    /// </summary>
    [Fact]
    public async Task A_schedule_due_while_checks_run_in_its_folder_stays_due_and_fires_after()
    {
        var schedule = Due();

        using (WorkspaceActivity.TryChecks(_workspace, out _)!)
        {
            var take = await DueSchedules.TakeAsync(_schedules, _markers, _workspace, DateTimeOffset.Now, CancellationToken.None);

            Assert.Null(take.Run);
            Assert.Contains("next tick", take.Busy);
            Assert.Contains("checks are running", take.Busy);
            Assert.Null(_schedules.For(_workspace).Single().LastFiredAt);
            Assert.False(_markers.IsRunning(schedule.Id));
        }

        var after = await DueSchedules.TakeAsync(_schedules, _markers, _workspace, DateTimeOffset.Now, CancellationToken.None);
        Assert.NotNull(after.Run);
        Assert.NotNull(_schedules.For(_workspace).Single().LastFiredAt);
        // Held as a run of this process now: the checks wait for it.
        Assert.Null(WorkspaceActivity.TryChecks(_workspace, out var why));
        Assert.Contains("A run is going", why);
        after.Run.Dispose();
        using var free = WorkspaceActivity.TryChecks(_workspace, out _);
        Assert.NotNull(free);
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
        Assert.StartsWith("Did not run", item.Summary);

        var again = await DueSchedules.TakeAsync(_schedules, _markers, _workspace, DateTimeOffset.Now, CancellationToken.None);
        Assert.Null(again.Failed);
    }

    /// <summary>
    /// A folder that is gone is not made again: running would create an empty workspace where the project was. Spent,
    /// so it is not tried every minute, and said. The window's copy of these steps had this and the service's did not.
    /// </summary>
    [Fact]
    public async Task A_schedule_whose_folder_is_gone_is_spent_and_the_folder_not_made_again()
    {
        var gone = Path.Combine(_root, "moved-away");
        var schedule = Daily("release-check", "vanished folder", workspace: gone);

        var take = await TakeFirstAsync(Now);

        Assert.Null(take.Run);
        Assert.Contains("not there any more", take.Failed);
        Assert.False(Directory.Exists(gone));
        Assert.NotNull(_schedules.For(gone).Single().LastFiredAt);
        Assert.False(_markers.IsRunning(schedule.Id));
    }

    [Fact]
    public async Task Nothing_due_takes_nothing()
    {
        var take = await DueSchedules.TakeAsync(_schedules, _markers, _workspace, DateTimeOffset.Now, CancellationToken.None);

        Assert.Equal((null, null, null), (take.Run, take.Busy, take.Failed));
        Assert.Empty(take.Decisions);
    }

    // ── every workspace: the window's tick, the console's --due-all ─────────

    [Fact]
    public async Task The_most_overdue_schedule_anywhere_is_taken_with_its_template()
    {
        var schedule = Daily("release-check");

        using var taken = (await TakeFirstAsync(Now)).Run;

        Assert.NotNull(taken);
        Assert.Equal(schedule.Id, taken.Schedule.Id);
        Assert.Equal("release-check", taken.Spec.TemplateId);
        Assert.Equal(Path.GetFullPath(_workspace), taken.Workspace.RootPath);
        Assert.True(_markers.IsRunning(schedule.Id));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 7, 0, 0, TimeSpan.Zero), _schedules.For(_workspace).Single().LastFiredAt);
    }

    // Marked as fired before it runs: the next tick a minute later must not start the same occurrence again,
    // whether the first run is still going or not.
    [Fact]
    public async Task The_same_occurrence_is_not_taken_twice()
    {
        Daily("release-check");

        (await TakeFirstAsync(Now)).Run!.Dispose();

        Assert.Null((await TakeFirstAsync(Now.AddMinutes(1))).Run);
    }

    // The claim is the one every tick takes: the window, a console from a Windows task and the service on one machine
    // do not both start it.
    [Fact]
    public async Task A_schedule_claimed_elsewhere_is_not_taken_and_stays_unspent()
    {
        var schedule = Daily("release-check");
        using var elsewhere = _markers.Claim(schedule.Id);

        var take = await TakeFirstAsync(Now);

        // Not even due: a schedule running elsewhere is decided as running (ScheduleTick), not taken and refused.
        Assert.Null(take.Run);
        Assert.DoesNotContain(take.Decisions, d => d.ShouldRun);
        Assert.Null(_schedules.For(_workspace).Single().LastFiredAt);
    }

    [Fact]
    public async Task Nothing_is_taken_when_nothing_is_due()
    {
        Daily("release-check");

        Assert.Null((await TakeFirstAsync(Now.AddHours(-2))).Run);
    }

    // The schedules window lists a schedule's outcomes by the schedule id on its Inbox items: a run the app started
    // for a schedule, filed without it, would be a schedule that has "never run".
    [Fact]
    public async Task A_background_run_for_a_schedule_files_its_outcome_under_that_schedule()
    {
        var inbox = new ListInbox();
        var scheduleId = Guid.NewGuid();

        await BackgroundRunner.RunAsync(NoEvents(), inbox, WorkspaceInfo.For(_workspace), "nightly", CancellationToken.None, scheduleId);

        Assert.Equal(scheduleId, Assert.Single(inbox.Items).ScheduleId);
    }

    private static async IAsyncEnumerable<Enactive.Core.Events.WorkEvent> NoEvents()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class ListInbox : Enactive.Core.Inbox.IInboxStore
    {
        public List<Enactive.Core.Inbox.InboxItem> Items { get; } = [];
        public Task AppendAsync(Enactive.Core.Inbox.InboxItem item, CancellationToken ct) { Items.Add(item); return Task.CompletedTask; }
        public Task<IReadOnlyList<Enactive.Core.Inbox.InboxItem>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Enactive.Core.Inbox.InboxItem>>(Items);
        public Task MarkReadAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task MarkAllReadAsync(CancellationToken ct) => Task.CompletedTask;
    }

    // The window is what the person has open: it asks the schedules itself, every minute, with the pulse the
    // schedules window reads, and runs what is due as the console does - a schedule's run, nobody to answer - through
    // the same steps as every other host (DueSchedules).
    [Fact]
    public void The_app_asks_the_schedules_every_minute_while_it_is_open()
    {
        var window = File.ReadAllText(Path.Combine(TestRepository.Root, "src", "Enactive.App.Ui", "MainWindow.axaml.cs"));

        Assert.Contains("ScheduleTickEvery = TimeSpan.FromMinutes(1)", window, StringComparison.Ordinal);
        Assert.Contains("_scheduleTick.Start();", window, StringComparison.Ordinal);
        Assert.Contains("Heartbeat.Stamp(now);", window, StringComparison.Ordinal);
        Assert.Contains("DueSchedules.TakeFirstAsync(", window, StringComparison.Ordinal);
        var at = window.IndexOf("private void StartScheduled(", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var start = window[at..(at + 4000)];
        Assert.Contains("IntentSource.Schedule", start, StringComparison.Ordinal);
        Assert.Contains("new UnattendedDecisionHandler()", start, StringComparison.Ordinal);
        Assert.Contains("schedule.Name, ct, schedule.Id)", start, StringComparison.Ordinal);
    }

    /// <summary>One copy of the steps: the window's own (Enactive.Workspace) is gone into the shared one.</summary>
    [Fact]
    public void There_is_one_copy_of_taking_a_due_schedule()
        => Assert.Null(typeof(WorkspaceInfo).Assembly.GetTypes().FirstOrDefault(t => t.Name == "DueSchedules")
                       ?? typeof(Enactive.Workspace.InboxStoreFactory).Assembly.GetTypes().FirstOrDefault(t => t.Name == "DueSchedules"));
}
