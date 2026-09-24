namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;
using Enactive.Providers;
using Enactive.Tools;
using Xunit;

/// <summary>
/// The same answers, for less work: a scan that does not open what its name already says is binary,
/// and a prompt log that does not write the same conversation again every turn.
///
/// <para><b>Measured 2026-09-24 on this repository.</b> The first search of a run took 17.5 s and the
/// second 0.6 s; the difference is the cold open of about 5,000 files, 2,735 of them build output
/// (<c>.dll</c>, <c>.so</c>, <c>.a</c>, <c>.pdb</c>) opened only to find a NUL byte. And a prompt of
/// 171 messages wrote 506,334 characters into the log each turn, when the turn before had written
/// 169 of the same messages: about 150 MB of log a run.</para>
/// </summary>
public sealed class LessWorkForTheSameAnswerTests
{
    // ── the scan ─────────────────────────────────────────────────────────────

    /// <summary>A file named as binary is treated as binary without being read.</summary>
    [Fact]
    public async Task A_file_whose_name_says_binary_is_not_searched()
    {
        using var fx = new EngineFixture();
        fx.Write("lib/native.dll", "needle - but this is a DLL by name, and is not opened");
        fx.Write("notes.md", "no match here");

        var result = await fx.Invoke(new SearchFilesTool(), """{"pattern":"needle"}""");

        Assert.DoesNotContain("native.dll:", result.Output ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BOUNDARY. An extension nobody listed is still decided by looking, so a text file with an
    /// unusual name is never mistaken for binary.
    /// </summary>
    [Fact]
    public async Task An_unlisted_extension_is_still_searched()
    {
        using var fx = new EngineFixture();
        fx.Write("data.xyz", "a needle in an unusual file");

        var result = await fx.Invoke(new SearchFilesTool(), """{"pattern":"needle"}""");

        Assert.Contains("data.xyz", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Skipped folders are cut off before the walk enters them - and are still found when the scan is
    /// pointed AT one, which is how the worker's own scratch area is reachable.
    /// </summary>
    [Fact]
    public async Task Skipped_folders_are_skipped_in_a_sweep_and_found_when_named()
    {
        using var fx = new EngineFixture();
        fx.Write("src/a.cs", "needle in the source");
        fx.Write("bin/Debug/a.txt", "needle in build output");
        fx.Write("sub/obj/b.txt", "needle in nested build output");

        var sweep = await fx.Invoke(new SearchFilesTool(), """{"pattern":"needle"}""");
        Assert.Contains("src/a.cs", sweep.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("bin/", sweep.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("obj/", sweep.Output, StringComparison.Ordinal);

        var named = await fx.Invoke(new SearchFilesTool(), """{"pattern":"needle","path":"bin"}""");
        Assert.Contains("a.txt", named.Output, StringComparison.Ordinal);
    }

    // ── the prompt log ───────────────────────────────────────────────────────

    private sealed class Capture : ILogSink
    {
        public List<LogEntry> Entries { get; } = new();
        public void Log(LogEntry entry) => Entries.Add(entry);
    }

    private static string LastPrompt(Capture log)
        => log.Entries.Last(e => e.Message.StartsWith("prompt", StringComparison.Ordinal)).Detail ?? "";

    /// <summary>
    /// A turn that only appends to the conversation logs only what it appended - and a prompt that
    /// is not a continuation (a trim or a handover built new messages) is logged whole.
    /// </summary>
    [Fact]
    public async Task A_continued_conversation_logs_only_what_was_added()
    {
        var log = new Capture();
        var provider = new LoggingChatProvider(new FakeChatProvider { WhenExhausted = Turn.Says("ok") }, log, "p");

        var messages = new List<ChatMessage>
        {
            ChatMessage.System("SYSTEM-PROMPT"),
            ChatMessage.User("FIRST-REQUEST")
        };

        await provider.CompleteAsync(new ChatRequest("m", messages), default);
        Assert.Contains("FIRST-REQUEST", LastPrompt(log), StringComparison.Ordinal);

        // The next turn: the same two messages, and one more.
        messages.Add(ChatMessage.User("SECOND-TURN"));
        await provider.CompleteAsync(new ChatRequest("m", messages), default);

        var second = LastPrompt(log);
        Assert.Contains("messages 1-2 are exactly as in the previous prompt", second, StringComparison.Ordinal);
        Assert.Contains("SECOND-TURN", second, StringComparison.Ordinal);
        Assert.DoesNotContain("FIRST-REQUEST", second, StringComparison.Ordinal);

        // A trim or a handover: an earlier message is a new object now. Logged whole.
        messages[1] = ChatMessage.User("FIRST-REQUEST, shortened");
        await provider.CompleteAsync(new ChatRequest("m", messages), default);

        var third = LastPrompt(log);
        Assert.DoesNotContain("exactly as in the previous prompt", third, StringComparison.Ordinal);
        Assert.Contains("SYSTEM-PROMPT", third, StringComparison.Ordinal);
    }
}
