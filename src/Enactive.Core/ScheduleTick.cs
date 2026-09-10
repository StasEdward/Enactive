namespace Enactive.Core.Schedules;

/// <summary>What a tick decided about one schedule.</summary>
public enum DueVerdict
{
    /// <summary>Its time has come and nothing is in the way.</summary>
    Run,

    /// <summary>Not yet. The ordinary answer, and the only one nobody needs telling about.</summary>
    NotDue,

    /// <summary>Switched off by a person. Not the same as skipped, and not worth reporting.</summary>
    Off,

    /// <summary>An occurrence went by while the machine was elsewhere, and this one is not being made up.</summary>
    Missed,

    /// <summary>The previous run of this same schedule is still going.</summary>
    Busy,

    /// <summary>It cannot be timed at all - an unknown zone, or work that names nothing.</summary>
    Unschedulable
}

/// <summary>
/// One schedule's answer, with the sentence a person would need.
/// </summary>
/// <param name="Why">
/// Plain words, because most of these verdicts are a run NOT happening, and a run that does not
/// happen is invisible unless something says so. "Skipped: due 03:00, the machine was asleep" is a
/// different fact from silence, and the difference is the whole reason a person trusts a schedule.
/// </param>
public sealed record ScheduleDecision(
    Schedule Schedule, DueVerdict Verdict, DateTimeOffset? Occurrence, string Why)
{
    public bool ShouldRun => Verdict == DueVerdict.Run;

    /// <summary>Whether this is worth telling somebody about. "Not yet" never is.</summary>
    public bool WorthReporting => Verdict is DueVerdict.Missed or DueVerdict.Busy or DueVerdict.Unschedulable;
}

/// <summary>
/// What is due, asked of a list of schedules at a moment.
///
/// <para>A pure function of its inputs: the schedules, the time, and a way to ask whether one is
/// already running. It starts nothing. That is what makes the rules below testable at all — the
/// alternative is a timer somewhere that can only be checked by waiting for three in the morning,
/// which means it never gets checked.</para>
/// </summary>
public static class ScheduleTick
{
    /// <summary>
    /// How late an occurrence may be and still count as "now".
    ///
    /// <para>It exists because a tick is not continuous: something wakes every few minutes, and an
    /// occurrence at 03:00 seen at 03:04 is that occurrence, not a missed one. It must be
    /// comfortably longer than the gap between ticks and comfortably shorter than the gap between
    /// occurrences — fifteen minutes is both for anything a person schedules by day and hour.</para>
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    /// <param name="isRunning">
    /// Whether a previous run of this schedule is still going. Passed in rather than looked up, so
    /// the overlap rule can be tested without a second process to be overlapped with.
    /// </param>
    public static IReadOnlyList<ScheduleDecision> Decide(
        IEnumerable<Schedule> schedules, DateTimeOffset now, Func<Guid, bool>? isRunning = null)
        => schedules.Select(s => Decide(s, now, isRunning)).ToArray();

    /// <summary>
    /// The one to run now: the most overdue of those that should run, or null when none should.
    ///
    /// <para>Most overdue first, because if only one can go this tick it should be the one that has
    /// been waiting longest rather than whichever the file happened to list first. Here rather than
    /// at the call site because there are two call sites now - one workspace, and every workspace -
    /// and an ordering written twice is an ordering that will differ after the next edit.</para>
    /// </summary>
    public static ScheduleDecision? FirstDue(IEnumerable<ScheduleDecision> decisions)
        => decisions.Where(d => d.ShouldRun).OrderBy(d => d.Occurrence).FirstOrDefault();

    public static ScheduleDecision Decide(Schedule schedule, DateTimeOffset now, Func<Guid, bool>? isRunning = null)
    {
        if (!schedule.Enabled)
            return new(schedule, DueVerdict.Off, null, "switched off");

        if (!schedule.Work.IsValid)
            return new(schedule, DueVerdict.Unschedulable, null,
                       "this schedule does not say what to run - it names neither a template nor a past run");

        if (ScheduleClock.Zone(schedule.Timing.TimeZoneId) is null
            && schedule.Timing.Repeat != ScheduleRepeat.Once)
            return new(schedule, DueVerdict.Unschedulable, null,
                       $"this machine has no time zone called '{schedule.Timing.TimeZoneId}', so there is no "
                       + "way to say when local time this should run");

        // From the last firing, so an occurrence is considered exactly once - and from CREATION
        // when there has not been one, so the first occurrence is found even if it has already gone
        // by. Anchoring a never-fired schedule at `now` hides an occurrence earlier today, which is
        // the missed-run question answered wrongly and in silence.
        var since = schedule.LastFiredAt ?? schedule.CreatedAt;

        // Asked BACKWARDS from now, so a backlog collapses to its most recent member. Walking
        // forward from the last firing answers with the OLDEST occurrence nobody ran, and a laptop
        // opened after a week away would then be told a schedule was due seven days ago - and a
        // run that made itself up would be making up last Tuesday's work rather than today's.
        if (ScheduleClock.MostRecent(schedule, now, since) is not { } due)
            return ScheduleClock.Next(schedule, now) is { } next
                ? new(schedule, DueVerdict.NotDue, next, $"next at {Local(schedule, next)}")
                : new(schedule, DueVerdict.NotDue, null, "no further occurrence");

        // It is in the past. Whether that means "now" or "missed" is the grace window, and whether
        // a missed one is made up is the person's choice, made when the schedule was created.
        var late = now - due;
        if (late > Grace && schedule.Missed == MissedRun.Skip)
            return new(schedule, DueVerdict.Missed, due,
                       $"due at {Local(schedule, due)} and missed by {Describe(late)} - this schedule skips "
                       + "what it missed, so the next occurrence is the one that will run");

        // Asked LAST, so a schedule that is busy is reported as busy rather than as missed: the two
        // look alike from the outside and mean opposite things about whether the work is happening.
        if (isRunning is not null && isRunning(schedule.Id))
            return new(schedule, DueVerdict.Busy, due,
                       $"due at {Local(schedule, due)}, but the previous run has not finished - two copies of "
                       + "one task in one workspace is not a busier day, it is two agents editing the same files");

        return new(schedule, DueVerdict.Run, due,
                   late > Grace
                       ? $"due at {Local(schedule, due)}, {Describe(late)} ago - running it late, once"
                       : $"due at {Local(schedule, due)}");
    }

    /// <summary>The time as the schedule's own clock reads it, which is how the person set it.</summary>
    private static string Local(Schedule schedule, DateTimeOffset at)
        => ScheduleClock.Zone(schedule.Timing.TimeZoneId) is { } zone
            ? TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm")
            : at.ToString("yyyy-MM-dd HH:mm zzz");

    private static string Describe(TimeSpan late)
        => late.TotalMinutes < 90 ? $"{(int)late.TotalMinutes} minutes"
         : late.TotalHours   < 48 ? $"{(int)late.TotalHours} hours"
         : $"{(int)late.TotalDays} days";
}
