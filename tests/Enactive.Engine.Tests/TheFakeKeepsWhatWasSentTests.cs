namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// The fake provider records each request AS IT WAS SENT: the engine goes on growing, trimming and
/// clearing the conversation it passed, and a recorded request must not follow it.
///
/// <para><b>Found 2026-09-24</b> (Docs/PROVIDERS_AGENTS_TOOLS_TESTS_REVIEW_2026-09-24.md #2). Once
/// the fakes snapshot, two tests turned out to have passed on history written after the turn they
/// were about: a retry check that read the wrong turn, and a handover check whose resumed message
/// was never sent at all - the step had stopped with a full window first.</para>
/// </summary>
public sealed class TheFakeKeepsWhatWasSentTests
{
    [Fact]
    public async Task A_recorded_request_does_not_change_when_the_conversation_does()
    {
        var calls = new List<ToolCall> { new("c1", "read_file", """{"path":"a.md"}""") };
        var conversation = new List<ChatMessage>
        {
            ChatMessage.System("SYSTEM"),
            new(ChatRole.Assistant, null, calls)
        };
        var fake = new FakeChatProvider { WhenExhausted = Turn.Says("ok") };

        await fake.CompleteAsync(new ChatRequest("m", conversation), CancellationToken.None);

        // What the engine does next: appends, replaces, and changes a call list it still holds.
        conversation.Add(ChatMessage.User("A LATER TURN"));
        conversation[0] = ChatMessage.System("REPLACED");
        calls.Add(new ToolCall("c2", "write_file", "{}"));

        var sent = Assert.Single(fake.Requests);
        Assert.Equal(2, sent.Messages.Count);
        Assert.Equal("SYSTEM", sent.Messages[0].Content);
        Assert.Single(sent.Messages[1].ToolCalls!);
    }
}
