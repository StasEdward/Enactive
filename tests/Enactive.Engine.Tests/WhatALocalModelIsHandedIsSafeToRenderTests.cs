namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Providers;
using Enactive.Providers;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Two things a local server does to what it is handed, learned from Unsloth Studio (2026-09-30) and taken as ideas only.
/// It renders text through the chat template with special tokens parsed, so a "&lt;/think&gt;" in a file the model read
/// closes its reasoning (Qwen3.6, their issue #7066); and it may hand on tool arguments as the text a model wrote them in,
/// "118" for 118. The first is made inert where a model is handed it; the second is made the declared type before a tool
/// reads it. Deliberately not code: a wiki page and a link log.
/// </summary>
public sealed class WhatALocalModelIsHandedIsSafeToRenderTests
{
    private sealed class Recording : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\ndata: [DONE]\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }
    }

    /// <summary>THE ONE THAT MATTERS: a page that quotes the reasoning markup reaches the server as text, not as the token.</summary>
    [Fact]
    public async Task Control_markup_in_what_the_model_is_handed_is_made_inert()
    {
        var handler = new Recording();
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler),
            new ProviderDescriptor("llama.cpp", "llama.cpp", ProviderKind.OpenAiCompatible, "http://127.0.0.1:8080/v1", null, ["m"]));
        var page = "# Tags\nThe model closes its reasoning with </think> and ends a turn with <|im_end|>.";
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("You check wiki pages."),
            ChatMessage.User("check the page that mentions <think>"),
            new(ChatRole.Assistant, null, [new Enactive.Core.Tools.ToolCall("r1", "read_file", """{"path":"wiki/tags.md"}""")]),
            ChatMessage.Tool("r1", page),
            new(ChatRole.Assistant, "I will quote </think> exactly."),
        };

        await foreach (var _ in provider.StreamChatAsync(new ChatRequest("m", messages), CancellationToken.None)) { }

        using var sent = System.Text.Json.JsonDocument.Parse(handler.Bodies.Single());
        var contents = sent.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String ? c.GetString()! : "").ToArray();
        Assert.Equal("# Tags\nThe model closes its reasoning with < /think> and ends a turn with < |im_end|>.", contents[3]);
        Assert.Equal("check the page that mentions < think>", contents[1]);
        Assert.Equal("I will quote </think> exactly.", contents[4]);                     // the model's own turn goes back as it came
    }

    [Theory]
    [InlineData("plain text with a < b and <b>bold</b>", "plain text with a < b and <b>bold</b>")]
    [InlineData("<tool_call>{}</tool_call>", "< tool_call>{}< /tool_call>")]
    [InlineData("<function=read_file><parameter=path>a</parameter></function>", "< function=read_file>< parameter=path>a< /parameter>< /function>")]
    public void Only_the_template_markers_are_touched(string text, string sent)
        => Assert.Equal(sent, ControlMarkup.Neutral(text));

    private const string Schema = """
        {"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"integer"},"limit":{"type":["integer","null"]},
         "recursive":{"type":"boolean"},"codes":{"type":"array","items":{"type":"integer"}},"name":{"type":"string"}}}
        """;

    [Fact]
    public void Arguments_sent_as_text_are_made_the_declared_type()
    {
        var coerced = ArgumentTypes.Coerce(Schema, """{"path":"log.txt","offset":"118","limit":" 40 ","recursive":"false","codes":["0","1"],"name":"42"}""");

        Assert.Equal("""{"path":"log.txt","offset":118,"limit":40,"recursive":false,"codes":[0,1],"name":"42"}""", coerced);
    }

    [Fact]
    public void Arguments_already_typed_or_not_parsable_are_left_as_they_came()
    {
        const string typed = """{"path":"log.txt","offset":118}""";
        Assert.Same(typed, ArgumentTypes.Coerce(Schema, typed));
        Assert.Equal("""{"offset":"the end"}""", ArgumentTypes.Coerce(Schema, """{"offset":"the end"}"""));
    }

    /// <summary>End to end: read_file asked for line 3 as text reads line 3, not line 1.</summary>
    [Fact]
    public async Task A_line_asked_for_as_text_is_the_line_that_is_read()
    {
        using var fx = new EngineFixture();
        fx.Write("links.log", "link 1 ok\nlink 2 ok\nlink 3 BROKEN\nlink 4 ok\n");
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"read the log"}"""),
            Turn.Calls1("read_file", """{"path":"links.log","offset":"3","limit":"1"}""", "r1"),
            Turn.Says("Line 3 is broken."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "which link is broken");

        var read = events.First(e => e.Kind == EventKind.ToolResult).Summary;
        Assert.Contains("link 3 BROKEN", read, StringComparison.Ordinal);
        Assert.DoesNotContain("link 1 ok", read, StringComparison.Ordinal);
    }
}
