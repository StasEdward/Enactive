namespace Enactive.Core.Schedules;

using Enactive.Core.Permissions;

/// <summary>How often a schedule comes round.</summary>
public enum ScheduleRepeat { Once, Daily, Weekly }

/// <summary>
/// What to do about an occurrence the machine slept through.
///
/// <para><see cref="Skip"/> is the default and the right one for anything periodic: a daily
/// hygiene task that missed Tuesday should do Wednesday's work, not Tuesday's. <see cref="RunLate"/>
/// is for the one you actually wanted to happen - and it runs ONCE however many were missed, because
/// waking a laptop after a week must not start seven runs.</para>
/// </summary>
public enum MissedRun { Skip, RunLate }

/// <summary>
/// What a schedule runs. Exactly one of the two, and <see cref="IsValid"/> is how a caller checks.
///
/// <para><b>The two are not symmetrical, on purpose.</b> A template schedule names a template and
/// resolves it AT FIRE TIME, so editing the template changes what tomorrow's run does - which is
/// what a template is for. A past-run schedule carries that run's frozen
/// <c>ResolvedTaskSpec</c> snapshot and never changes, because "run that again" means the thing
/// that ran, not today's version of it.</para>
///
/// <para>The cost of the first is that a template edited into needing a new parameter makes the
/// schedule unresolvable. That must be REPORTED rather than skipped - see SCHEDULER_PLAN step 2 -
/// and it is the reason resolution is not attempted here: this type says what to run, and the thing
/// that runs it is where a failure to resolve becomes visible.</para>
/// </summary>
public sealed record ScheduledWork(
    string? TemplateId,
    IReadOnlyDictionary<string, string> Parameters,
    string? SpecSnapshot)
{
    public static ScheduledWork FromTemplate(string templateId, IReadOnlyDictionary<string, string>? parameters = null)
        => new(templateId, parameters ?? new Dictionary<string, string>(), null);

    /// <param name="snapshot">A past run's <c>ResolvedTaskSpec.Snapshot()</c>.</param>
    public static ScheduledWork FromPastRun(string snapshot)
        => new(null, new Dictionary<string, string>(), snapshot);

    /// <summary>One of the two, never both and never neither.</summary>
    public bool IsValid
        => (TemplateId is { Length: > 0 }) ^ (SpecSnapshot is { Length: > 0 });
}

/// <summary>
/// When a schedule comes round.
///
/// <para><b>A recurring time is a WALL CLOCK plus a zone, never an instant.</b> "Every day at 09:00"
/// means nine o'clock as the person's clock reads it, and a clock that moves twice a year is exactly
/// what a time zone is. Stored as UTC it would silently become 08:00 or 10:00 for half the year, and
/// nobody would connect the drift to the day the clocks changed.</para>
///
/// <para>A one-off keeps an instant instead, because "next Tuesday at 9" was a point in time and
/// there is no second occurrence for a rule to be wrong about.</para>
/// </summary>
/// <param name="TimeZoneId">
/// A Windows or IANA id - .NET accepts either on either platform. Stored rather than assumed,
/// so a schedule made on a laptop that travels still means what it said.
/// </param>
public sealed record ScheduleTiming(
    ScheduleRepeat Repeat,
    TimeOnly AtLocal,
    string TimeZoneId,
    DayOfWeek? OnDay = null,
    DateTimeOffset? OnceAt = null)
{
    public static ScheduleTiming Once(DateTimeOffset at)
        => new(ScheduleRepeat.Once, TimeOnly.FromDateTime(at.DateTime), TimeZoneInfo.Local.Id, null, at);

    public static ScheduleTiming Daily(TimeOnly at, string? zone = null)
        => new(ScheduleRepeat.Daily, at, zone ?? TimeZoneInfo.Local.Id);

    public static ScheduleTiming Weekly(DayOfWeek day, TimeOnly at, string? zone = null)
        => new(ScheduleRepeat.Weekly, at, zone ?? TimeZoneInfo.Local.Id, day);
}

/// <summary>
/// One saved "run this then". Lives outside the workspace - see <see cref="ScheduleStore"/>.
/// </summary>
/// <param name="WorkspaceRoot">
/// The folder this runs in. The store keys authority by the PATH, for
/// <c>ApprovalStore</c>'s reason: a schedule is permission to run commands unattended, and a folder
/// that arrives carrying somebody's id file must not arrive carrying their schedules.
/// </param>
/// <param name="Permissions">
/// What this schedule's runs may do, chosen when it was made. A schedule is an approval given in
/// advance for every future run of it, so the thing that creates one has to show this back in
/// words - a person cannot approve what they were not shown.
/// </param>
public sealed record Schedule(
    Guid Id,
    string WorkspaceRoot,
    string Name,
    ScheduledWork Work,
    ScheduleTiming Timing,
    PermissionPolicy Permissions,
    MissedRun Missed = MissedRun.Skip,
    bool Enabled = true,
    DateTimeOffset? LastFiredAt = null);
