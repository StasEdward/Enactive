namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;
using Enactive.Providers;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Four edges of 9da-9dc that a review of those commits reproduced, 2026-09-24: a text format taken
/// for binary by its name, a line of exactly 8,000 characters taken for one too long to show, a long
/// LAST line the ledger sent the model back to read again, and a prompt log that pointed at a prompt
/// the log no longer held.
/// </summary>
public sealed class EdgesTheReviewFoundTests
{
    // ── .obj is text as often as it is binary ────────────────────────────────

    [Fact]
    public async Task A_text_obj_model_is_searched()
    {
        using var fx = new EngineFixture();
        fx.Write("mesh.obj", "v 1 2 3\nv 4 5 6\n");

        var sweep = await fx.Invoke(new SearchFilesTool(), """{"pattern":"v 1 2 3","context":0}""");
        Assert.Contains("mesh.obj:1:", sweep.Output, StringComparison.Ordinal);

        var named = await fx.Invoke(new SearchFilesTool(), """{"pattern":"v 4","path":"mesh.obj","context":0}""");
        Assert.Contains("mesh.obj:2:", named.Output, StringComparison.Ordinal);
    }

    /// <summary>THE BOUNDARY. An object file a compiler wrote is still binary - the NUL check says so.</summary>
    [Fact]
    public async Task A_compiled_obj_is_still_binary()
    {
        using var fx = new EngineFixture();
        File.WriteAllBytes(Path.Combine(fx.Root, "main.obj"), [0x4C, 0x01, 0x00, 0x00, (byte)'n', (byte)'e', (byte)'e', (byte)'d', (byte)'l', (byte)'e']);

        var result = await fx.Invoke(new SearchFilesTool(), """{"pattern":"needle"}""");

        Assert.DoesNotContain("main.obj:", result.Output ?? "", StringComparison.Ordinal);
    }

    // ── a cut that falls between two lines ───────────────────────────────────

