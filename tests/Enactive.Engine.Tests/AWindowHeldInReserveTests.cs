namespace Enactive.Engine.Tests;

using System.Text.RegularExpressions;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// A step works at one prompt size and may never exceed another, and the two are set apart.
///
/// <para>Before this, the working size was a share of the window. Handover fired at
/// <c>HandoverAtPercent</c> of <c>ContextWindowTokens</c>, and an emergency trim cut back to half
/// of the window. So the two could not be tuned independently: declaring a model's real, larger
/// window grew the working prompt with it. 75% of 65,536 is 49,152 tokens; 75% of 131,072 is
/// 98,304. The whole point of the larger window - that it is almost never used, and is there when
/// a step genuinely needs it - was lost the moment it was declared.</para>
///
/// <para>A local model is steadier with a shorter prompt, and a changed history is cheaper to
/// re-read when there is less of it. So <c>WorkingContextTokens</c> names the size to work at, and
/// the window stays what it always was: the line that may not be crossed.</para>
/// </summary>
public sealed class AWindowHeldInReserveTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"a long job"}""";

    private static Turn Read(EngineFixture fx, int i)
    {
        fx.Write($"page{i}.md", $"page {i}");
        return Turn.Calls1("read_file", $$"""{"path":"page{{i}}.md"}""", $"r{i}");
    }

    private static int Count(IEnumerable<WorkEvent> events, string text)
        => events.Count(e => e.Kind == EventKind.ContextTrimmed
                             && e.Summary.Contains(text, StringComparison.Ordinal));

    /// <summary>
    /// THE ONE THAT MATTERS. A prompt of 9,000 tokens is under a quarter of a 40,000 window - far
    /// below the 75% share that would hand it over - and the step is handed over anyway, because it
    /// is past the 8,000 it was set to work at.
    /// </summary>
    [Fact]
    public async Task A_step_is_handed_over_at_the_size_it_works_at_not_at_a_share_of_the_window()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Read(fx, 1).Reporting(prompt: 3_000),
            Read(fx, 2).Reporting(prompt: 9_000),
            Turn.Says("# Note to self - read pages 1 and 2; pages 3 and 4 are left."),
            Read(fx, 3),
            Read(fx, 4),
            Turn.Says("All four pages read.")) { Window = 40_000, HandoverAt = 75, Working = 8_000 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "a long job");

        Assert.Equal(1, Count(events, "fresh conversation and continuing"));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>
    /// The same prompt, the same window, and no working size: 9,000 of 40,000 is 22%, under the
    /// 75% share, so nothing is handed over. This is the behaviour the setting exists to change,
    /// and without it nothing changes - the default is exactly what the engine did before.
    /// </summary>
    [Fact]
    public async Task Without_a_working_size_the_share_of_the_window_decides_as_before()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Read(fx, 1).Reporting(prompt: 3_000),
            Read(fx, 2).Reporting(prompt: 9_000),
            Read(fx, 3),
            Turn.Says("All three pages read.")) { Window = 40_000, HandoverAt = 75 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "a long job");

        Assert.Equal(0, Count(events, "fresh conversation"));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>
    /// Said as what it is. Past the working size, the rest of the window is not "needed to write in"
    /// - it is reserve, deliberately unused - and a person reading the run should be told which line
    /// was crossed.
    /// </summary>
    [Fact]
    public async Task The_handover_says_the_rest_of_the_window_is_held_in_reserve()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Read(fx, 1).Reporting(prompt: 9_000),
            Turn.Says("# Note to self - read page 1."),
            Read(fx, 2),
            Turn.Says("Both pages read.")) { Window = 40_000, Working = 8_000 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "a long job");

        var handover = Assert.Single(events, e => e.Kind == EventKind.ContextTrimmed
                                                  && e.Summary.Contains("fresh conversation and continuing", StringComparison.Ordinal));
        Assert.Contains("set to work at", handover.Summary, StringComparison.Ordinal);
        Assert.Contains("held in reserve", handover.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each setting is somebody saying "no further than this", so when both are set the nearer
    /// one wins. Here the share of the window (40% of 20,000 = 8,000) is nearer than the working
    /// size (12,000), and the step is handed over at 8,000 - in the share's own words.
    /// </summary>
    [Fact]
    public async Task When_both_are_set_the_nearer_line_wins()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Read(fx, 1).Reporting(prompt: 8_500),
            Turn.Says("# Note to self - read page 1."),
            Read(fx, 2),
            Turn.Says("Both pages read.")) { Window = 20_000, HandoverAt = 40, Working = 12_000 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "a long job");

        var handover = Assert.Single(events, e => e.Kind == EventKind.ContextTrimmed
                                                  && e.Summary.Contains("fresh conversation and continuing", StringComparison.Ordinal));
        Assert.Contains("tokens this model was given", handover.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("held in reserve", handover.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A provider that states no window - every cloud one here - still has a size its steps can
    /// work at, as long as it reports what a prompt cost. There is no reserve to speak of, and
    /// nothing says there is.
    /// </summary>
    [Fact]
    public async Task A_working_size_applies_without_a_stated_window()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Read(fx, 1).Reporting(prompt: 9_000),
            Turn.Says("# Note to self - read page 1."),
            Read(fx, 2),
            Turn.Says("Both pages read.")) { Working = 8_000 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "a long job");

        var handover = Assert.Single(events, e => e.Kind == EventKind.ContextTrimmed
                                                  && e.Summary.Contains("fresh conversation and continuing", StringComparison.Ordinal));
        Assert.Contains("set to work at", handover.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("reserve", handover.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A working size past the window is held to the line the emergency trim works to - the window
    /// less the answer's reserve - not to the whole window, and never left at what was asked for.
    ///
    /// <para>Past that line a working size could never be reached, because the trim fires first. And
    /// held to the whole window instead, a handover would fire with no room left to write its note:
    /// the first draft of this clamp did exactly that, and this test caught it. At a 9,000 window the
    /// reserve is an eighth, 1,125, so the working size becomes 7,875 however large it was set.</para>
    ///
    /// <para>What this checks is the NUMBER the step is held to. A prompt past it on a window this
    /// small is past the trim line too, so the run cannot finish cleanly; that is the window being
    /// too small, which is already its own message.</para>
    /// </summary>
    [Fact]
    public async Task A_working_size_past_the_window_is_held_to_the_emergency_line()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Read(fx, 1).Reporting(prompt: 9_500),
            Turn.Says("# Note to self - read page 1."),
            Read(fx, 2),
            Turn.Says("Both pages read.")) { Window = 9_000, Working = 50_000 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "a long job");

        var said = string.Join(" | ", events.Where(e => e.Summary.Contains("set to work at", StringComparison.Ordinal))
                                            .Select(e => e.Summary));
        Assert.Contains("past the 7875 this model is set to work at", said, StringComparison.Ordinal);
        Assert.DoesNotContain("50000", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The emergency path. With no prompt reported there is nothing to hand over by, so the prompt
    /// grows until it reaches the window's own line and is trimmed. Before, the trim cut back to
    /// half the WINDOW; with a working size it cuts to half of THAT, back to where the step is meant
    /// to live rather than into the reserve.
    /// </summary>
    [Fact]
    public async Task An_emergency_trim_cuts_back_to_half_the_working_size_not_half_the_window()
    {
        var withWorking = await TokensLeftAfterTrim(working: 3_000);
        var withoutWorking = await TokensLeftAfterTrim(working: null);

        Assert.True(withWorking < withoutWorking,
            $"half the working size should cut deeper than half the window: {withWorking} vs {withoutWorking}");
    }

    private static async Task<int> TokensLeftAfterTrim(int? working)
    {
        using var fx = new EngineFixture();
        var payload = new string('y', 3_000);

        var turns = new List<Turn> { Turn.Says(QuickAction) };
        for (var i = 0; i < 24; i++)
            turns.Add(Turn.Calls1("write_file", $$"""{"path":"part{{i}}.txt","content":"{{payload}}"}""", $"w{i}"));
        turns.Add(Turn.Says("Done."));

        var provider = new FakeChatProvider(turns.ToArray()) { Window = 16_000, Working = working };
        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write parts");

        var trim = events.First(e => e.Kind == EventKind.ContextTrimmed
                                     && e.Summary.Contains("dropped the contents", StringComparison.Ordinal));
        var match = Regex.Match(trim.Summary, @"about (\d+) of \d+ tokens now");
        Assert.True(match.Success, trim.Summary);
        return int.Parse(match.Groups[1].Value);
    }
}
