namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;

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
/// <para>The remaining window is an additional ceiling. A separate purpose-specific generation
/// budget applies even when the provider declares no window.</para>
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
            Assert.Null(r.MaxTokens); // The window is a ceiling, not an override of provider preferences.
            Assert.NotNull(r.OutputTokenLimit);
            Assert.InRange(r.OutputTokenLimit!.Value, 1, 8192);
        });

        // And it SHRINKS as the transcript grows - which is the property that makes it a ceiling on
        // the answer rather than a constant wearing one's clothes.
        Assert.True(asked[^1].OutputTokenLimit < asked[0].OutputTokenLimit,
                    $"first {asked[0].OutputTokenLimit}, last {asked[^1].OutputTokenLimit} - the room left must fall "
                    + "as the prompt grows");
    }

    /// <summary>
    /// Without a declared context window, the action budget still bounds generation.
    /// </summary>
    [Fact]
    public async Task A_provider_with_no_stated_window_gets_an_action_budget()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Says("Nothing to do."));   // Window stays null

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write it");

        Assert.All(provider.Requests.Where(r => r.Tools is { Count: > 0 }),
            r => Assert.Equal(new GenerationBudgets().Action, r.OutputTokenLimit));
    }
}
