namespace Enactive.Engine.Tests;

using Enactive.Application;
using Xunit;

/// <summary>
/// The command bar's verb chips (CommandVerbs): which verbs, in which order, and what pressing one does to what was
/// typed. The window's and the service's bars offer the same chips, so they come from one list.
/// </summary>
public sealed class CommandVerbsTests
{
    [Fact]
    public void The_verbs_are_the_window_s_in_its_order()
        => Assert.Equal(["Fix", "Build", "Investigate", "Explain", "Refactor"], CommandVerbs.All);

    [Theory]
    [InlineData("", "Fix ")]
    [InlineData("   ", "Fix ")]
    [InlineData(null, "Fix ")]
    [InlineData("the build", "Fix the build")]
    public void Prefix_puts_the_verb_at_the_front(string? input, string expected)
        => Assert.Equal(expected, CommandVerbs.Prefix(input, "Fix"));

    /// <summary>
    /// Run's tooltip says why it is greyed while a run goes, in the window's words - the service's bar hard-coded the idle
    /// line, and once the bar and the run shared a page it said "Run this here" over a greyed button.
    /// </summary>
    [Fact]
    public void The_run_button_says_what_it_does_and_why_it_is_greyed()
    {
        Assert.Equal("Run this here, and watch it.", RunButton.Tip(busy: false));
        Assert.Equal("A run is already in progress. Enactive runs one foreground run at a time, and that run may be in "
                     + "another workspace. Run in background is not held by it.", RunButton.Tip(busy: true));
    }

    /// <summary>
    /// What holds Run is the host's to say: the service's bar is held only by the run open under it, and the window's
    /// "one foreground run at a time" was untrue there.
    /// </summary>
    [Fact]
    public void A_host_gives_its_own_reason_why_run_is_greyed()
    {
        Assert.Equal("A run is already in progress. The run below holds it.", RunButton.Tip(busy: true, "The run below holds it."));
        Assert.Equal("Run this here, and watch it.", RunButton.Tip(busy: false, "The run below holds it."));
    }
}
