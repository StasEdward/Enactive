namespace Enactive.Engine.Tests;

using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Schedules;
using Enactive.Core.Templates;
using Enactive.Settings;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// A schedule that comes round every few minutes or hours rather than once a day.
///
/// <para>Before it there were three repeats - once, daily, weekly - and "every fifteen minutes" could only be had as
/// ninety-six daily schedules. The occurrences are wall-clock times counted from the time the form names, so "every
/// 15 minutes" lands on :00, :15, :30 and :45 of the person's clock; across a clock change they stay an interval of
/// REAL time apart - the hour that happens twice is run twice, the hour that does not exist is not run.</para>
///
/// <para>Central European time for the clock changes, for ScheduleClockTests' reason: last Sunday in March forward,
/// last Sunday in October back, checkable by hand.</para>
/// </summary>
public sealed class ScheduleIntervalTests : IDisposable
{
    private static readonly string Zone =
        ScheduleClock.Zone("Europe/Berlin") is not null ? "Europe/Berlin"
        : ScheduleClock.Zone("Central European Standard Time") is not null ? "Central European Standard Time"
        : throw new InvalidOperationException("No Central European time zone on this machine.");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-interval", Guid.NewGuid().ToString("N"));

    public ScheduleIntervalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private static Schedule With(ScheduleTiming timing, DateTimeOffset? lastFired = null, MissedRun missed = MissedRun.Skip)
        => new(Guid.NewGuid(), @"C:\ws", "every so often", ScheduledWork.FromTemplate("t"), timing,
               PermissionPolicy.PermissiveDefault, CreatedAt: DateTimeOffset.MinValue, Missed: missed, LastFiredAt: lastFired);

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        var zone = ScheduleClock.Zone(Zone)!;
        var wall = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    private static string WallClock(DateTimeOffset at)
        => TimeZoneInfo.ConvertTime(at, ScheduleClock.Zone(Zone)!).ToString("yyyy-MM-dd HH:mm");

    /// <summary>The next <paramref name="count"/> occurrences after <paramref name="from"/>, each asked from the last.</summary>
    private static List<DateTimeOffset> Following(Schedule schedule, DateTimeOffset from, int count)
    {
        var found = new List<DateTimeOffset>();
        var at = from;
        for (var i = 0; i < count; i++)
        {
            at = ScheduleClock.Next(schedule, at)!.Value;
            found.Add(at);
        }
        return found;
    }

    // ── when it comes round ─────────────────────────────────────────────────

    [Fact]
    public void Every_fifteen_minutes_lands_on_the_quarter_hours()
    {
        var every = With(ScheduleTiming.Every(15, zone: Zone));

        Assert.Equal(
            ["2026-06-10 10:15", "2026-06-10 10:30", "2026-06-10 10:45", "2026-06-10 11:00"],
            Following(every, Local(2026, 6, 10, 10, 7), 4).Select(WallClock));
    }

    /// <summary>Strictly after: an occurrence asked about at its own minute is the one after it.</summary>
    [Fact]
    public void The_next_occurrence_is_strictly_after_the_moment_asked()
        => Assert.Equal("2026-06-10 10:30",
            WallClock(ScheduleClock.Next(With(ScheduleTiming.Every(15, zone: Zone)), Local(2026, 6, 10, 10, 15))!.Value));

    /// <summary>Counted from the time the form names, and carried across midnight on the same count.</summary>
    [Fact]
    public void Every_two_hours_is_counted_from_its_time_and_carries_across_midnight()
    {
        var every = With(ScheduleTiming.Every(120, new TimeOnly(1, 30), Zone));

        Assert.Equal("2026-06-10 05:30", WallClock(ScheduleClock.Next(every, Local(2026, 6, 10, 4, 0))!.Value));
        Assert.Equal(
            ["2026-06-10 23:30", "2026-06-11 01:30"],
            Following(every, Local(2026, 6, 10, 22, 0), 2).Select(WallClock));
    }

    [Fact]
    public void The_most_recent_occurrence_is_the_last_one_since_the_last_firing()
    {
        var every = With(ScheduleTiming.Every(15, zone: Zone));

        Assert.Equal("2026-06-10 10:15",
            WallClock(ScheduleClock.MostRecent(every, Local(2026, 6, 10, 10, 20), Local(2026, 6, 10, 9, 0))!.Value));
        Assert.Null(ScheduleClock.MostRecent(every, Local(2026, 6, 10, 10, 20), Local(2026, 6, 10, 10, 15)));
    }

