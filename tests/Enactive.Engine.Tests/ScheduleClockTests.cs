namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Enactive.Core.Schedules;
using Xunit;

/// <summary>
/// When a schedule is next due.
///
/// <para>This is the only part of a schedule with a rule that can be WRONG, and it is wrong on
/// exactly two days a year - which is why it is worth a file of its own. A daily job that slides an
/// hour twice a year, or silently misses one day in spring, is a defect nobody attributes to the
/// day the clocks moved.</para>
///
/// <para>Central European time throughout: EU rules are the ones most people can check by hand -
/// last Sunday in March forward, last Sunday in October back - so a failure here can be reasoned
/// about rather than only re-run.</para>
/// </summary>
public sealed class ScheduleClockTests
{
    /// <summary>
    /// The zone under both spellings. .NET takes IANA and Windows ids on either platform, but which
    /// one is present depends on the machine's data, and a test that cannot find its zone must say
    /// so rather than quietly pass.
    /// </summary>
    private static readonly string Zone =
        ScheduleClock.Zone("Europe/Berlin") is not null ? "Europe/Berlin"
        : ScheduleClock.Zone("Central European Standard Time") is not null ? "Central European Standard Time"
        : throw new InvalidOperationException("No Central European time zone on this machine.");

    private static Schedule With(ScheduleTiming timing, bool enabled = true)
        => new(Guid.NewGuid(), @"C:\ws", "test", ScheduledWork.FromTemplate("t"), timing,
               PermissionPolicy.PermissiveDefault, CreatedAt: DateTimeOffset.MinValue, Enabled: enabled);

    /// <summary>A moment in that zone, written the way a person would say it.</summary>
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        var zone = ScheduleClock.Zone(Zone)!;
        var wall = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    /// <summary>The wall clock a person would read off, for the zone this schedule is in.</summary>
    private static string WallClock(DateTimeOffset at)
        => TimeZoneInfo.ConvertTime(at, ScheduleClock.Zone(Zone)!).ToString("yyyy-MM-dd HH:mm");

    // ── the ordinary days ───────────────────────────────────────────────────

    [Fact]
    public void A_daily_schedule_is_due_later_today_when_the_time_has_not_passed()
    {
        var next = ScheduleClock.Next(
            With(ScheduleTiming.Daily(new TimeOnly(9, 0), Zone)), Local(2026, 6, 10, 7, 30));

        Assert.Equal("2026-06-10 09:00", WallClock(next!.Value));
    }

    [Fact]
    public void A_daily_schedule_that_has_passed_today_is_due_tomorrow()
    {
        var next = ScheduleClock.Next(
            With(ScheduleTiming.Daily(new TimeOnly(9, 0), Zone)), Local(2026, 6, 10, 9, 30));

        Assert.Equal("2026-06-11 09:00", WallClock(next!.Value));
    }

    [Fact]
    public void A_weekly_schedule_lands_on_its_own_day()
    {
        var next = ScheduleClock.Next(
            With(ScheduleTiming.Weekly(DayOfWeek.Tuesday, new TimeOnly(9, 0), Zone)),
            Local(2026, 6, 10, 12, 0));   // a Wednesday

        Assert.Equal("2026-06-16 09:00", WallClock(next!.Value));
        Assert.Equal(DayOfWeek.Tuesday, TimeZoneInfo.ConvertTime(next.Value, ScheduleClock.Zone(Zone)!).DayOfWeek);
    }

    // ── the two days a year ─────────────────────────────────────────────────

    /// <summary>
    /// The named differential from SCHEDULER_PLAN step 1: nine o'clock stays nine o'clock across a
    /// clock change.
    ///
    /// <para>Stored as an instant and repeated by adding 24 hours, the occurrence after the
    /// last Sunday in March would be 10:00 - an hour late every day for seven months, and nobody
    /// would connect it to the day the clocks moved.</para>
    /// </summary>
    [Theory]
    [InlineData(2026, 3, 28, "2026-03-29 09:00")]   // the day before the spring change
    [InlineData(2026, 10, 24, "2026-10-25 09:00")]  // the day before the autumn change
    public void A_daily_schedule_keeps_its_wall_clock_across_a_clock_change(
        int year, int month, int day, string expected)
    {
        var next = ScheduleClock.Next(
            With(ScheduleTiming.Daily(new TimeOnly(9, 0), Zone)), Local(year, month, day, 12, 0));

        Assert.Equal(expected, WallClock(next!.Value));
    }

    /// <summary>
    /// The hour that does not exist. On the last Sunday in March the clocks go 02:00 → 03:00, so a
    /// 02:30 schedule has no 02:30 to run at.
    ///
    /// <para>It runs at the far side of the gap. Skipping is the tempting answer and the wrong one:
    /// a daily job that silently does not run one day a year is a schedule that looks configured
    /// and is not.</para>
    /// </summary>
    [Fact]
    public void A_time_that_does_not_exist_that_day_runs_at_the_end_of_the_gap()
    {
        var next = ScheduleClock.Next(
            With(ScheduleTiming.Daily(new TimeOnly(2, 30), Zone)), Local(2026, 3, 29, 0, 30));

        Assert.Equal("2026-03-29 03:00", WallClock(next!.Value));
    }

    /// <summary>
    /// The hour that happens twice. On the last Sunday in October 02:00 → 01:00, so 01:30 comes
    /// round twice; the job runs ONCE, at the earlier of the two.
    ///
    /// <para>Asserted on the UTC instant, because both candidates read "01:30" on the wall and only
    /// the offset tells them apart - a test comparing the wall clock would pass either way.</para>
    /// </summary>
    [Fact]
    public void A_time_that_happens_twice_that_day_runs_at_the_first_of_them()
    {
        var next = ScheduleClock.Next(
            With(ScheduleTiming.Daily(new TimeOnly(1, 30), Zone)), Local(2026, 10, 24, 12, 0));

        // 01:30 CEST (+02:00) is 23:30 UTC the previous day; 01:30 CET (+01:00) is 00:30 UTC.
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero), next!.Value.ToUniversalTime());
    }

    // ── the ones with no next time ──────────────────────────────────────────

    [Fact]
    public void A_disabled_schedule_is_never_due()
        => Assert.Null(ScheduleClock.Next(
            With(ScheduleTiming.Daily(new TimeOnly(9, 0), Zone), enabled: false), Local(2026, 6, 10, 7, 0)));

    [Fact]
    public void A_one_off_is_due_once_and_then_never()
    {
        var at = Local(2026, 6, 10, 9, 0);
        var schedule = With(ScheduleTiming.Once(at));

        Assert.Equal(at, ScheduleClock.Next(schedule, at.AddHours(-1)));
        Assert.Null(ScheduleClock.Next(schedule, at));
    }

    /// <summary>
    /// A zone this machine has never heard of gives no next time - it does not throw. A tick walking
    /// every schedule would otherwise stop at the first one made on a machine with different data,
    /// and the rest would silently never run.
    /// </summary>
    [Fact]
    public void A_zone_this_machine_does_not_know_is_not_an_exception()
        => Assert.Null(ScheduleClock.Next(
            With(ScheduleTiming.Daily(new TimeOnly(9, 0), "Mars/Olympus_Mons")), DateTimeOffset.UtcNow));
}
