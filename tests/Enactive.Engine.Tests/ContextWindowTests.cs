namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Not running out of room, and saying so honestly when it happens anyway.
///
/// <para>On 2026-09-06 a local model with num_ctx=8192 reached 8174 prompt tokens on a three-file
/// task and was cut off mid-argument — the last thing it managed to emit was
/// <c>{"command":"cd TicTacToe &amp;&amp;</c>. The engine reported "cut off at the token limit;
/// raise max_tokens", which named a setting that does not exist for that provider: Ollama's num_ctx
/// is ONE budget shared by prompt and generation, and the prompt had taken all of it.</para>
///
/// <para>Two things were missing. Nothing kept the transcript inside the window — an agent
/// conversation carries every file twice at full length and every command's whole stdout — and
/// nothing distinguished "the answer was too long" from "there was no room to answer".</para>
/// </summary>
public sealed class ContextWindowTests
{
    private const string QuickPlan = """{"disposition":"quick_action","title":"do the thing"}""";

    private static ChatMessage AssistantCall(string name, string args, string id)
        => new(ChatRole.Assistant, null, new[] { new ToolCall(id, name, args) });

    private static ChatMessage ToolResult(string id, string content)
        => new(ChatRole.Tool, content, null, id);

    /// <summary>system + user, then n tool exchanges of the given size.</summary>
    private static List<ChatMessage> Conversation(params int[] exchangeSizes)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("You are a developer agent."),
            ChatMessage.User("do the thing")
        };

        for (var i = 0; i < exchangeSizes.Length; i++)
        {
            messages.Add(AssistantCall("write_file", new string('a', exchangeSizes[i]), $"call_{i}"));
            messages.Add(ToolResult($"call_{i}", new string('b', exchangeSizes[i])));
        }

        return messages;
    }

    // ── the trimmer ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Size_counts_tool_call_arguments_not_just_message_text()
    {
        var withArguments = Conversation(1000);
        var withoutArguments = Conversation(0);

        Assert.True(Size(withArguments) - Size(withoutArguments) >= 2000);

        static int Size(List<ChatMessage> m) => Transcript.Size(m);
    }

    // The invariant that matters most: a tool result without its call, or a call without its result,
    // is a malformed conversation that providers reject outright. Trimming must never produce one,
    // which is why it elides content and never removes a message.
    [Fact]
    public void Trimming_shortens_messages_and_never_removes_one()
    {
        var messages = Conversation(2000, 2000, 2000, 2000);
        var before = messages.Count;

        Transcript.Elide(messages, targetChars: 500);

        Assert.Equal(before, messages.Count);
        Assert.Equal(4, messages.Count(m => m.Role == ChatRole.Tool));
        Assert.Equal(4, messages.Count(m => m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 }));
        // Every remaining call still has a result carrying its id.
        foreach (var call in messages.Where(m => m.ToolCalls is { Count: > 0 }).SelectMany(m => m.ToolCalls!))
            Assert.Contains(messages, m => m.Role == ChatRole.Tool && m.ToolCallId == call.Id);
    }

    [Fact]
    public void Trimming_takes_the_oldest_first_and_spares_the_two_newest()
    {
        var messages = Conversation(2000, 2000, 2000, 2000);

        // Enough room for the two spared exchanges (about 8100 characters) and no more.
        Transcript.Elide(messages, targetChars: 9000);

        var results = messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.Content!).ToArray();
        Assert.StartsWith("[earlier tool result:", results[0]);
        Assert.StartsWith("[earlier tool result:", results[1]);
        Assert.Equal(2000, results[2].Length);
        Assert.Equal(2000, results[3].Length);
    }

    // Sparing the newest is a preference, not a guarantee. One oversized write is the whole
    // transcript, and protecting it would end the step to preserve the contents of a file that is
    // already on disk.
    [Fact]
    public void A_single_oversized_exchange_is_elided_rather_than_spared()
    {
        var messages = Conversation(20000);

        var changed = Transcript.Elide(messages, targetChars: 1000);

        Assert.Equal(2, changed);
        Assert.True(Transcript.Size(messages) <= 1000);
        Assert.Equal(Transcript.ElidedArguments, messages.Single(m => m.ToolCalls is { Count: > 0 }).ToolCalls![0].ArgumentsJson);
    }

    // The stub replaces arguments, which are still sent as arguments — so it has to be valid JSON.
    [Fact]
    public void The_elided_arguments_stub_is_valid_json()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Transcript.ElidedArguments);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Trimming_an_already_trimmed_conversation_reports_nothing_left_to_give()
    {
        var messages = Conversation(5000, 5000);

        Assert.True(Transcript.Elide(messages, targetChars: 200) > 0);
        Assert.Equal(0, Transcript.Elide(messages, targetChars: 200));
    }

    // ── the scale ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_token_scale_learns_the_models_real_ratio()
    {
        var scale = new TokenScale();

        // Pessimistic before it has seen anything: 3 characters to a token.
        Assert.Equal(1000, scale.TokensFor(3000));

        scale.Observe(chars: 6000, promptTokens: 1000);
        Assert.Equal(500, scale.TokensFor(3000));
    }

    // One anomalous turn — a cached prompt, a provider that counts differently — must not be able to
    // convince the estimate that a token is forty characters.
    [Fact]
    public void An_absurd_observation_is_clamped()
    {
        var scale = new TokenScale();

        scale.Observe(chars: 100_000, promptTokens: 1);
        Assert.Equal(125, scale.TokensFor(1000));   // clamped at 8 chars/token

        scale.Observe(chars: 1, promptTokens: 100_000);
        Assert.Equal(667, scale.TokensFor(1000));   // clamped at 1.5 chars/token
    }

    [Fact]
    public void An_unreported_turn_leaves_the_scale_alone()
    {
        var scale = new TokenScale();

        scale.Observe(chars: 0, promptTokens: 0);
        Assert.Equal(1000, scale.TokensFor(3000));
    }

    // ── the engine ────────────────────────────────────────────────────────────────────

    // The step used to die here. Now the old traffic goes and the work continues.
    [Fact]
    public async Task A_transcript_that_outgrows_the_window_is_trimmed_and_the_step_continues()
    {
        using var fx = new EngineFixture();

        // Leave room for the real tool schemas/preamble; make tool traffic alone exceed it.
        var big = new string('x', 24000);

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("write_file", $$"""{"path":"one.txt","content":"{{big}}"}""", "c1"),
            Turn.Calls1("read_file", """{"path":"one.txt"}""", "c2"),
            Turn.Says("Done."))
        {
            Window = 4096
        };

        var events = await fx.RunAsync(fx.Build(provider), "do the thing");

        Assert.True(events.Has(EventKind.ContextTrimmed));
        Assert.True(events.Has(EventKind.TaskCompleted));
    }

    // Nothing to trim — the request itself does not fit — so the step stops instead of sending a
    // prompt the model can only answer with a fragment.
    [Fact]
    public async Task When_trimming_cannot_free_enough_the_step_stops_and_blames_num_ctx()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(Turn.Says(QuickPlan), Turn.Says("Done.")) { Window = 512 };

        // The request alone is far past the window, and there is no tool traffic to drop.
        var events = await fx.RunAsync(fx.Build(provider), new string('q', 40000));

        var stopped = Assert.Single(events.OfKind(EventKind.ErrorObserved));
        Assert.Contains("num_ctx", stopped.Summary);
        Assert.DoesNotContain("max_tokens", stopped.Summary);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    // A prompt that filled the window and a model that talked too long both arrive as finish=length.
    // They are different problems with different fixes, and the message has to say which one it was.
    [Fact]
    public async Task A_cut_off_with_a_full_window_names_the_window_not_max_tokens()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            new Turn(Text: "cd Tic", FinishReason: "length", PromptTokens: 7900, CompletionTokens: 18))
        {
            Window = 8192
        };

        var events = await fx.RunAsync(fx.Build(provider), "do the thing");

        var stopped = Assert.Single(events.OfKind(EventKind.ErrorObserved));
        Assert.Contains("num_ctx", stopped.Summary);
        Assert.Contains("7900", stopped.Summary);
        Assert.Contains("8192", stopped.Summary);
    }

    // And with no stated window it is the old answer, unchanged: that provider really does cap the
    // ANSWER, and sending someone to num_ctx would be sending them to a setting it does not have.
    [Fact]
    public async Task A_cut_off_without_a_stated_window_still_blames_the_output_budget()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            new Turn(Text: "half an ans", FinishReason: "max_tokens", PromptTokens: 200, CompletionTokens: 4096));

        var events = await fx.RunAsync(fx.Build(provider), "do the thing");

        var stopped = Assert.Single(events.OfKind(EventKind.ErrorObserved));
        Assert.Contains("token limit", stopped.Summary);
        Assert.DoesNotContain("num_ctx", stopped.Summary);
    }
}
