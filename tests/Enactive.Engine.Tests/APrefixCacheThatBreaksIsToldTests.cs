namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Xunit;

/// <summary>
/// A conversation the engine only appends to should be read by the server once. Where the server's cache did not hold,
/// every turn paid the whole prompt again - on a hybrid model up to a checkpoint's spacing for a change near the end,
/// nearly the whole prompt for a trim near the start - and nothing said so: the usage was logged, never compared with the
/// request before. Now a break is told, and told apart from the engine's own rewrites. Deliberately not code: a wiki check.
/// </summary>
public sealed class APrefixCacheThatBreaksIsToldTests
{
    private static List<ChatMessage> Conversation()
        => [ChatMessage.System("You check wiki pages."), ChatMessage.User("check the pages")];

    /// <summary>THE ONE THAT MATTERS: only appended to, and the server re-read it all - that is said.</summary>
    [Fact]
    public void A_cache_that_broke_though_the_conversation_only_grew_is_told()
    {
        var watch = new PrefixCacheWatch();
        var messages = Conversation();
        watch.Sending(messages);
        Assert.Null(watch.Observed(20_000, 0, true));                               // the first request: nothing to compare

        messages.Add(new ChatMessage(ChatRole.Assistant, "Reading the first page."));
        messages.Add(ChatMessage.User("go on"));
        watch.Sending(messages);
        var note = watch.Observed(20_600, 12_000, true);

        Assert.NotNull(note);
        Assert.Contains("re-read 8600 of 20600 prompt tokens (cached 12000)", note!, StringComparison.Ordinal);
        Assert.Contains("only grew", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cache_that_held_is_not_mentioned()
    {
        var watch = new PrefixCacheWatch();
        var messages = Conversation();
        watch.Sending(messages);
        watch.Observed(20_000, 0, true);
        messages.Add(new ChatMessage(ChatRole.Assistant, "Reading the first page."));
        watch.Sending(messages);

        Assert.Null(watch.Observed(20_600, 19_990, true));
    }

    /// <summary>A trim or a hand-over rewrites the conversation: what it cost is said, as the engine's own doing.</summary>
    [Fact]
    public void A_rewrite_is_told_as_one_with_what_it_cost()
    {
        var watch = new PrefixCacheWatch();
        var messages = Conversation();
        messages.Add(new ChatMessage(ChatRole.Assistant, new string('a', 5_000)));
        watch.Sending(messages);
        watch.Observed(30_000, 0, true);

        messages[2] = new ChatMessage(ChatRole.Assistant, "[earlier tool result: 5000 characters, dropped to fit the context window]");
        watch.Sending(messages);
        var note = watch.Observed(12_000, 900, true);

        Assert.NotNull(note);
        Assert.Contains("was rewritten", note!, StringComparison.Ordinal);
    }

    /// <summary>A server that does not count cached tokens, and a second usage report in one reply, say nothing.</summary>
    [Fact]
    public void Without_numbers_or_twice_in_one_reply_nothing_is_said()
    {
        var watch = new PrefixCacheWatch();
        var messages = Conversation();
        watch.Sending(messages);
        watch.Observed(20_000, 0, true);
        messages.Add(ChatMessage.User("go on"));
        watch.Sending(messages);

        Assert.Null(watch.Observed(20_600, null, true));
        Assert.NotNull(watch.Observed(20_600, 100, true));
        Assert.Null(watch.Observed(20_600, 100, true));
    }
}
