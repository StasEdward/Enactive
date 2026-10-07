namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

/// <summary>
/// Found in a log Stas sent on 2026-09-07 at 19:54, after a run that had otherwise gone fine.
///
/// <para>The run itself ended at line 10,429. He pressed "AI Analyze". The analysis prompt carries
/// an excerpt of the log — 4,740 lines of it — and the provider decorator wrote that prompt into
/// the log, so the file became 15,214 lines. He pressed it again. The second analysis was handed a
/// log that was a third its own previous prompt, and could only excerpt 4,427 lines of it; the
/// exported file was 19,664 lines, <b>47% of it analysis prompts</b>.</para>
///
/// <para>Each press therefore buys less of the run and more of itself, and the budget that decides
/// what the model sees is spent on the wrong text. A log-reading feature that fills the log is
/// eating the record it exists to read.</para>
///
/// <para>The fix is the rule this codebase applies everywhere else — <b>elide content, never remove
/// the record that something happened</b>. The call is still logged, with its model, settings and a
/// note; the model's ANSWER is still logged, because it is short and it is the useful part. Only
/// the messages, which are the log, are left out.</para>
///
/// <para>What this file cannot reach: that the log window's button actually ASKS for it. That is
/// one argument in <c>MainWindow.LogAnalysis()</c>, in a UI project this suite does not reference.
/// The decorator is pinned here; the call site is one line and is read, not tested.</para>
/// </summary>
public sealed class LogRecursionTests
{
    /// <summary>A sink that keeps everything, which is what makes "what reached the log" assertable.</summary>
    private sealed class Recorder : ILogSink
    {
        public List<LogEntry> Entries { get; } = new();
        public void Log(LogEntry entry) { lock (Entries) Entries.Add(entry); }

        public string Everything()
            => string.Join("\n", Entries.Select(e => $"{e.Source}: {e.Message}\n{e.Detail}"));
    }

