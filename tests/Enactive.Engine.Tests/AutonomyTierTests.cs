namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Permissions;
using Xunit;

/// <summary>
/// The autonomy slider, as a policy - and the same policy wherever a run is started from.
///
/// <para>The mapping was a private method on the main window. No test could reach it, and the
/// console host had written its own version: one hard-coded policy, roughly tier 2, belonging to no
/// slider position. So a run started from a command line was not running under any tier the app can
/// be set to, which makes anything checked there evidence about the console rather than about the
/// product.</para>
/// </summary>
public sealed class AutonomyTierTests
{
    [Theory]
    [InlineData(0, PermissionLevel.Observe)]
    [InlineData(1, PermissionLevel.Suggest)]
    [InlineData(2, PermissionLevel.Execute)]
    [InlineData(3, PermissionLevel.Autonomous)]
    public void Each_slider_position_is_its_tier(int level, PermissionLevel expected)
        => Assert.Equal(expected, AutonomyTiers.PolicyFor(level).Level);

    /// <summary>
    /// Execute is the tier that stops at a command line, and the only one that asks about anything.
    /// It is where the product is meant to be lived in, so it is worth pinning by name rather than
    /// leaving to the shape of a switch.
    /// </summary>
    [Fact]
    public void Execute_edits_freely_and_stops_at_a_command_line()
    {
        var policy = AutonomyTiers.PolicyFor(2);

        Assert.Contains("run_command", policy.AskBefore, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("run_powershell", policy.AskBefore, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("*", policy.Allow);
    }

    /// <summary>
    /// And Autonomous asks about nothing. Said out loud because it is the premise the remote shell
    /// rule turns on: a handler only ever sees what the POLICY decided to ask about, so at this
    /// tier a rule enforced in a decision handler is not enforced at all. That is not a bug here -
    /// it is why RemotePolicy exists.
    /// </summary>
    [Fact]
    public void Autonomous_asks_about_nothing()
        => Assert.Empty(AutonomyTiers.PolicyFor(3).AskBefore);

    /// <summary>A level outside the tiers is the nearest one, in one place for every host.</summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(7, 3)]
    public void A_level_outside_the_tiers_is_the_nearest_one(int level, int expected)
        => Assert.Equal(expected, AutonomyTiers.Clamp(level));

    /// <summary>
    /// A negative level is the LOWEST tier. It was the top one - anything the switch had no case for was - so a -1 from
    /// a damaged record that reached the policy without being clamped ran everything without asking.
    /// </summary>
    [Fact]
    public void A_negative_level_is_the_lowest_tier_not_the_top()
    {
        Assert.Equal(PermissionLevel.Observe, AutonomyTiers.PolicyFor(-1).Level);
        Assert.StartsWith("Observe", AutonomyTiers.Describe(-1));
        Assert.Equal(PermissionLevel.Autonomous, AutonomyTiers.PolicyFor(7).Level);
    }

    /// <summary>
    /// The range is written in AutonomyTiers only. The window clamped a workspace's level with a literal 3 and its slider
    /// stopped at a literal 3, beside a composer that asked AutonomyTiers - so a tier added there would have been cut
    /// off in the window alone.
    /// </summary>
    [Fact]
    public void The_tiers_range_is_written_in_one_place()
    {
        var src = Path.Combine(TestRepository.Root, "src");
        var clamps = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && Path.GetFileName(f) != "AutonomyTiers.cs")
            .SelectMany(f => File.ReadLines(f).Select(line => (f, line)))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.line, @"Math\.Clamp\([^;]*[Aa]utonomy"))
            .Select(x => $"{Path.GetFileName(x.f)}: {x.line.Trim()}")
            .ToArray();
        Assert.True(clamps.Length == 0, "Clamped outside AutonomyTiers:\n" + string.Join("\n", clamps));

        var window = File.ReadAllText(Path.Combine(src, "Enactive.App.Ui", "MainWindow.axaml"));
        var slider = System.Text.RegularExpressions.Regex.Match(window, @"<Slider\b[^>]*AutonomyLevel[^>]*>").Value;
        Assert.Contains("Maximum=\"{Binding HighestAutonomyLevel}\"", slider);
    }

    [Theory]
    [InlineData("observe", 0)]
    [InlineData("Suggest", 1)]
    [InlineData("EXECUTE", 2)]
    [InlineData("autonomous", 3)]
    [InlineData("0", 0)]
    [InlineData("3", 3)]
    public void A_tier_can_be_named_or_numbered(string text, int expected)
        => Assert.Equal(expected, AutonomyTiers.Parse(text));

    /// <summary>
    /// A typo is refused, not rounded. Reading "excute" as Autonomous would hand a run more freedom
    /// than anybody asked for; reading it as Observe would produce a run that does nothing and looks
    /// broken. Neither answers what was typed.
    /// </summary>
    [Theory]
    [InlineData("excute")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData(null)]
    public void Anything_else_is_not_a_tier(string? text)
        => Assert.Null(AutonomyTiers.Parse(text));
}
