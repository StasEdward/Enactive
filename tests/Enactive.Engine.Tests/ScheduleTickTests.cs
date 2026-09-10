namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Enactive.Core.Schedules;
using Xunit;

/// <summary>
/// What a tick decides, and — mostly — why a run is NOT happening.
///
/// <para>Every verdict here except <c>Run</c> is a run that does not happen, and a run that does
/// not happen is invisible unless something says so. The rules are separated because from the
/// outside they look identical — nothing ran — and they mean opposite things about whether the work
/// is in hand: <c>Busy</c> is the work happening right now, <c>Missed</c> is the work not happening
/// at all.</para>
///
/// <para>Decide() is a pure function of its inputs, which is the only reason any of this is
/// checkable. A timer that can only be tested by waiting until three in the morning is a timer
/// nobody tests.</para>
/// </summary>
public sealed class ScheduleTickTests
{
    private const string Zone = "UTC";   // the rules here are about lateness, not about clocks

    private static readonly DateTimeOffset Now = new(2026, 6, 10, 9, 5, 0, TimeSpan.Zero);

    private static Schedule At(TimeOnly at, DateTimeOffset? lastFired = null,
                               MissedRun missed = MissedRun.Skip, bool enabled = true)
        => new(Guid.NewGuid(), @"C:\ws", "nightly", ScheduledWork.FromTemplate("t"),
               ScheduleTiming.Daily(at, Zone), PermissionPolicy.PermissiveDefault,
               // Made a fortnight ago: these tests are about what happens to occurrences since,
               // and a schedule created after its own occurrence has none to reason about.
               CreatedAt: Now.AddDays(-14),
               missed, enabled, lastFired);

    [Fact]
    public void A_schedule_whose_time_has_just_come_runs()
    {
        var decision = ScheduleTick.Decide(At(new TimeOnly(9, 0)), Now);

        Assert.Equal(DueVerdict.Run, decision.Verdict);
        Assert.True(decision.ShouldRun);
    }

    /// <summary>
    /// Waiting for this evening, having run yesterday evening.
    ///
    /// <para>The <c>lastFired</c> matters and the first version of this test did without it - which
    /// made it a schedule created a fortnight ago that had never once run, and every one of those
    /// evenings really had been missed. The code said Missed and was right; the test was describing
    /// a state that does not occur.</para>
    /// </summary>
    [Fact]
    public void A_schedule_whose_time_has_not_come_is_not_due()
    {
        var decision = ScheduleTick.Decide(At(new TimeOnly(18, 0), lastFired: Yesterday(18)), Now);

        Assert.Equal(DueVerdict.NotDue, decision.Verdict);
        Assert.False(decision.WorthReporting);   // "not yet" is the one answer nobody needs telling
    }

    /// <summary>The occurrence a well-behaved evening schedule last ran at.</summary>
    private static DateTimeOffset Yesterday(int hour)
        => new(2026, 6, 9, hour, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A tick is not continuous. Something wakes every few minutes, so an occurrence at 09:00 seen
    /// at 09:05 IS that occurrence — treating it as missed would mean a schedule almost never runs
    /// on the nose and therefore almost never runs at all.
    /// </summary>
    [Fact]
    public void A_few_minutes_late_is_still_now()
        => Assert.Equal(DueVerdict.Run, ScheduleTick.Decide(At(new TimeOnly(9, 0)), Now).Verdict);

    /// <summary>The machine was asleep at three. A skipping schedule does not make it up.</summary>
    [Fact]
    public void An_occurrence_long_gone_is_missed_rather_than_run()
    {
        var decision = ScheduleTick.Decide(At(new TimeOnly(3, 0)), Now);

        Assert.Equal(DueVerdict.Missed, decision.Verdict);
        Assert.True(decision.WorthReporting);

        // The sentence has to carry the WHEN. "Skipped" alone is not something a person can act on.
        Assert.Contains("03:00", decision.Why, StringComparison.Ordinal);
    }

    /// <summary>And the other choice, made when the schedule was created, runs it late.</summary>
    [Fact]
    public void A_schedule_told_to_run_late_runs_late()
    {
        var decision = ScheduleTick.Decide(At(new TimeOnly(3, 0), missed: MissedRun.RunLate), Now);

        Assert.Equal(DueVerdict.Run, decision.Verdict);
        Assert.Contains("late", decision.Why, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A week asleep is still ONE run, not seven. The occurrence is measured from the last firing,
    /// so a backlog collapses to its most recent member rather than queueing.
    /// </summary>
    [Fact]
    public void A_week_of_missed_occurrences_is_one_run_and_not_seven()
    {
        var schedule = At(new TimeOnly(3, 0), lastFired: Now.AddDays(-7), missed: MissedRun.RunLate);

        var decision = ScheduleTick.Decide(schedule, Now);
        Assert.Equal(DueVerdict.Run, decision.Verdict);

        // Firing advances the mark, and the next tick finds tomorrow rather than the other six.
        var after = ScheduleTick.Decide(schedule with { LastFiredAt = Now }, Now);
        Assert.Equal(DueVerdict.NotDue, after.Verdict);
    }

    /// <summary>
    /// Busy is asked LAST, so a schedule whose previous run is still going says so rather than
    /// saying it was missed. From the outside both are "nothing started"; one means the work is in
    /// hand and the other means it is not.
    /// </summary>
    [Fact]
    public void A_schedule_whose_previous_run_is_still_going_says_so()
    {
        var schedule = At(new TimeOnly(3, 0), missed: MissedRun.RunLate);

        var decision = ScheduleTick.Decide(schedule, Now, isRunning: _ => true);

        Assert.Equal(DueVerdict.Busy, decision.Verdict);
        Assert.NotEqual(DueVerdict.Missed, decision.Verdict);
    }

    [Fact]
    public void A_schedule_that_is_switched_off_is_off_rather_than_missed()
    {
        var decision = ScheduleTick.Decide(At(new TimeOnly(3, 0), enabled: false), Now);

        Assert.Equal(DueVerdict.Off, decision.Verdict);
        Assert.False(decision.WorthReporting);
    }

    /// <summary>
    /// A schedule that cannot be timed is reported, not silently ignored. A zone this machine has
    /// never heard of is the realistic way in: a schedule made on another computer, or one whose
    /// zone database is older.
    /// </summary>
    [Fact]
    public void A_schedule_that_cannot_be_timed_says_why()
    {
        var schedule = At(new TimeOnly(3, 0)) with
        {
            Timing = ScheduleTiming.Daily(new TimeOnly(3, 0), "Mars/Olympus_Mons")
        };

        var decision = ScheduleTick.Decide(schedule, Now);

        Assert.Equal(DueVerdict.Unschedulable, decision.Verdict);
        Assert.True(decision.WorthReporting);
        Assert.Contains("Mars/Olympus_Mons", decision.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Deciding_a_list_answers_for_every_one_of_them()
    {
        var decisions = ScheduleTick.Decide(
            new[]
            {
                At(new TimeOnly(9, 0)),                                   // just now
                At(new TimeOnly(18, 0), lastFired: Yesterday(18)),        // this evening
                At(new TimeOnly(3, 0)),                                   // slept through it
            }, Now);

        Assert.Equal(3, decisions.Count);
        Assert.Single(decisions, d => d.Verdict == DueVerdict.Run);
        Assert.Single(decisions, d => d.Verdict == DueVerdict.NotDue);
        Assert.Single(decisions, d => d.Verdict == DueVerdict.Missed);
    }
}
