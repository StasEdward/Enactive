namespace Enactive.Engine.Tests;

using Enactive.Application.Projections;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// Small rules for showing a run that the window kept inside itself and the service shows too - how long it has taken, what
/// a line of a diff does, which icon a step's action carries. Each lives once, in Enactive.Application, so the two do not
/// say the same run two ways.
/// </summary>
public sealed class RunFiguresTests
{
    [Theory]
    [InlineData(5, "5s")]
    [InlineData(123, "2m 3s")]
    [InlineData(3723, "1h 2m 3s")]
    [InlineData(90061, "25h 1m 1s")]
    public void Elapsed_is_said_as_the_window_s_tile_says_it(int seconds, string said)
        => Assert.Equal(said, RunElapsed.Format(TimeSpan.FromSeconds(seconds)));

    // The kind by name: DiffLineKind is internal, and a public test method cannot take it as a parameter.
    [Theory]
    [InlineData("+added line", "Added")]
    [InlineData("-removed line", "Removed")]
    [InlineData(" context", "Context")]
    [InlineData("@@ -1,2 +1,3 @@", "Context")]
    [InlineData("", "Context")]
    public void A_diff_line_is_known_by_its_first_character(string line, string kind)
        => Assert.Equal(Enum.Parse<DiffLineKind>(kind), DiffLines.Kind(line));

    [Fact]
    public void Every_kind_of_step_action_has_its_icon()
    {
        Assert.Equal(["⌘", "📄", "🛠", "🔔", "🛇"], Enum.GetValues<FeedEntryKind>().Select(StepEntries.Icon));
    }
}
