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
