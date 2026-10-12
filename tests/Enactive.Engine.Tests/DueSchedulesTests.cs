namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Schedules;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// One tick of the schedules, as the app runs it while it is open: the most overdue schedule anywhere is
/// claimed, marked as fired and resolved to the task it runs - or, when it cannot be, the Inbox is told.
/// </summary>
public sealed class DueSchedulesTests : IDisposable
{
    // 07:02 UTC, two minutes after a daily 07:00 - due, and well inside the grace.
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 7, 2, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-due", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly ScheduleStore _schedules;
    private readonly RunMarkers _markers;
    private readonly DueSchedules _due;

    public DueSchedulesTests()
    {
        _workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(_workspace);
        _schedules = new ScheduleStore(Path.Combine(_root, "schedules.json"));
        _markers = new RunMarkers(Path.Combine(_root, "markers"));
        _due = new DueSchedules(_schedules, _markers, Path.Combine(_root, "global-templates"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private Schedule Daily(string templateId, string name = "nightly", IReadOnlyDictionary<string, string>? parameters = null)
    {
        var schedule = new Schedule(Guid.NewGuid(), _workspace, name, ScheduledWork.FromTemplate(templateId, parameters),
            ScheduleTiming.Daily(new TimeOnly(7, 0), "UTC"), AutonomyTiers.PolicyFor(3), Now.AddDays(-10));
        _schedules.Save(schedule);
        return schedule;
    }

    [Fact]
    public async Task A_due_schedule_is_taken_claimed_and_marked_as_fired()
    {
        var schedule = Daily("release-check");

        using var taken = await _due.TakeAsync(Now, CancellationToken.None);

        Assert.NotNull(taken);
        Assert.Equal(schedule.Id, taken.Schedule.Id);
        Assert.Equal("release-check", taken.Spec.TemplateId);
        Assert.Equal(Path.GetFullPath(_workspace), taken.Workspace.RootPath);
        Assert.True(_markers.IsRunning(schedule.Id));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 7, 0, 0, TimeSpan.Zero),
            _schedules.For(_workspace).Single().LastFiredAt);
    }

    // Marked as fired before it runs: the next tick a minute later must not start the same occurrence again,
    // whether the first run is still going or not.
    [Fact]
    public async Task The_same_occurrence_is_not_taken_twice()
    {
        Daily("release-check");

        var first = await _due.TakeAsync(Now, CancellationToken.None);
        first!.Dispose();
        var second = await _due.TakeAsync(Now.AddMinutes(1), CancellationToken.None);

        Assert.Null(second);
    }

    // The claim is the one the console's --due-all takes too: the app and a Windows task on the same machine do
    // not both start it.
    [Fact]
    public async Task A_schedule_claimed_elsewhere_is_not_taken()
    {
        var schedule = Daily("release-check");
        using var elsewhere = _markers.Claim(schedule.Id);

        var taken = await _due.TakeAsync(Now, CancellationToken.None);

        Assert.Null(taken);
        Assert.Null(_schedules.For(_workspace).Single().LastFiredAt);
    }

    [Fact]
    public async Task Nothing_is_taken_when_nothing_is_due()
    {
        Daily("release-check");

        Assert.Null(await _due.TakeAsync(Now.AddHours(-2), CancellationToken.None));
    }

    // Reported once, not retried every minute: the occurrence is marked as fired and the Inbox says why it did not run.
    [Fact]
    public async Task A_schedule_whose_template_is_gone_is_reported_and_not_retried()
    {
        var schedule = Daily("no-such-template", "vanished");

        var taken = await _due.TakeAsync(Now, CancellationToken.None);

        Assert.Null(taken);
        Assert.NotNull(_schedules.For(_workspace).Single().LastFiredAt);
        Assert.False(_markers.IsRunning(schedule.Id));
        var item = Assert.Single(await InboxStoreFactory.Create(WorkspaceInfo.For(_workspace)).LoadAllAsync(CancellationToken.None));
        Assert.Equal(schedule.Id, item.ScheduleId);
        Assert.StartsWith("Did not run", item.Summary);
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
    // schedules window reads, and runs what is due as the console does - a schedule's run, nobody to answer.
    [Fact]
    public void The_app_asks_the_schedules_every_minute_while_it_is_open()
    {
        var window = File.ReadAllText(Path.Combine(TestRepository.Root, "src", "Enactive.App.Ui", "MainWindow.axaml.cs"));

        Assert.Contains("ScheduleTickEvery = TimeSpan.FromMinutes(1)", window, StringComparison.Ordinal);
        Assert.Contains("_scheduleTick.Start();", window, StringComparison.Ordinal);
        Assert.Contains("Heartbeat.Stamp(now);", window, StringComparison.Ordinal);
        Assert.Contains("DueSchedules.Default.TakeAsync(", window, StringComparison.Ordinal);
        var at = window.IndexOf("private void StartScheduled(", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var start = window[at..(at + 4000)];
        Assert.Contains("IntentSource.Schedule", start, StringComparison.Ordinal);
        Assert.Contains("new UnattendedDecisionHandler()", start, StringComparison.Ordinal);
        Assert.Contains("schedule.Name, ct, schedule.Id)", start, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposing_what_was_taken_releases_the_claim()
    {
        var schedule = Daily("release-check");

        var taken = await _due.TakeAsync(Now, CancellationToken.None);
        taken!.Dispose();

        Assert.False(_markers.IsRunning(schedule.Id));
    }
}
