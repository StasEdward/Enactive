namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Agents;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// A reply whose text runs away - a passage going round, or far more prose than a turn needs - is
/// stopped while it streams, explained to the model, and the step goes on; and a long reply can be
/// read from the log while it is still being written.
///
/// <para><b>Measured 2026-09-24 19:51, run 99f7dc1d.</b> One reply streamed plain text for more than
/// six minutes, 15,000 tokens and counting with 40,000 allowed, and nothing reached the log until it
/// would end. The reply before it had "confirmed" five facts in a row as prose.</para>
/// </summary>
public sealed class ARunawayReplyIsStoppedTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"check the claims"}""";

    private const string Passage = "Now let me check the next claim in the page. Confirmed, it matches.\n";

    private static string Looping(int times) => "Starting the checks.\n" + string.Concat(Enumerable.Repeat(Passage, times));

    /// <summary>Prose that never repeats: numbered, so no passage comes round twice.</summary>
    private static string Varied(int chars)
    {
        var sb = new StringBuilder();
        for (var i = 0; sb.Length < chars; i++)
            sb.Append("Item ").Append(i).Append(" was looked at, and item ").Append(i * 7 + 3).Append(" is next.\n");
        return sb.ToString();
    }

    private static string AllSaid(FakeChatProvider provider)
        => string.Join("\n", provider.Requests.SelectMany(r => r.Messages).Select(m => m.Content));

    // ── the watch itself ─────────────────────────────────────────────────────

    /// <summary>RunawayReply.LoopRepeats / ShortestLoopChars / CheckEveryChars — a passage three times in a row.</summary>
    [Fact]
    public void A_passage_coming_round_is_a_loop_and_its_first_pass_is_kept()
    {
        var text = new StringBuilder(Looping(20));

        var stop = new RunawayReply().After(text);

        Assert.NotNull(stop);
        Assert.True(stop.Looped);
        Assert.Contains($"{Passage.Length} characters came 20 times", stop.Reason, StringComparison.Ordinal);
        Assert.Equal("Starting the checks.\n" + Passage, text.ToString(0, stop.KeepChars));
    }

    /// <summary>RunawayReply.LongestLoopChars — a loop of a whole paragraph is found as well.</summary>
    [Fact]
    public void A_paragraph_coming_round_is_a_loop_too()
    {
        var paragraph = Varied(1_500);
        var text = new StringBuilder(paragraph + paragraph + paragraph);

        var stop = new RunawayReply().After(text);

        Assert.NotNull(stop);
        Assert.True(stop.Looped);
    }

    /// <summary>
    /// THE BOUNDARY. How text is shaped is not a loop: a separator of dashes repeats with every period,
    /// table rows repeat their frame, and neither is a model going round.
    /// </summary>
    [Fact]
    public void Separators_and_tables_are_not_loops()
    {
        var text = new StringBuilder();
        text.Append(new string('-', 400)).Append('\n');
        for (var i = 0; i < 60; i++)
            text.Append("| claim ").Append(i).Append(" | checked against the code | ✅ |\n");
        text.Append(new string('=', 400));

        Assert.Null(new RunawayReply().After(text));
    }

    /// <summary>RunawayReply.MaxTextChars — prose past the limit is stopped even when it never repeats.</summary>
    [Fact]
    public void Prose_past_the_limit_is_stopped()
    {
        var text = new StringBuilder(Varied(RunawayReply.MaxTextChars + 100));

        var stop = new RunawayReply().After(text);

        Assert.NotNull(stop);
        Assert.False(stop.Looped);
        Assert.Contains("without a tool call", stop.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_long_reply_is_not_stopped()
        => Assert.Null(new RunawayReply().After(new StringBuilder(Varied(12_000))));

    // ── in a step ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_looping_reply_is_stopped_explained_and_the_step_goes_on()
    {
        using var fx = new EngineFixture();
        fx.Write("page.md", "the claim");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Says(Looping(50)),
            Turn.Calls1("read_file", """{"path":"page.md"}""", "r1"),
            Turn.Says("Checked page.md: the claim is there."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the claims");

        Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved
                                     && e.Summary.Contains("reply was stopped", StringComparison.Ordinal));

        var after = provider.Requests.First(r => r.Messages.Any(
            m => m.Content?.Contains("Your reply above was stopped", StringComparison.Ordinal) == true));
        var said = string.Join("\n", after.Messages.Select(m => m.Content));
        Assert.Contains("Only its first pass is kept", said, StringComparison.Ordinal);
        Assert.Contains("a check is made by calling a tool", said, StringComparison.Ordinal);

        // One pass of the loop in the transcript, not fifty.
        Assert.Equal(1, CountOf(said, Passage));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    // ── reasoning that goes round ───────────────────────────────────────────

    /// <summary>
    /// Run f08f1e, 2026-10-09: three turns of reasoning, 50-59 thousand characters each, every one repeating a paragraph
    /// word for word by its 4,000th to 9,000th character, cut only at the token limit. A loop in the reasoning is stopped
    /// as one in the reply is; its size alone is not (that is the provider's reasoning allowance).
    /// </summary>
    [Fact]
    public void A_reasoning_that_goes_round_is_a_loop_and_its_size_alone_is_not()
    {
        var looping = new StringBuilder(Looping(50));
        Assert.True(new RunawayReply().LoopIn(looping) is { Looped: true, InReasoning: true });

        var long_ = new StringBuilder(Varied(RunawayReply.MaxTextChars * 2));
        Assert.Null(new RunawayReply().LoopIn(long_));
    }

    [Fact]
    public async Task A_looping_reasoning_is_stopped_told_to_act_and_the_step_goes_on()
    {
        using var fx = new EngineFixture();
        fx.Write("page.md", "the claim");
        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Thinks(Looping(50)),
            Turn.Calls1("read_file", """{"path":"page.md"}""", "r1"),
            Turn.Says("Checked page.md: the claim is there."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the claims");

        Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved
                                     && e.Summary.StartsWith("The model's reasoning was stopped", StringComparison.Ordinal));
        Assert.Contains(provider.Requests.SelectMany(r => r.Messages),
            m => m.Content?.Contains("Your reasoning was stopped", StringComparison.Ordinal) == true
                 && m.Content.Contains("Decide from what you have and act", StringComparison.Ordinal));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    [Fact]
    public async Task A_second_runaway_in_the_same_step_stops_the_step()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Says(Looping(50)),
            Turn.Says(Looping(50)),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the claims");

        Assert.Contains("ran away again", events.Text(), StringComparison.Ordinal);
        Assert.DoesNotContain("Done.", AllSaid(provider), StringComparison.Ordinal);
    }

    /// <summary>ProgressEveryChars, for text: a long reply says what it is writing, in the log too.</summary>
    [Fact]
    public async Task A_long_reply_says_what_it_is_writing()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Says(Varied(5_000)));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the claims");

        var progress = events.Where(e => e.Kind == EventKind.GenerationProgress).ToList();
        Assert.Contains(progress, e => e.Summary.StartsWith("Writing a reply:", StringComparison.Ordinal)
                                       && e.Summary.Contains("\"Item ", StringComparison.Ordinal));
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
