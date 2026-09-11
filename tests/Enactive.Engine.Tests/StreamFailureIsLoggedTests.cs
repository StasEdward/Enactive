namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

/// <summary>
/// Found in a log Stas sent on 2026-09-11 at 16:03. He started a run with Ollama switched off. The
/// run failed, correctly, with a diagnosis that could not have been better worded — and the log
/// line the provider decorator wrote for the call that threw was this:
///
/// <para><c>INF  Llm  [gemma4:31b-cloud]  response ← ollama/gemma4:31b-cloud (0 chars, 0 tool
/// call(s))</c></para>
///
/// <para>Informational. Past tense. Indistinguishable from a model that was asked and chose to say
/// nothing. The exception's message did not reach the log from here at all, so a person reading the
/// log for the cause found a normal-looking call and no error.</para>
///
/// <para><b>Why nothing caught it:</b> the streaming method held a <c>faulted</c> flag that nothing
/// ever assigned, read by its <c>finally</c> to pick Warn over Info. A guard that enforces nothing
/// while looking configured. The reason it was never assigned is that a <c>yield return</c> cannot
/// live inside a <c>try</c> that catches, so the catch that would have set it does not compile —
/// and the non-streaming half of the same decorator has logged the failure correctly all along,
/// which is exactly what made the gap invisible from a reading.</para>
///
/// <para>These tests hold the two halves to the same promise, because the half nobody checked is
/// the half that was wrong.</para>
/// </summary>
public sealed class StreamFailureIsLoggedTests
{
    private const string Down =
        "Nothing is listening at http://localhost:11434/v1, so ollama/gemma4:31b-cloud could not be "
        + "asked. If that is a local model server, it is not running.";

    private sealed class Recorder : ILogSink
    {
        public List<LogEntry> Entries { get; } = new();
        public void Log(LogEntry entry) { lock (Entries) Entries.Add(entry); }

        public IEnumerable<LogEntry> Llm => Entries.Where(e => e.Source == LogSource.Llm);
    }

    /// <summary>Throws where a dead model server throws: on the first move, before any content.</summary>
    private sealed class DeadServer : IChatProvider
    {
        public int? ContextWindow(ChatRequest request) => null;

        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
            => throw new InvalidOperationException(Down);

        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException(Down);
#pragma warning disable CS0162 // unreachable: an iterator needs a yield to be one
            yield break;
#pragma warning restore CS0162
        }
    }

    /// <summary>Says something, then dies — the half-stream case.</summary>
    private sealed class DiesMidStream : IChatProvider
    {
        public int? ContextWindow(ChatRequest request) => null;

        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
            => throw new InvalidOperationException(Down);

        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new TextDelta("I had started to say");
            throw new InvalidOperationException(Down);
        }
    }

    /// <summary>Answers normally — the control, so "always ERR" cannot pass these tests.</summary>
    private sealed class Fine : IChatProvider
    {
        public int? ContextWindow(ChatRequest request) => null;

        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
            => Task.FromResult(new ChatCompletion(
                new ChatMessage(ChatRole.Assistant, "done", null), "stop", 10, 2, null));

        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new TextDelta("done");
            yield return new FinishDelta("stop");
        }
    }

    private static ChatRequest Ask()
        => new("gemma4:31b-cloud", new[] { ChatMessage.User("Read README and survey codebase") });

    private static async Task<Recorder> Stream(IChatProvider inner)
    {
        var log = new Recorder();
        var provider = new LoggingChatProvider(inner, log, "ollama");
        try
        {
            await foreach (var _ in provider.StreamChatAsync(Ask(), CancellationToken.None)) { }
        }
        catch (InvalidOperationException)
        {
            // The decorator must not swallow it either — the run needs it to fail.
        }
        return log;
    }

    // ── the defect ──────────────────────────────────────────────────────────

    /// <summary>The line a person reading the log actually sees.</summary>
    [Fact]
    public async Task A_stream_that_threw_is_logged_as_an_error()
    {
        var entry = Assert.Single((await Stream(new DeadServer())).Llm);

        Assert.Equal(LogLevel.Error, entry.Level);
    }

    /// <summary>
    /// And says WHY. An error line that reads "response ← ollama/gemma4:31b-cloud" and stops is the
    /// same failure one level quieter.
    /// </summary>
    [Fact]
    public async Task The_error_line_carries_the_providers_own_words()
    {
        var entry = Assert.Single((await Stream(new DeadServer())).Llm);

        Assert.Contains("localhost:11434", entry.Message);
        Assert.Contains("not running", entry.Message);
    }

    /// <summary>
    /// The exact sentence from his log, which is what the fix is for: it must not be possible to
    /// read this line as a call that went fine.
    /// </summary>
    [Fact]
    public async Task It_no_longer_reads_as_a_model_that_simply_said_nothing()
    {
        var entry = Assert.Single((await Stream(new DeadServer())).Llm);

        Assert.DoesNotContain("0 chars, 0 tool call(s)", entry.Message);
    }

    /// <summary>The detail pane gets the type and the stack, not just the sentence.</summary>
    [Fact]
    public async Task The_body_carries_the_exception_itself()
    {
        var entry = Assert.Single((await Stream(new DeadServer())).Llm);

        Assert.Contains(nameof(InvalidOperationException), entry.Detail ?? "");
    }

    /// <summary>
    /// What arrived before the throw is kept. A stream that failed half way is more legible with
    /// its half than without it, and that half is the only evidence of how far the call got.
    /// </summary>
    [Fact]
    public async Task A_half_stream_keeps_what_it_had_already_received()
    {
        var entry = Assert.Single((await Stream(new DiesMidStream())).Llm);

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("I had started to say", entry.Detail ?? "");
    }

    /// <summary>The decorator observes; it does not absorb. The run must still see the failure.</summary>
    [Fact]
    public async Task The_exception_still_reaches_the_caller()
    {
        var provider = new LoggingChatProvider(new DeadServer(), new Recorder(), "ollama");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.StreamChatAsync(Ask(), CancellationToken.None)) { }
        });

        Assert.Equal(Down, thrown.Message);
    }

    // ── the control ─────────────────────────────────────────────────────────

    /// <summary>
    /// Present so that "log everything as an error" cannot pass the tests above. A call that
    /// worked is still an ordinary informational line.
    /// </summary>
    [Fact]
    public async Task A_stream_that_worked_is_still_logged_as_information()
    {
        var entry = Assert.Single((await Stream(new Fine())).Llm);

        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Contains("4 chars", entry.Message);
        Assert.DoesNotContain("FAILED", entry.Message);
    }

    // ── the half that was already right, pinned so the two cannot drift again ──

    /// <summary>
    /// The non-streaming path has logged this correctly since it was written. It is asserted here
    /// anyway: the defect above existed because one half of this decorator was checked by reading
    /// and the other half was checked by nothing, and a rule enforced in one place is enforced
    /// nowhere after the next refactor.
    /// </summary>
    [Fact]
    public async Task The_non_streaming_path_makes_the_same_promise()
    {
        var log = new Recorder();
        var provider = new LoggingChatProvider(new DeadServer(), log, "ollama");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteAsync(Ask(), CancellationToken.None));

        var entry = Assert.Single(log.Llm);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("not running", entry.Message);
    }
}