    /// <summary>
    /// The autumn change: 02:00-03:00 happens twice. An hourly job runs in both of them - an hour of real time apart,
    /// as every other hour of the year. Taken once, as a daily schedule takes it, an every-fifteen-minutes job went
    /// quiet for an hour each October.
    /// </summary>
    [Fact]
    public void The_hour_that_happens_twice_is_run_twice()
    {
        var hourly = With(ScheduleTiming.Every(60, zone: Zone));

        var found = Following(hourly, Local(2026, 10, 25, 0, 30), 5);

        Assert.Equal(
            ["2026-10-25 01:00", "2026-10-25 02:00", "2026-10-25 02:00", "2026-10-25 03:00", "2026-10-25 04:00"],
            found.Select(WallClock));
        Assert.All(found.Zip(found.Skip(1)), pair => Assert.Equal(TimeSpan.FromHours(1), pair.Second - pair.First));
    }

    /// <summary>
    /// The spring change: 02:00-03:00 does not exist. Its quarter hours are not run - no real time went by for them -
    /// and they are not piled onto 03:00 either, as a daily schedule's one occurrence is moved there: four runs at
    /// one moment is not "every fifteen minutes".
    /// </summary>
    [Fact]
    public void The_hour_that_does_not_exist_is_not_run_and_not_piled_up()
    {
        var quarterly = With(ScheduleTiming.Every(15, zone: Zone));

        var found = Following(quarterly, Local(2026, 3, 29, 1, 30), 3);

        Assert.Equal(["2026-03-29 01:45", "2026-03-29 03:00", "2026-03-29 03:15"], found.Select(WallClock));
        Assert.All(found.Zip(found.Skip(1)), pair => Assert.Equal(TimeSpan.FromMinutes(15), pair.Second - pair.First));
    }

    /// <summary>
    /// An interval the clock cannot repeat at - edited into the file by hand, or saved by a build that allowed it - has
    /// no occurrence, and the tick says so rather than leaving a schedule that looks set and never runs.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1440)]
    public void An_interval_that_cannot_be_repeated_is_reported_not_left_silent(int? minutes)
    {
        var broken = With(new ScheduleTiming(ScheduleRepeat.Interval, TimeOnly.MinValue, Zone, EveryMinutes: minutes));
        var now = Local(2026, 6, 10, 10, 7);

        Assert.Null(ScheduleClock.Next(broken, now));
        var decision = ScheduleTick.Decide(broken, now);
        Assert.Equal(DueVerdict.Unschedulable, decision.Verdict);
        Assert.True(decision.WorthReporting);
    }

    // ── the tick ────────────────────────────────────────────────────────────

    [Fact]
    public void An_hourly_schedule_runs_at_the_hour_and_skips_one_the_machine_slept_through()
    {
        var hourly = With(ScheduleTiming.Every(60, zone: Zone), lastFired: Local(2026, 6, 10, 10, 0));

        var onTime = ScheduleTick.Decide(hourly, Local(2026, 6, 10, 11, 3));
        Assert.Equal((DueVerdict.Run, "2026-06-10 11:00"), (onTime.Verdict, WallClock(onTime.Occurrence!.Value)));

        var late = ScheduleTick.Decide(hourly, Local(2026, 6, 10, 11, 40));
        Assert.Equal(DueVerdict.Missed, late.Verdict);

        var made = ScheduleTick.Decide(hourly with { Missed = MissedRun.RunLate }, Local(2026, 6, 10, 13, 40));
        Assert.Equal((DueVerdict.Run, "2026-06-10 13:00"), (made.Verdict, WallClock(made.Occurrence!.Value)));
    }

    // ── what it says ────────────────────────────────────────────────────────

