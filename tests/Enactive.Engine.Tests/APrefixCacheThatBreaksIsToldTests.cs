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
    /// <summary>
    /// Run b90162, 2026-10-05: a step on a local server was followed by a step routed to a cloud model, in the same
    /// conversation. The first request to the cloud server was compared with the last one to the local server, whose
    /// cache it cannot share, and was told as a break "though the conversation only grew". Each server's cache is its
    /// own: a request is compared with the request before it to the same server and model.
    /// </summary>
    [Fact]
    public void The_first_request_to_another_server_is_not_told_as_a_break()
    {
        var watch = new PrefixCacheWatch();
        var messages = Conversation();
        watch.Sending(messages, "local/small");
        watch.Observed(12_000, 0, true);
        messages.Add(new ChatMessage(ChatRole.Assistant, "Ran the digest."));
        watch.Sending(messages, "local/small");
        Assert.Null(watch.Observed(12_400, 11_900, true));

        messages.Add(ChatMessage.User("now decide"));
        watch.Sending(messages, "cloud/large");

        Assert.Null(watch.Observed(14_500, 384, true));
    }

    [Fact]
    public void A_break_on_the_same_server_after_a_change_of_server_is_still_told()
    {
        var watch = new PrefixCacheWatch();
        var messages = Conversation();
        watch.Sending(messages, "local/small");
        watch.Observed(12_000, 0, true);
        messages.Add(ChatMessage.User("now decide"));
        watch.Sending(messages, "cloud/large");
        watch.Observed(14_500, 384, true);

        messages.Add(new ChatMessage(ChatRole.Assistant, "Sending the letter."));
        watch.Sending(messages, "cloud/large");

        Assert.Contains("only grew", watch.Observed(15_000, 200, true), StringComparison.Ordinal);
    }
}
