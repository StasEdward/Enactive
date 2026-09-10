namespace Enactive.Core.Schedules;

/// <summary>
/// When a schedule next comes round.
///
/// <para>Separate from <see cref="Schedule"/> because it is the only part with a rule that can be
/// wrong, and it is wrong on exactly two days a year. A wall clock is not a duration: adding 24
/// hours to yesterday's instant gives 08:00 or 10:00 on the days the clocks move, and a daily job
/// that quietly slides an hour twice a year is the kind of defect nobody attributes to the right
/// cause.</para>
///
/// <para>So each occurrence is built from the DATE and the wall time, and converted through the
/// zone - which is what makes "every day at nine" mean nine o'clock.</para>
/// </summary>
public static class ScheduleClock
{
    /// <summary>
    /// The first time this schedule is due strictly after <paramref name="after"/>, or null when
    /// there is none - a disabled schedule, or a one-off that has passed.
    /// </summary>
    public static DateTimeOffset? Next(Schedule schedule, DateTimeOffset after)
    {
        if (!schedule.Enabled)
            return null;

        var timing = schedule.Timing;

        if (timing.Repeat == ScheduleRepeat.Once)
            return timing.OnceAt is { } once && once > after ? once : null;

        if (Zone(timing.TimeZoneId) is not { } zone)
            return null;

        // Walked forward a day at a time from the day BEFORE, so an occurrence earlier today that
        // has not happened yet is still found. 400 days covers a weekly schedule and any amount of
        // clock-shifting; running out means the timing is nonsense rather than far away.
        var local = TimeZoneInfo.ConvertTime(after, zone);
        for (var day = 0; day < 400; day++)
        {
            var date = DateOnly.FromDateTime(local.Date).AddDays(day);

            if (timing.Repeat == ScheduleRepeat.Weekly
                && timing.OnDay is { } wanted
                && date.DayOfWeek != wanted)
                continue;

            if (Occurrence(date, timing.AtLocal, zone) is { } at && at > after)
                return at;
        }

        return null;
    }

    /// <summary>
    /// One occurrence: this date, at this wall time, in this zone - resolving the two ways a local
    /// time can fail to be a single instant.
    ///
    /// <para><b>The hour that does not exist.</b> When the clocks go forward, 02:30 is simply not a
    /// time that day. Skipping is the tempting answer and the wrong one: a daily job that silently
    /// does not run one day a year is a schedule that looks configured and is not, which is the
    /// failure this codebase keeps naming. It runs at the moment the clock reaches the far side of
    /// the gap instead - late by the length of the gap, and it happened.</para>
    ///
    /// <para><b>The hour that happens twice.</b> When the clocks go back, 01:30 comes round twice.
    /// The FIRST one is used, so the job runs once and at the earlier of the two - the alternative
    /// is either running twice, or running an hour later than every other day of the year.</para>
    /// </summary>
    private static DateTimeOffset? Occurrence(DateOnly date, TimeOnly at, TimeZoneInfo zone)
    {
        var wanted = date.ToDateTime(at, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(wanted))
        {
            // Step forward a minute at a time to the end of the gap. A gap is an hour in every zone
            // that has one, and half an hour in the few that are odd; a minute at a time needs no
            // opinion about which.
            for (var minutes = 1; minutes <= 24 * 60; minutes++)
            {
                var shifted = wanted.AddMinutes(minutes);
                if (!zone.IsInvalidTime(shifted))
                    return new DateTimeOffset(shifted, zone.GetUtcOffset(shifted));
            }

            return null;
        }

        if (zone.IsAmbiguousTime(wanted))
        {
            // The offsets come back in an order the documentation does not promise, so the earlier
            // instant is chosen by comparing them rather than by taking one of them by index.
            var offsets = zone.GetAmbiguousTimeOffsets(wanted);
            var first = offsets[0];
            foreach (var offset in offsets)
                if (offset > first)
                    first = offset;   // a LARGER offset is an EARLIER instant for the same wall time

            return new DateTimeOffset(wanted, first);
        }

        return new DateTimeOffset(wanted, zone.GetUtcOffset(wanted));
    }

    /// <summary>
    /// The zone, or null when this machine has never heard of it.
    ///
    /// <para>Null rather than an exception: a schedule made on a machine with a zone database this
    /// one lacks is a schedule that cannot be timed, and the caller reports that. Throwing from a
    /// tick that is walking every schedule would take the others down with it.</para>
    /// </summary>
    public static TimeZoneInfo? Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
}