    [Fact]
    public void It_says_how_often_and_on_which_times_of_the_clock()
    {
        var zone = ScheduleClock.Zone(Zone)!.DisplayName;

        Assert.Equal($"Every 15 minutes — 00:00, 00:15, 00:30 … ({zone})", ScheduleWords.When(ScheduleTiming.Every(15, zone: Zone)));
        Assert.Equal($"Every hour — 00:05, 01:05, 02:05 … ({zone})", ScheduleWords.When(ScheduleTiming.Every(60, new TimeOnly(9, 5), Zone)));
        Assert.Equal($"Every 2 hours — 01:30, 03:30, 05:30 … ({zone})", ScheduleWords.When(ScheduleTiming.Every(120, new TimeOnly(9, 30), Zone)));
    }

    [Fact]
    public void An_interval_is_said_in_minutes_or_hours()
        => Assert.Equal(
            ["5 minutes", "15 minutes", "1 hour", "90 minutes", "2 hours", "12 hours"],
            new[] { 5, 15, 60, 90, 120, 720 }.Select(ScheduleWords.Interval));

    // ── the form ────────────────────────────────────────────────────────────

    private ScheduleDraftResult Check(ScheduleTiming timing)
        => ScheduleDrafts.Check(
            new ScheduleDraft("often", _root, ScheduledWork.FromTemplate("tidy"), timing, PermissionPolicy.PermissiveDefault),
            WorkspaceInfo.For(_root), DateTimeOffset.Now,
            id => id == "tidy" ? new TaskTemplate("tidy", "Tidy up", "tidy the folder") : null);

    [Fact]
    public void Every_offered_interval_can_be_saved()
        => Assert.All(ScheduleTiming.Intervals, minutes => Assert.Empty(Check(ScheduleTiming.Every(minutes, zone: "UTC")).Problems));

    /// <summary>
    /// Refused when it is written, not found out at the first tick: a minute is shorter than the tick that asks, and
    /// seven minutes does not divide a day, so its times would differ from one day to the next.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1440)]
    public void An_interval_the_clock_cannot_keep_is_refused_when_it_is_written(int minutes)
    {
        var result = Check(new ScheduleTiming(ScheduleRepeat.Interval, TimeOnly.MinValue, "UTC", EveryMinutes: minutes));

        Assert.Null(result.Schedule);
        Assert.Contains(result.Problems, p => p.StartsWith("Choose how often it repeats", StringComparison.Ordinal));
    }

    [Fact]
    public void An_interval_schedule_is_stored_and_read_back_whole()
    {
        var store = new ScheduleStore(Path.Combine(_root, "schedules.json"));
        var schedule = With(ScheduleTiming.Every(15, new TimeOnly(0, 5), Zone)) with { WorkspaceRoot = _root };

        store.Save(schedule);

        Assert.Equal(schedule.Timing, Assert.Single(store.For(_root)).Timing);
    }

    /// <summary>The form offers it, saves it, and opens it again on the interval it was saved with.</summary>
    [Fact]
    public async Task The_form_saves_an_interval_and_opens_it_again_on_the_same_one()
    {
        var workspace = Path.Combine(_root, "project");
        Directory.CreateDirectory(workspace);
        new TemplateStore(workspace).Save(new TaskTemplate("digest", "Digest", "Write the digest."), TemplateScope.Workspace);
        var store = new ScheduleStore(Path.Combine(_root, "schedules.json"));
        var schedules = new SchedulesViewModel(workspace, () => new AppSettings(), store, tickFile: Path.Combine(_root, "tick.txt"));
        await schedules.RefreshAsync();

        Assert.Contains("Interval", schedules.Repeats);
        schedules.NewCommand.Execute(null);
        schedules.DraftName = "inbox sweep";
        schedules.DraftWork = schedules.WorkChoices.Single(c => c.TemplateId == "digest");
        schedules.DraftRepeat = "Interval";
        Assert.True(schedules.IsInterval);
        schedules.DraftEvery = "15 minutes";
        schedules.DraftHour = 0;
        schedules.DraftMinute = 5;
        schedules.SaveCommand.Execute(null);

        var saved = Assert.Single(store.For(workspace));
        Assert.Equal((ScheduleRepeat.Interval, 15, new TimeOnly(0, 5)), (saved.Timing.Repeat, saved.Timing.EveryMinutes, saved.Timing.AtLocal));

        schedules.EditCommand.Execute(null);
        Assert.Equal(("Interval", "15 minutes"), (schedules.DraftRepeat, schedules.DraftEvery));
    }
}
