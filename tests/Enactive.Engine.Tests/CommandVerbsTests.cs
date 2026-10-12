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
}
