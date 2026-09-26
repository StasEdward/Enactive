namespace Enactive.Engine.Tests;

using System.Net;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Enactive.Providers;
using Enactive.Settings;

public sealed class ProviderWireHygieneTests
{
    public static IEnumerable<object[]> InvalidReplies()
    {
        foreach (var kind in Enum.GetValues<ProviderKind>())
        foreach (var stream in new[] { false, true })
        foreach (var body in new[] { "<html>private proxy error</html>", "{broken", "[]" })
            yield return [kind, stream, body];
    }

    [Theory]
    [MemberData(nameof(InvalidReplies))]
    public async Task Malformed_response_has_context_and_is_not_retried(ProviderKind kind, bool stream, string body)
    {
        var wire = stream && kind != ProviderKind.OllamaNative && !body.StartsWith('<') ? "data: " + body + "\n\n" : body;
        using var handler = new Capture(wire);
        using var http = new HttpClient(handler);
        var descriptor = Descriptor(kind) with { BaseUrl = "https://user:secret@proxy.test/v1?token=private#secret" };
        var provider = new ResilientChatProvider(Build(http, descriptor));
        var request = new ChatRequest("model-a", [ChatMessage.User("hi")]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            if (stream) await foreach (var _ in provider.StreamChatAsync(request, default)) { }
            else await provider.CompleteAsync(request, default);
        });
        Assert.Contains("provider-a", error.Message);
        Assert.Contains("model-a", error.Message);
        Assert.Contains("https://proxy.test/v1", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain("private", error.Message);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1800)]
    public async Task Ollama_sends_configured_keep_alive_and_pairs_results_by_id(int? seconds)
    {
        using var handler = new Capture("{\"message\":{\"content\":\"ok\"},\"done\":true}\n");
        using var http = new HttpClient(handler);
        var provider = Build(http, Descriptor(ProviderKind.OllamaNative) with { OllamaKeepAliveSeconds = seconds });
        var request = new ChatRequest("model-a", [
            ChatMessage.Assistant(null, [new ToolCall("a", "read_file", "{}"), new ToolCall("b", "list_dir", "{}")]),
            ChatMessage.Tool("b", "directory"), ChatMessage.Tool("a", "file"),
            ChatMessage.Tool("missing", "orphan"),
            new ChatMessage(ChatRole.Tool, "named", Name: "search_files")]);
        await provider.CompleteAsync(request, default);
        await foreach (var _ in provider.StreamChatAsync(request, default)) { }
        foreach (var json in handler.Bodies)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (seconds is { } value) Assert.Equal(value, root.GetProperty("keep_alive").GetInt32());
            else Assert.False(root.TryGetProperty("keep_alive", out _));
            var messages = root.GetProperty("messages");
            Assert.Equal("list_dir", messages[1].GetProperty("tool_name").GetString());
            Assert.Equal("read_file", messages[2].GetProperty("tool_name").GetString());
            Assert.False(messages[3].TryGetProperty("tool_name", out _));
            Assert.Equal("search_files", messages[4].GetProperty("tool_name").GetString());
        }
        var config = new ProviderConfig { OllamaKeepAliveSeconds = seconds };
        Assert.Equal(seconds, config.Clone().OllamaKeepAliveSeconds);
        Assert.Equal(seconds, JsonSerializer.Deserialize<ProviderConfig>(JsonSerializer.Serialize(config))!.OllamaKeepAliveSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Openai_keeps_call_ids_and_argument_strings_without_native_fields(bool stream)
    {
        using var handler = new Capture(stream ? "data: [DONE]\n\n"
            : "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}");
        using var http = new HttpClient(handler);
        var provider = Build(http, Descriptor(ProviderKind.OpenAiCompatible) with { OllamaKeepAliveSeconds = 1800 });
        const string arguments = "{ \"path\" : \"a.cs\" }";
        var request = new ChatRequest("model-a", [
            ChatMessage.Assistant(null, [new ToolCall("a", "read_file", arguments), new ToolCall("b", "list_dir", "{}")]),
            ChatMessage.Tool("b", "directory"), ChatMessage.Tool("a", "file")],
            Tools: [new ToolDefinition("read_file", "Read", "{\"type\":\"object\"}")],
            NumCtx: 8192, MaxTokens: 17, ResponseSchema: "{\"type\":\"object\"}");
        if (stream) await foreach (var _ in provider.StreamChatAsync(request, default)) { }
        else await provider.CompleteAsync(request, default);
        using var doc = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("keep_alive", out _));
        Assert.False(root.TryGetProperty("options", out _));
        Assert.Equal(17, root.GetProperty("max_tokens").GetInt32());
        var messages = root.GetProperty("messages");
        Assert.Equal("b", messages[1].GetProperty("tool_call_id").GetString());
        Assert.Equal("a", messages[2].GetProperty("tool_call_id").GetString());
        Assert.False(messages[1].TryGetProperty("tool_name", out _));
        Assert.Equal(arguments, messages[0].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("object", root.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal("object", root.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Cached_schema_is_shared_by_concurrent_requests_but_not_record_copies()
    {
        var tool = new ToolDefinition("read", "Read", "{\"type\":\"object\"}");
        var values = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => WireJson.Schema(tool))));
        Assert.All(values, value => Assert.Equal(values[0], value));
        var changed = WireJson.Schema(tool with { JsonSchema = "{\"type\":\"string\"}" });
        Assert.Equal("string", changed.GetProperty("type").GetString());
        var call = new ToolCall("1", "read", "{\"path\":\"a\"}");
        Assert.Equal(WireJson.ArgumentsOf(call), WireJson.ArgumentsOf(call));
        Assert.Equal("b", WireJson.ArgumentsOf(call with { ArgumentsJson = "{\"path\":\"b\"}" }).GetProperty("path").GetString());
    }

    private static ProviderDescriptor Descriptor(ProviderKind kind)
        => new("provider-a", "Test", kind, "https://proxy.test", null, []);

    private static IChatProvider Build(HttpClient http, ProviderDescriptor descriptor) => descriptor.Kind switch
    {
        ProviderKind.OllamaNative => new OllamaNativeProvider(http, descriptor),
        ProviderKind.Anthropic => new AnthropicProvider(http, descriptor),
        _ => new OpenAiCompatibleProvider(http, descriptor)
    };

    private sealed class Capture(string response) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }
}
