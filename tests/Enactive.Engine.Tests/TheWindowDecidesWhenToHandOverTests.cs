namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Xunit;

/// <summary>
/// For a provider configured with <c>HandoverAtPercent</c>, that states its window and reports what
/// each prompt cost, the step is handed over when the conversation fills that share of the window -
/// not after sixty turns. Unconfigured, the turn count decides, as it always has.
///
/// <para><b>Measured 2026-09-24, run ed72d64a, a local model with a 131,072-token window.</b> The
/// turn count got it wrong both ways in a single run:</para>
///
/// <code>
/// step 1   60 turns at 68K (52%)   handed over anyway: 112 s of note-writing, cache thrown away
/// step 2   40 turns at 117K (89%)  not handed over - began a 51,000-character append, and the
///                                  window ran out 13,772 tokens and four minutes later
/// </code>
///
/// <para>The first handover was not needed and cost two minutes; the second was needed and never
/// came. Sixty turns was always a proxy for "this conversation has got long", and a provider that
/// states its window and reports its prompt says how long directly.</para>
///
/// <para>It is a SETTING, per provider, and off by default. The 75 that fitted the model above is
/// a number about that model, and an engine meant for any model has no business holding it as a
/// constant.</para>
/// </summary>
public sealed class TheWindowDecidesWhenToHandOverTests
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
    /// THE ONE THAT MATTERS. The prompt passes 75% of the window on the third turn, and the step is
    /// handed over there - on turn three, not sixty - and said so BEFORE the note is written.
    /// </summary>
    [Fact]
    public async Task A_conversation_three_quarters_full_is_handed_over_whatever_the_turn()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Read(fx, 1).Reporting(prompt: 3_000),
            Read(fx, 2).Reporting(prompt: 8_000),          // 80% of 10,000: over the line
            Turn.Says("# Note to self - read pages 1 and 2; pages 3 and 4 are left."),
            Read(fx, 3),
            Read(fx, 4),
            Turn.Says("All four pages read.")) { Window = 10_000, HandoverAt = 75 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "a long job");

        var announced = events.FindIndex(e => e.Kind == EventKind.ContextTrimmed
                                              && e.Summary.Contains("Writing notes to carry", StringComparison.Ordinal));
        var done = events.FindIndex(e => e.Kind == EventKind.ContextTrimmed
                                         && e.Summary.Contains("Carrying its own notes into a fresh conversation", StringComparison.Ordinal));

        Assert.True(announced >= 0, events.Text());
        Assert.True(done > announced, "the handover must be announced before its note, not after");
        Assert.Contains("tokens this model was given", events[done].Summary, StringComparison.Ordinal);

        // Once, and only once. The measurement belonged to the conversation just thrown away;
        // kept, it would read the new short one as still full and hand it over again at once.
        Assert.Equal(1, Count(events, "Carrying its own notes into a fresh conversation"));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>
    /// THE OTHER HALF. With the window stated and the prompt small, sixty turns is no reason to
    /// throw a warm conversation away - which is what cost step 1 two minutes.
    /// </summary>
    [Fact]
    public async Task Sixty_turns_in_a_roomy_window_are_not_a_reason_to_hand_over()
    {
        using var fx = new EngineFixture();

        var turns = new List<Turn> { Turn.Says(QuickAction) };
        for (var i = 0; i < 64; i++)
            turns.Add(Read(fx, i).Reporting(prompt: 1_000));
        turns.Add(Turn.Says("All read."));

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(turns.ToArray()) { Window = 1_000_000, HandoverAt = 75 }, EngineFixture.Role("developer")),
            "a long job");

        // Not even ATTEMPTED. Under the turn rule a handover is tried at sixty and, with a tool call
        // where the note should be, gives up saying so - which a check for a finished handover alone
        // would wave through, as the first draft of this test did.
        Assert.Equal(0, Count(events, "fresh conversation"));
        Assert.DoesNotContain(events, e => e.Summary.Contains("could not summarise", StringComparison.Ordinal));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>
    /// THE BOUNDARY. A provider that states a window but never reports what a prompt cost gives
    /// nothing to measure, and falls back to the turn count - the only length signal left.
    /// </summary>
    [Fact]
    public async Task A_window_with_no_measurement_still_hands_over_by_turns()
    {
        using var fx = new EngineFixture();

        var turns = new List<Turn> { Turn.Says(QuickAction) };
        for (var i = 0; i < 60; i++)
            turns.Add(Read(fx, i));
        turns.Add(Turn.Says("# Note to self - sixty pages read."));
        for (var i = 60; i < 63; i++)
            turns.Add(Read(fx, i));
        turns.Add(Turn.Says("All read."));

        var events = await fx.RunAsync(
            fx.Build(new FakeChatProvider(turns.ToArray()) { Window = 1_000_000 }, EngineFixture.Role("developer")),
            "a long job");

        Assert.Equal(1, Count(events, "This step has run 60 turns. Carrying its own notes"));
    }

    /// <summary>
    /// THE DEFAULT. A window stated and every prompt reported, but no HandoverAtPercent configured:
    /// nothing changes from how the engine has always worked - sixty turns.
    /// </summary>
    [Fact]
    public async Task Without_the_setting_the_turn_count_still_decides()
    {
        using var fx = new EngineFixture();

        var turns = new List<Turn> { Turn.Says(QuickAction) };
        for (var i = 0; i < 60; i++)
            turns.Add(Read(fx, i).Reporting(prompt: 900_000));    // 90% of the window, every turn
        turns.Add(Turn.Says("# Note to self - sixty pages read."));
        for (var i = 60; i < 63; i++)
            turns.Add(Read(fx, i));
        turns.Add(Turn.Says("All read."));

        var events = await fx.RunAsync(
            // A large window, so the reported size is the whole story and no trim gets involved.
            fx.Build(new FakeChatProvider(turns.ToArray()) { Window = 1_000_000 }, EngineFixture.Role("developer")),
            "a long job");

        Assert.Equal(1, Count(events, "This step has run 60 turns. Carrying its own notes"));
        Assert.Equal(0, Count(events, "tokens this model was given"));
    }
}
