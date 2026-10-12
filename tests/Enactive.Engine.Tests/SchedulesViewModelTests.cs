namespace Enactive.Engine.Tests;

using Enactive.Application;
using Enactive.Core.Schedules;
using Enactive.Core.Templates;
using Enactive.Settings;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The schedules' form and list (SchedulesViewModel), shared by the window and the service's page from the application
/// layer: what the form saves goes where its host says (IScheduleChanges) - the window's own store, or the service, which
/// may refuse - what it will agree to is said before it is saved, and the warning that nothing will run reads the tick of
/// the host that runs them. Every store here is a temp file: the machine's real schedules are %APPDATA%'s.
/// </summary>
public sealed class SchedulesViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-schedules-vm", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly ScheduleStore _store;

    public SchedulesViewModelTests()
    {
        _workspace = Path.Combine(_root, "project");
        Directory.CreateDirectory(_workspace);
        new TemplateStore(_workspace).Save(new TaskTemplate("digest", "Digest", "Write the weekly digest."), TemplateScope.Workspace);
        _store = new ScheduleStore(Path.Combine(_root, "schedules.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private async Task<SchedulesViewModel> Open(IScheduleChanges? changes = null, string? tickFile = null)
    {
        var schedules = new SchedulesViewModel(_workspace, () => new AppSettings(), _store, changes: changes,
            tickFile: tickFile ?? Path.Combine(_root, "tick.txt"));
        await schedules.RefreshAsync();
        return schedules;
    }

    private static void Fill(SchedulesViewModel schedules, string name = "morning digest")
    {
        schedules.NewCommand.Execute(null);
        schedules.DraftName = name;
        schedules.DraftWork = schedules.WorkChoices.Single(c => c.TemplateId == "digest");
        schedules.DraftHour = 7;
        schedules.DraftMinute = 30;
    }

    [Fact]
    public async Task The_window_s_form_saves_into_its_own_store_and_selects_what_it_saved()
    {
        var schedules = await Open();

        Fill(schedules);
        schedules.SaveCommand.Execute(null);

        var saved = Assert.Single(_store.For(_workspace));
        Assert.Equal(("morning digest", "digest", new TimeOnly(7, 30)), (saved.Name, saved.Work.TemplateId, saved.Timing.AtLocal));
        Assert.False(schedules.IsEditing);
        Assert.Equal(saved.Id, schedules.Selected?.Schedule.Id);

        schedules.ToggleEnabledCommand.Execute(null);
        Assert.False(_store.For(_workspace).Single().Enabled);
        schedules.DeleteCommand.Execute(null);
        Assert.Empty(_store.For(_workspace));
    }

    /// <summary>
    /// A host that refuses - the service, to a person whose role does not include it - keeps nothing, and the person is
    /// told why where the form says what is wrong: the draft stays open, as typed.
    /// </summary>
    [Fact]
    public async Task A_change_the_host_refuses_is_not_kept_and_says_why()
    {
        var refusing = new Refusing("Your role does not include changing templates and schedules.");
        var schedules = await Open(refusing);

        Fill(schedules);
        schedules.SaveCommand.Execute(null);

        Assert.Empty(_store.For(_workspace));
        Assert.True(schedules.IsEditing);
        Assert.Equal(refusing.Why, schedules.Problems);
        Assert.Equal("morning digest", Assert.Single(refusing.Asked).Name);
    }

    [Fact]
    public async Task Switching_or_deleting_one_the_host_refuses_leaves_it_and_says_why()
    {
        _store.Save(Saved());
        var refusing = new Refusing("Your session has ended. Sign in again.");
        var schedules = await Open(refusing);

        schedules.ToggleEnabledCommand.Execute(null);
        Assert.Equal(refusing.Why, schedules.Status);
        schedules.DeleteCommand.Execute(null);

        Assert.True(Assert.Single(_store.For(_workspace)).Enabled);
        Assert.Equal(refusing.Why, schedules.Status);
    }

    /// <summary>
    /// What the form will agree to is said as it is filled in, before Save: the lines a saved schedule's "What this will
    /// do" shows, of the schedule ScheduleDrafts makes of the form - when it runs next among them. Nothing while the form
    /// could not be saved yet.
    /// </summary>
    [Fact]
    public async Task The_form_says_what_it_will_agree_to_before_it_is_saved()
    {
        var schedules = await Open();
        Fill(schedules, name: "");

        Assert.Empty(schedules.DraftApproval);

        schedules.DraftName = "morning digest";

        Assert.Contains(schedules.DraftApproval, line => line.StartsWith("Next run: ", StringComparison.Ordinal));
        Assert.Contains(ScheduleWords.When(ScheduleTiming.Daily(new TimeOnly(7, 30), schedules.DraftZone.Id)), schedules.DraftApproval);
        Assert.Empty(_store.For(_workspace));
    }

    /// <summary>
    /// The warning that nothing will run reads the tick its host names: the service's scheduler keeps its own pulse,
    /// and read from the machine's - which no console tick writes there - its page said nothing would run while the
    /// service ran them every minute.
    /// </summary>
    [Fact]
    public async Task The_warning_that_nothing_runs_reads_the_hosts_own_tick()
    {
        var tick = Path.Combine(_root, "service", "tick.txt");

        Assert.True((await Open(tickFile: tick)).HasTickWarning);

        Heartbeat.Stamp(DateTimeOffset.Now, tick);

        Assert.False((await Open(tickFile: tick)).HasTickWarning);
    }

    private Schedule Saved()
        => new(Guid.NewGuid(), _workspace, "morning digest", ScheduledWork.FromTemplate("digest"),
            ScheduleTiming.Daily(new TimeOnly(7, 30)), new SchedulePolicy(() => new AppSettings()).For("execute"), DateTimeOffset.Now);

    private sealed class Refusing(string why) : IScheduleChanges
    {
        public string Why { get; } = why;
        public List<Schedule> Asked { get; } = [];

        public string? Save(Schedule schedule)
        {
            Asked.Add(schedule);
            return Why;
        }

        public string? Switch(Schedule schedule, bool on) => Why;
        public string? Remove(Schedule schedule) => Why;
    }
}
