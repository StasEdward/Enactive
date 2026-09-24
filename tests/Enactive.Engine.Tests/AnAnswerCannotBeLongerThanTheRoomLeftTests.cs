namespace Enactive.Engine.Tests;

using Xunit;

/// <summary>
/// A turn is told how much room is left for its answer.
///
/// <para><b>Measured 2026-09-24 03:37.</b> A step's turn went into generation and did not come out:
/// the engine's log stops at the request, and the llama.cpp server shows <c>n_gen = 7296</c> still
/// climbing two minutes later, at 62 tokens a second, with no <c>prompt eval</c> line because the
/// prefix had hit the cache and it went straight to writing.</para>
///
/// <para>Nothing stopped it. <c>max_tokens</c> was never sent — the provider's field was blank and
/// the engine added nothing — so the model generated until it chose to stop. A hosted provider has
/// a bill to make somebody care; a local one does not.</para>
///
/// <para><b>Derived, not invented.</b> A constant would either truncate a long write that would
/// have fitted — the largest real one measured is 5,478 tokens, a report appended with
/// <c>edit_file</c> — or sit high enough to be no limit at all. What is LEFT of the declared window
/// cannot truncate anything that would have succeeded, because anything longer was going to
/// overflow the window regardless. It only turns two silent minutes into <c>finish=length</c>,
/// which the loop already explains.</para>
/// </summary>
public sealed class AnAnswerCannotBeLongerThanTheRoomLeftTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write it"}""";

    [Fact]
    public async Task A_provider_with_a_window_is_told_what_is_left_for_the_answer()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", new string('x', 4_000));

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("read_file", """{"path":"a.md"}"""),
            Turn.Says("Read it.")) { Window = 8192 };

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write it");

        // Every worker request carries a ceiling, and it is smaller than the window: the prompt has
        // already taken part of it.
        var asked = provider.Requests.Where(r => r.Tools is { Count: > 0 }).ToArray();
        Assert.NotEmpty(asked);

        Assert.All(asked, r =>
        {
            Assert.NotNull(r.MaxTokens);
            Assert.InRange(r.MaxTokens!.Value, 1, 8192);
        });

        // And it SHRINKS as the transcript grows - which is the property that makes it a ceiling on
        // the answer rather than a constant wearing one's clothes.
        Assert.True(asked[^1].MaxTokens < asked[0].MaxTokens,
                    $"first {asked[0].MaxTokens}, last {asked[^1].MaxTokens} - the room left must fall "
                    + "as the prompt grows");
    }

    /// <summary>
    /// THE BOUNDARY. A provider that states no window gets no ceiling from here. Its own
    /// <c>max_tokens</c> setting caps the answer and says nothing about the transcript, which is
    /// every hosted provider — and inventing a number for them would cut work that was fine.
    /// </summary>
    [Fact]
    public async Task A_provider_with_no_stated_window_is_left_alone()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Says("Nothing to do."));   // Window stays null

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write it");

        Assert.All(provider.Requests, r => Assert.Null(r.MaxTokens));
    }
}
