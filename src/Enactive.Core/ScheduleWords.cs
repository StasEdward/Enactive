namespace Enactive.Core.Schedules;

using System.Globalization;
using Enactive.Core.Permissions;

/// <summary>
/// A schedule in the words a person has to read before agreeing to it.
///
/// <para><b>A schedule is an approval given in advance for every future run.</b> That is a stronger
/// thing than approving one command: the person is not there when it happens, they will not be asked
/// again, and it repeats. A person cannot approve what they were not shown, so the window has to say
/// what this will do — in sentences, not in a <c>PermissionPolicy</c> rendered as a struct.</para>
///
/// <para>Here rather than in the window because it is a RULE about what must be said, and a rule in
/// <c>Enactive.App.Ui</c> is a rule no test can reach: that project is a WinExe nothing references.
/// The window's job is to show these strings; deciding what they say is this file's.</para>
///
/// <para>English, like every other string the product shows.</para>
/// </summary>
public static class ScheduleWords
{
    private static readonly CultureInfo Text = CultureInfo.InvariantCulture;

    /// <summary>When it comes round: "Every day at 03:00 (Israel Standard Time)".</summary>
    public static string When(ScheduleTiming timing)
    {
        var at = timing.AtLocal.ToString("HH\\:mm", Text);
        var zone = ZoneName(timing.TimeZoneId);

        return timing.Repeat switch
        {
            ScheduleRepeat.Once when timing.OnceAt is { } once
                => $"Once, on {once.ToLocalTime().ToString("d MMMM yyyy 'at' HH:mm", Text)}",

            // A one-off with no instant is not a time at all. Saying "Once" and stopping would read
            // as a schedule that is set; it is a schedule that cannot fire.
            ScheduleRepeat.Once => "Once — but no date was set, so it will never run",

            ScheduleRepeat.Weekly when timing.OnDay is { } day
                => $"Every {day} at {at} ({zone})",
            ScheduleRepeat.Weekly => $"Every week at {at} ({zone}) — but no day was chosen",

            _ => $"Every day at {at} ({zone})"
        };
    }

    /// <summary>
    /// What it will be allowed to do, in sentences. One line per thing that is true of it, so a
    /// window can show them as a list and nothing has to be summarised away.
    /// </summary>
    public static IReadOnlyList<string> MayDo(PermissionPolicy policy)
    {
        var lines = new List<string>
        {
            policy.Level switch
            {
                PermissionLevel.Observe => "Read files. It cannot change anything.",
                PermissionLevel.Suggest => "Read files and prepare changes, but not apply them.",
                PermissionLevel.Execute => "Edit files in this workspace and run commands.",
                PermissionLevel.Autonomous =>
                    "Edit files, run commands, and install or deploy — without stopping to ask.",
                _ => $"Act at the '{policy.Level}' tier."
            }
        };

        if (policy.Allow.Count > 0 && !policy.Allow.Contains("*"))
            lines.Add($"Only these tools: {string.Join(", ", policy.Allow)}.");

        if (policy.Deny.Count > 0)
            lines.Add($"Never, whatever the tier: {string.Join(", ", policy.Deny)}.");

        // The one a person would not guess, and the one most likely to matter at three in the
        // morning. Unattended + Ask = Deny is the rule the whole design rests on (see
        // UnattendedDecisionHandler); shown as "will be asked about", it would read as the opposite.
        if (policy.AskBefore.Count > 0)
            lines.Add(
                $"Refused, not asked about: {string.Join(", ", policy.AskBefore)} — "
                + "nobody is watching a scheduled run, so anything needing approval is declined.");

        return lines;
    }

    /// <summary>
    /// What happens to an occurrence the machine slept through — the other thing a person is
    /// agreeing to and would not otherwise find out until it surprised them.
    /// </summary>
    public static string IfItWasMissed(MissedRun missed)
        => missed == MissedRun.RunLate
            ? "If the machine was off, it runs as soon as the machine is back."
            : "If the machine was off, that run is skipped — it does not catch up.";

    /// <summary>The next time it is due, in words, or why there is not one.</summary>
    public static string NextRun(Schedule schedule, DateTimeOffset now)
    {
        if (!schedule.Enabled)
            return "Disabled — it will not run until it is turned back on.";

        if (ScheduleClock.Zone(schedule.Timing.TimeZoneId) is null)
            return $"This machine does not know the time zone '{schedule.Timing.TimeZoneId}', "
                 + "so nothing can be timed from it.";

        return ScheduleClock.Next(schedule, now) is { } next
            ? $"Next run: {next.ToLocalTime().ToString("dddd d MMMM 'at' HH:mm", Text)}"
            : "It has no next run — a one-off whose time has passed.";
    }

    /// <summary>
    /// Everything above, in the order a person reads it: what it runs, when, what happens if the
    /// machine was off, and what it is allowed to do. This is the whole of what is being approved,
    /// which is why it is one method rather than four the window might use three of.
    /// </summary>
    public static IReadOnlyList<string> Approval(Schedule schedule, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            When(schedule.Timing),
            NextRun(schedule, now),
            IfItWasMissed(schedule.Missed)
        };

        lines.AddRange(MayDo(schedule.Permissions));
        return lines;
    }

    private static string ZoneName(string id)
        => ScheduleClock.Zone(id) is { } zone ? zone.DisplayName : $"{id} — unknown on this machine";
}