    /// <summary>
    /// A line of exactly 8,000 characters, then a break: it was shown whole, so it is not "longer than
    /// 8000", it counts as seen, and reading on starts at the next line.
    /// </summary>
    [Fact]
    public async Task A_line_of_exactly_the_cap_is_shown_whole()
    {
        using var fx = new EngineFixture();
        fx.Write("exact.txt", new string('x', 8000) + "\ny");

        var result = await fx.Invoke(new ReadFileTool(), """{"path":"exact.txt"}""");

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("longer than", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("only in part", result.Output, StringComparison.Ordinal);
        Assert.Contains("after line 1", result.Output, StringComparison.Ordinal);
        Assert.Contains("Read on with offset 2.", result.Output, StringComparison.Ordinal);
        Assert.Equal(1, Convert.ToInt32(result.Metadata["lastLine"]));
        Assert.Null(result.Metadata["tooLongLine"]);
    }

    /// <summary>Read in two windows as told, the file counts as read, and a whole-file write goes ahead.</summary>
    [Fact]
    public async Task Reading_on_as_told_after_a_cap_sized_line_reads_the_file()
    {
        using var fx = new EngineFixture();
        fx.Write("exact.txt", new string('x', 8000) + "\ny");

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"rewrite it"}"""),
            Turn.Calls1("read_file", """{"path":"exact.txt"}""", "r1"),
            Turn.Calls1("read_file", """{"path":"exact.txt","offset":2}""", "r2"),
            // As long as the original, so the guard against a file shrinking to nothing stays out of it.
            Turn.Calls1("write_file", "{\"path\":\"exact.txt\",\"content\":\"" + new string('z', 8000) + "\"}", "w1"),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "rewrite it");

        var said = string.Join("\n", provider.Requests.SelectMany(r => r.Messages).Select(m => m.Content));
        Assert.DoesNotContain("has read only lines", said, StringComparison.Ordinal);
        Assert.Equal(new string('z', 8000), fx.Read("exact.txt"));
    }

    // ── a long last line ─────────────────────────────────────────────────────

    /// <summary>
    /// A file that is one line longer than the cap has no next page, and the refusal must not send the
    /// model back to read it: that returns the same cut forever.
    /// </summary>
    [Fact]
    public async Task A_long_last_line_is_not_offered_as_the_place_to_read_again()
    {
        using var fx = new EngineFixture();
        fx.Write("one.js", new string('m', 9000));

        var read = await fx.Invoke(new ReadFileTool(), """{"path":"one.js"}""");
        Assert.Equal(1, Convert.ToInt32(read.Metadata["tooLongLine"]));

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"rewrite it"}"""),
            Turn.Calls1("read_file", """{"path":"one.js"}""", "r1"),
            Turn.Calls1("write_file", """{"path":"one.js","content":"rewritten"}""", "w1"),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "rewrite it");

        var said = string.Join("\n", provider.Requests.SelectMany(r => r.Messages).Select(m => m.Content));
        Assert.Contains("longer than read_file can show", said, StringComparison.Ordinal);
        Assert.DoesNotContain("\"offset\": 1", said, StringComparison.Ordinal);
    }

    // ── the prompt log points only at what the log still holds ───────────────

    private static string LastPrompt(LogHub hub)
        => hub.Snapshot().Last(e => e.Source == LogSource.Prompt).Detail ?? "";

    [Fact]
    public async Task After_the_log_is_cleared_the_next_prompt_is_written_whole()
    {
        using var hub = new LogHub();
        var provider = new LoggingChatProvider(new FakeChatProvider { WhenExhausted = Turn.Says("ok") }, hub, "p");
        var messages = new List<ChatMessage> { ChatMessage.System("SYSTEM-PROMPT"), ChatMessage.User("FIRST-REQUEST") };

        await provider.CompleteAsync(new ChatRequest("m", messages), default);
        messages.Add(ChatMessage.User("SECOND-TURN"));
        await provider.CompleteAsync(new ChatRequest("m", messages), default);
        Assert.Contains("exactly as in the previous prompt", LastPrompt(hub), StringComparison.Ordinal);

        hub.Clear();
        messages.Add(ChatMessage.User("THIRD-TURN"));
        await provider.CompleteAsync(new ChatRequest("m", messages), default);

        var third = LastPrompt(hub);
        Assert.DoesNotContain("exactly as in the previous prompt", third, StringComparison.Ordinal);
        Assert.Contains("SYSTEM-PROMPT", third, StringComparison.Ordinal);
        Assert.Contains("FIRST-REQUEST", third, StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_the_whole_prompt_gave_way_in_the_ring_the_next_is_written_whole()
    {
        using var hub = new LogHub(capacity: 64);
        var provider = new LoggingChatProvider(new FakeChatProvider { WhenExhausted = Turn.Says("ok") }, hub, "p");
        var messages = new List<ChatMessage> { ChatMessage.System("SYSTEM-PROMPT"), ChatMessage.User("FIRST-REQUEST") };

        await provider.CompleteAsync(new ChatRequest("m", messages), default);
        for (var i = 0; i < 100; i++)
            hub.Info(LogSource.Tool, $"filler {i}");

        messages.Add(ChatMessage.User("SECOND-TURN"));
        await provider.CompleteAsync(new ChatRequest("m", messages), default);

        Assert.Contains("SYSTEM-PROMPT", LastPrompt(hub), StringComparison.Ordinal);
    }

    /// <summary>THE BOUNDARY. While the chain is held, the delta still saves what it was built to save.</summary>
    [Fact]
    public async Task While_the_log_holds_the_chain_only_the_new_messages_are_written()
    {
        using var hub = new LogHub();
        var provider = new LoggingChatProvider(new FakeChatProvider { WhenExhausted = Turn.Says("ok") }, hub, "p");
        var messages = new List<ChatMessage> { ChatMessage.System("SYSTEM-PROMPT"), ChatMessage.User("FIRST-REQUEST") };

        await provider.CompleteAsync(new ChatRequest("m", messages), default);
        messages.Add(ChatMessage.User("SECOND-TURN"));
        await provider.CompleteAsync(new ChatRequest("m", messages), default);
        messages.Add(ChatMessage.User("THIRD-TURN"));
        await provider.CompleteAsync(new ChatRequest("m", messages), default);

        var third = LastPrompt(hub);
        Assert.Contains("messages 1-3 are exactly as in the previous prompt", third, StringComparison.Ordinal);
        Assert.DoesNotContain("SYSTEM-PROMPT", third, StringComparison.Ordinal);
    }
}