    /// <summary>A provider that answers and records nothing else — the decorator is under test.</summary>
    private sealed class Stub : IChatProvider
    {
        public int? ContextWindow(ChatRequest request) => null;

        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
            => Task.FromResult(new ChatCompletion(
                new ChatMessage(ChatRole.Assistant, "### PROBLEMS\nNothing failed.", null),
                "stop", 900, 20, null));

        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new TextDelta("### PROBLEMS\nNothing failed.");
            yield return new FinishDelta("stop");
        }
    }

    /// <summary>A prompt shaped like the real one: a system prompt and the log itself.</summary>
    private static ChatRequest AnalysisRequest()
        => new("a-model",
               new[]
               {
                   ChatMessage.System("You are reading an application's own log."),
                   ChatMessage.User(
                       "This is an EXCERPT: 4740 of the log's 10429 lines.\n\n----- LOG -----\n"
                       + string.Join("\n", Enumerable.Range(0, 4_000)
                           .Select(i => $"19:51:0{i % 10}.000  INF  Tool  8774e2#1  [ToolInvoked]  line {i}")))
               },
               Temperature: 0.0);

    private static async Task<Recorder> AnalyseWith(bool promptBodies)
    {
        var log = new Recorder();
        var provider = new LoggingChatProvider(new Stub(), log, "ollama", promptBodies);
        await provider.CompleteAsync(AnalysisRequest(), CancellationToken.None);
        return log;
    }

    // ── the defect ──────────────────────────────────────────────────────────

    /// <summary>The one that matters: the log the model was given does not end up in the log.</summary>
    [Fact]
    public async Task The_log_being_analysed_is_not_written_back_into_the_log()
    {
        var written = (await AnalyseWith(promptBodies: false)).Everything();

        Assert.DoesNotContain("----- LOG -----", written, StringComparison.Ordinal);
        Assert.DoesNotContain("[ToolInvoked]", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// To scale. The excerpt was ~4,700 lines and the entry that replaces it is a few hundred
    /// characters: the point of the fix is the size, not the wording.
    /// </summary>
    [Fact]
    public async Task What_reaches_the_log_is_a_note_rather_than_an_excerpt()
    {
        var written = (await AnalyseWith(promptBodies: false)).Everything();
        var whole = (await AnalyseWith(promptBodies: true)).Everything();

        Assert.True(written.Length < 1_000, $"{written.Length} characters");
        Assert.True(whole.Length > 100_000, $"the case being fixed should be huge; it was {whole.Length}");
    }

    // ── and the record is still there ───────────────────────────────────────

    /// <summary>
    /// The call is still logged. Removing the entry would be the other failure this codebase keeps
    /// finding: a record shortened with nothing saying so.
    /// </summary>
    [Fact]
    public async Task The_call_is_still_recorded()
    {
        var entries = (await AnalyseWith(promptBodies: false)).Entries;

        var prompt = Assert.Single(entries, e => e.Source == LogSource.Prompt);
        Assert.Contains("prompt → ollama/a-model", prompt.Message, StringComparison.Ordinal);
        Assert.Contains("2 messages", prompt.Message, StringComparison.Ordinal);
    }

    /// <summary>And it says WHY its body is not there, where the body would have been.</summary>
    [Fact]
    public async Task The_entry_says_why_the_prompt_is_not_shown()
    {
        var prompt = (await AnalyseWith(promptBodies: false))
            .Entries.Single(e => e.Source == LogSource.Prompt);

        Assert.Contains("not shown", prompt.Detail ?? "", StringComparison.Ordinal);
        Assert.Contains("The call above did happen", prompt.Detail ?? "", StringComparison.Ordinal);
        // How the call was made is cheap and true either way.
        Assert.Contains("model=a-model", prompt.Detail ?? "", StringComparison.Ordinal);
        Assert.Contains("temperature=0", prompt.Detail ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The ANSWER is still written in full. It is a few hundred characters, it is what somebody
    /// opened the log for, and it is the one part that is not a copy of something already there.
    /// </summary>
    [Fact]
    public async Task The_models_answer_is_still_logged_in_full()
    {
        var llm = (await AnalyseWith(promptBodies: false))
            .Entries.Single(e => e.Source == LogSource.Llm);

        Assert.Contains("### PROBLEMS", llm.Detail ?? "", StringComparison.Ordinal);
        Assert.Contains("Nothing failed.", llm.Detail ?? "", StringComparison.Ordinal);
    }

    // ── what must not change ────────────────────────────────────────────────

    /// <summary>
    /// Every OTHER call still logs its prompt in full. This is one caller's exemption, not a new
    /// default: a run whose prompts stopped being recorded would take the readable plane of the log
    /// with it, which is the thing that has diagnosed nine defects this week.
    /// </summary>
    [Fact]
    public async Task An_ordinary_call_still_logs_its_whole_prompt()
    {
        var log = new Recorder();
        var provider = new LoggingChatProvider(new Stub(), log, "ollama");

        await provider.CompleteAsync(
            new ChatRequest("a-model",
                            new[] { ChatMessage.System("You are a developer agent."),
                                    ChatMessage.User("Fix the failing build.") }),
            CancellationToken.None);

        var written = log.Everything();

        Assert.Contains("You are a developer agent.", written, StringComparison.Ordinal);
        Assert.Contains("Fix the failing build.", written, StringComparison.Ordinal);
    }

    /// <summary>The streaming path is the one every run uses, and it obeys the same flag.</summary>
    [Fact]
    public async Task The_streaming_path_obeys_the_same_rule()
    {
        var log = new Recorder();
        var provider = new LoggingChatProvider(new Stub(), log, "ollama", promptBodies: false);

        await foreach (var _ in provider.StreamChatAsync(AnalysisRequest(), CancellationToken.None)) { }

        Assert.DoesNotContain("----- LOG -----", log.Everything(), StringComparison.Ordinal);
        Assert.Single(log.Entries, e => e.Source == LogSource.Prompt);
    }

    /// <summary>
    /// The window asks for the analyst's provider through the interface now, since the engine it holds is no
    /// longer built from the concrete factory. The interface's own answer ignores the flag - right for a factory
    /// that logs nothing - so the real factory must answer it with its own method, or every analysis would
    /// write the log back into the log again.
    /// </summary>
    [Fact]
    public void The_real_factory_answers_the_interface_s_request_without_bodies_itself()
    {
        var map = typeof(ChatProviderFactory).GetInterfaceMap(typeof(IChatProviderFactory));
        var withFlag = Array.FindIndex(map.InterfaceMethods, m => m.GetParameters().Length == 2);

        Assert.Equal(typeof(ChatProviderFactory), map.TargetMethods[withFlag].DeclaringType);
    }
}
