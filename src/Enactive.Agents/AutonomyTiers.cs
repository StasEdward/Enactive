namespace Enactive.Agents;

using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// The autonomy slider, as a permission policy.
///
/// <para>Four positions, from watching to acting unasked. The mapping lived as a private method on
/// the main window, which meant two things: no test could reach it, and any other host had to write
/// the mapping again. The console host had written its own - one policy, hard-coded, roughly tier 2
/// - so a run started from a command line was not running under any tier the app can be set to, and
/// a check performed there said nothing about what a person's machine would do.</para>
///
/// <para>Which is the whole point of moving it: a scenario run from a console is only evidence
/// about the product if the console and the window agree on what "Execute" means.</para>
///
/// <para>The shell setting is deliberately NOT here. It is a separate control, applied on top of
/// whichever tier is chosen, because a shell is a different kind of permission - every other tool
/// is asked for one named action against a path this engine resolves and checks, and a shell is
/// handed a command line. Folding it into the tier would make "Autonomous" mean two things.</para>
/// </summary>
public static class AutonomyTiers
{
    /// <summary>The slider positions, lowest first. The index IS the position.</summary>
    public static readonly IReadOnlyList<string> Names = ["observe", "suggest", "execute", "autonomous"];

    /// <summary>The top position - the slider's maximum, and where a level above it is brought down to.</summary>
    public static int Highest => Names.Count - 1;

    /// <summary>
    /// A level brought into the tiers: below the lowest is the lowest, above the top is the top. The one place the
    /// range is applied - the window setting its slider from a workspace's record, and the composer starting a run,
    /// each wrote it out, one with a literal 3, so a tier added to <see cref="Names"/> would have been cut off in one
    /// and not the other.
    /// </summary>
    public static int Clamp(int level) => Math.Clamp(level, 0, Highest);

    /// <summary>
    /// The policy for a slider position; a level outside the tiers is the nearest one (<see cref="Clamp"/>).
    /// <see cref="Parse"/> is where a STRING is refused rather than rounded.
    ///
    /// <para>Anything the switch had no case for was the TOP tier, a negative level included - safe only because
    /// every caller passed a slider value. A -1 from a damaged record is not a request to run everything without
    /// asking; it is now the lowest tier.</para>
    /// </summary>
    public static PermissionPolicy PolicyFor(int level) => Clamp(level) switch
    {
        0 => new PermissionPolicy(PermissionLevel.Observe, ["*"], []),
        1 => new PermissionPolicy(PermissionLevel.Suggest, ["*"], []),

        // Execute edits files freely and stops at a command line. This is the tier the product is
        // meant to be lived in, and the only one whose AskBefore list is not empty. Every tool that runs something on this
        // machine (ShellTools.All): run_tests runs the workspace's own code, and asking before run_command alone left it a
        // way to run that code without asking (2026-10-09).
        2 => new PermissionPolicy(
                 PermissionLevel.Execute, ["*"], [.. ShellTools.All]),

        _ => new PermissionPolicy(PermissionLevel.Autonomous, ["*"], [])
    };

    /// <summary>
    /// The tier in words, as a run's record keeps it - a number alone means nothing to whoever reads that run
    /// in six months. Here rather than in the window, so a run started from the console records the same words.
    /// </summary>
    public static string Describe(int level) => Clamp(level) switch
    {
        0 => "Observe — read only, asks before changes",
        1 => "Suggest — prepares changes, asks to apply",
        2 => "Execute — edits freely, asks before run_command",
        _ => "Autonomous — runs everything without asking"
    };

    /// <summary>
    /// A tier named on a command line, by name or by its number.
    ///
    /// <para>Null for anything else, so a caller can refuse the invocation. A typo must not become
    /// a tier: silently reading "excute" as Autonomous would hand a run more freedom than the person
    /// asked for, and reading it as Observe would produce a run that does nothing and looks broken.
    /// Neither is an answer to what was typed.</para>
    /// </summary>
    public static int? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();

        for (var i = 0; i < Names.Count; i++)
            if (string.Equals(Names[i], trimmed, StringComparison.OrdinalIgnoreCase))
                return i;

        return int.TryParse(trimmed, out var number) && number >= 0 && number < Names.Count
            ? number
            : null;
    }
}
