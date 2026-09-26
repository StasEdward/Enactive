namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Mail;
using Enactive.Core.Tools;
using Enactive.Tools;

public sealed class EngineComponentRefactoringTests
{
    private static ModelTurn.Signal[] Observe(ModelTurn turn, ChatStreamEvent delta)
    {
        var observation = turn.Observe(delta);
        return new[] { observation.Visible, observation.Progress }.Where(s => s.HasValue).Select(s => s!.Value).ToArray();
    }

    [Fact]
    public void Stream_assembly_keeps_interleaved_arguments_verbatim_and_reasoning_separate()
    {
        var turn = new ModelTurn();
        Assert.Empty(Observe(turn, new ToolCallDelta(2, "later", "write_file", "{\"content\":\"")));
        Assert.Empty(Observe(turn, new ToolCallDelta(0, "first", "read_file", "{\"path\":\"a\"}")));
        Assert.Empty(Observe(turn, new ToolCallDelta(2, null, null, "x\\ny\"}")));
        Assert.Empty(Observe(turn, new ReasoningDelta("private draft")));
        var visible = Assert.Single(Observe(turn, new TextDelta("visible")));
        Assert.Equal(EventKind.AssistantDelta, visible.Kind);
        Assert.Equal("visible", visible.Text);
        Assert.Equal("visible", turn.Content.ToString());
        Assert.Equal("private draft", turn.Reasoning.ToString());
        var calls = turn.BuildCalls()!;
        Assert.Equal(new[] { "first", "later" }, calls.Select(c => c.Id));
        Assert.Equal("{\"content\":\"x\\ny\"}", calls[1].ArgumentsJson);
        Assert.Empty(Observe(turn, new FinishDelta("length")));
        Assert.Equal("length", turn.FinishReason);
    }

    [Fact]
    public void Progress_follows_visible_delta_and_counts_each_channel_independently()
    {
        var turn = new ModelTurn();
        var first = Observe(turn, new TextDelta(new string('x', 2000))).ToArray();
        Assert.Equal(new[] { EventKind.AssistantDelta, EventKind.GenerationProgress }, first.Select(e => e.Kind));
        Assert.Single(Observe(turn, new TextDelta("small")));
        Assert.Empty(Observe(turn, new ReasoningDelta(new string('r', 1999))));
        Assert.Equal(EventKind.GenerationProgress, Assert.Single(Observe(turn, new ReasoningDelta("r"))).Kind);
        Assert.Equal(EventKind.GenerationProgress, Assert.Single(Observe(turn, new ToolCallDelta(0, "call", "write_file", new string('a', 2000)))).Kind);
        Assert.Equal(2000, Assert.Single(turn.BuildCalls()!).ArgumentsJson.Length);
        Assert.Null(new ModelTurn().BuildCalls());
        Assert.Null(new ModelTurn().Stopped);
    }

    [Fact]
    public void Runaway_stops_prose_but_does_not_truncate_tool_arguments_or_reasoning()
    {
        var text = new string('x', RunawayReply.MaxTextChars + 1);
        var prose = new ModelTurn();
        _ = Observe(prose, new TextDelta(text)).ToArray();
        Assert.NotNull(prose.Stopped);
        var tool = new ModelTurn();
        _ = Observe(tool, new ToolCallDelta(0, "call", "write_file", text)).ToArray();
        _ = Observe(tool, new ReasoningDelta(text)).ToArray();
        _ = Observe(tool, new TextDelta(text)).ToArray();
        Assert.Null(tool.Stopped);
        Assert.Equal(text, Assert.Single(tool.BuildCalls()!).ArgumentsJson);
    }

    [Fact]
    public async Task Evidence_uses_matching_command_result_and_keeps_pending_separate_from_disk()
    {
        using var fx = new EngineFixture();
        var tools = new ToolRegistry(BuiltInTools.Create(MailAccount.None));
        ChatMessage[] messages =
        [
            ChatMessage.Assistant(null, [new("command", "run_command", """{"command":"build"}""")]),
            ChatMessage.Tool("command", "exit code 1\nFAILED"),
            ChatMessage.Assistant(null, [new("read", "read_file", """{"path":"result.txt"}""")]),
            ChatMessage.Tool("read", "unrelated success")
        ];
        var evidence = await HandoverEvidence.CaptureAsync(tools, null, null, messages,
            ["pending.txt"], [], fx.Root, default);
        Assert.Contains("NOT on disk yet", evidence);
        Assert.Contains("pending.txt", evidence);
        Assert.Contains("build", evidence);
        Assert.Contains("FAILED", evidence);
        Assert.DoesNotContain("unrelated success", evidence);
        Assert.DoesNotContain("No file", evidence); // no snapshot means unknown, not unchanged
    }

    [Fact]
    public void Catalog_creates_fresh_tools_and_preserves_mail_policy_per_configuration()
    {
        var unavailable = BuiltInTools.Create(MailAccount.None);
        var configured = BuiltInTools.Create(new("smtp.test", 587, true, "sender@test", "", "sender@test", ["recipient@test"])
            { SendWithoutAsking = true });
        Assert.Equal(unavailable.Select(t => t.Definition.Name), configured.Select(t => t.Definition.Name));
        Assert.Equal(unavailable.Length, unavailable.Select(t => t.Definition.Name).Distinct().Count());
        Assert.Contains(unavailable, t => t.Definition.Name == "read_file");
        Assert.Contains(unavailable, t => t.Definition.Name == "read_files");
        for (var i = 0; i < unavailable.Length; i++) Assert.NotSame(unavailable[i], configured[i]);
        var oldMail = unavailable.Single(t => t.Definition.Name == "send_email");
        var newMail = configured.Single(t => t.Definition.Name == "send_email");
        Assert.Contains("NOT AVAILABLE", oldMail.Definition.Description);
        Assert.Contains("recipient@test", newMail.Definition.Description);
        Assert.True(oldMail.RequiresApproval);
        Assert.False(newMail.RequiresApproval);
    }
}
