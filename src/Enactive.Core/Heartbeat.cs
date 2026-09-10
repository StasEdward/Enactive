namespace Enactive.Core.Schedules;

using System.Globalization;

/// <summary>Whether anything is actually asking the schedules what is due.</summary>
/// <param name="LastSeen">When the tick last ran, or null when it never has on this machine.</param>
/// <param name="Words">Empty when it is healthy. Otherwise the sentence to put in front of a person.</param>
public sealed record TickHealth(DateTimeOffset? LastSeen, bool IsHealthy, string Words);

/// <summary>
/// The tick's pulse: a timestamp written every time the runner is woken.
///
/// <para><b>Why this exists.</b> On 2026-09-10 two schedules were created in the window, saved
/// correctly, and did not run. Nothing was wrong with either of them: no Windows scheduled task had
/// ever been registered, so nothing on the machine had ever asked. The window meanwhile showed
/// "Next run: ..." with complete confidence about a run that could not happen — a thing that looks
/// configured and enforces nothing, which is the failure this project keeps coming back to.</para>
///
/// <para><b>Why a pulse and not "is the task registered".</b> A registered task can be disabled,
/// can be pointed at a binary that has moved, can fail on every fire. Each of those answers "yes,
/// registered" and none of them runs anything. What a person needs to know is whether the schedules
/// are BEING ASKED, and only the asking can report that.</para>
///
/// <para>Beside the schedules in <c>%APPDATA%/Enactive</c>, and never inside a workspace: one tick
/// serves every workspace, and a file the agent can write is a file a run could forge.</para>
/// </summary>
public static class Heartbeat
{
    /// <summary>
    /// How many ticks may be missed before saying so. Three, so one slow or skipped wake-up is not
    /// reported as a broken scheduler - the alarm has to mean something when it appears.
    /// </summary>
    public const int MissesAllowed = 3;

    public static string DefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Enactive", "tick.txt");

    /// <summary>Called by the runner every time it is woken, before it decides anything.</summary>
    public static void Stamp(DateTimeOffset at, string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);

            // Written whole and moved into place, like every other state file here: a tick
            // interrupted mid-write must not leave something that reads as "never ran".
            var temp = file + ".tmp";
            File.WriteAllText(temp, at.ToString("o", CultureInfo.InvariantCulture));
            File.Move(temp, file, overwrite: true);
        }
        catch { /* a missed pulse is a nuisance; failing the run over it is not */ }
    }

    /// <summary>When the tick last ran, or null when it never has - or the file cannot be read.</summary>
    public static DateTimeOffset? LastSeen(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath();
            if (!File.Exists(file))
                return null;

            return DateTimeOffset.TryParse(
                File.ReadAllText(file), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                ? at
                : null;
        }
        catch { return null; }
    }

    /// <param name="expected">How often the tick is supposed to run.</param>
    public static TickHealth Check(DateTimeOffset? lastSeen, DateTimeOffset now, TimeSpan expected)
    {
        if (lastSeen is not { } seen)
            return new TickHealth(
                null, false,
                "Nothing is waking these schedules on this machine, so none of them will run. "
                + "They are saved and will start being honoured as soon as something is.");

        var silence = now - seen;
        if (silence > expected * MissesAllowed)
            return new TickHealth(
                seen, false,
                $"Nothing has checked these schedules since {seen.ToLocalTime():yyyy-MM-dd HH:mm} "
                + $"({Describe(silence)} ago). They are not running.");

        return new TickHealth(seen, true, "");
    }

    private static string Describe(TimeSpan span)
        => span.TotalMinutes < 90 ? $"{(int)span.TotalMinutes} minutes"
         : span.TotalHours < 48 ? $"{(int)span.TotalHours} hours"
         : $"{(int)span.TotalDays} days";
}
