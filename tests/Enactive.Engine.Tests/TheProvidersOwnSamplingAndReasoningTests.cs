namespace Enactive.Engine.Tests;

using System.Net;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Enactive.Providers;
using Enactive.Settings;
using Xunit;

/// <summary>
/// Run fba4d6, 2026-09-29: a local server started with the model maker's sampling (llama.cpp --temperature 0.6) was sent
/// 0.2 in every request, which replaces it; and started with --reasoning-preserve, it had nothing to preserve, because the
/// reasoning was read and never sent back. A provider now says which temperature its models get - the engine's, its own,
/// or none, the server's - and whether the model's reasoning goes back with its earlier turns.
/// </summary>
public sealed class TheProvidersOwnSamplingAndReasoningTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""") };
        }
    }

    private static async Task<JsonDocument> Sent(ProviderDescriptor descriptor, IReadOnlyList<ChatMessage> messages, double? temperature = 0.2)
    {
        using var handler = new Capture();
        using var http = new HttpClient(handler);
        await new OpenAiCompatibleProvider(http, descriptor).CompleteAsync(new("model", messages, Temperature: temperature), default);
        return JsonDocument.Parse(handler.Body!);
    }

    private static ProviderDescriptor Local(double? temperature = null, bool server = false, bool reasoning = false)
        => new("local", "local", ProviderKind.OpenAiCompatible, "http://127.0.0.1:8080/v1", null, [],
            Temperature: temperature, ServerTemperature: server, SendReasoningBack: reasoning);

    [Fact]
    public async Task The_temperature_is_the_engines_the_providers_or_none()
    {
        ChatMessage[] hello = [ChatMessage.User("hello")];
        using (var engine = await Sent(Local(), hello))
            Assert.Equal(0.2, engine.RootElement.GetProperty("temperature").GetDouble());
        using (var own = await Sent(Local(temperature: 0.6), hello))
            Assert.Equal(0.6, own.RootElement.GetProperty("temperature").GetDouble());
        using (var server = await Sent(Local(server: true), hello))
            Assert.False(server.RootElement.TryGetProperty("temperature", out _));
    }

    private sealed class OllamaCapture : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}""")
            };
        }
    }

    private static async Task<JsonElement?> OllamaTemperature(double? own = null, bool server = false)
    {
        using var handler = new OllamaCapture();
        using var http = new HttpClient(handler);
        var descriptor = new ProviderDescriptor("ollama", "ollama", ProviderKind.OllamaNative, "http://127.0.0.1:11434", null, [],
            Temperature: own, ServerTemperature: server);
        await new OllamaNativeProvider(http, descriptor).CompleteAsync(new("model", [ChatMessage.User("hello")], Temperature: 0.2), default);
        using var sent = JsonDocument.Parse(handler.Body!);
        return sent.RootElement.TryGetProperty("options", out var options) && options.TryGetProperty("temperature", out var t)
            ? t.Clone() : null;
    }

    /// <summary>
    /// The native Ollama adapter reads the provider's temperature as the other two do. It sent the engine's whatever the
    /// provider said, so the field in the provider window did nothing for an Ollama model.
    /// </summary>
    [Fact]
    public async Task An_ollama_model_gets_the_providers_temperature_too()
    {
        Assert.Equal(0.2, (await OllamaTemperature())!.Value.GetDouble());
        Assert.Equal(0.6, (await OllamaTemperature(own: 0.6))!.Value.GetDouble());
        Assert.Null(await OllamaTemperature(server: true));
    }

    [Fact]
    public async Task The_reasoning_goes_back_with_the_models_own_turns_only_where_the_provider_is_told_to()
    {
        ChatMessage[] history =
        [
            ChatMessage.User("list the pages"),
            new ChatMessage(ChatRole.Assistant, null, [new ToolCall("c1", "list_dir", """{"path":"wiki"}""")]) { Reasoning = "look in wiki first" },
            ChatMessage.Tool("c1", "home.md"),
            new ChatMessage(ChatRole.Assistant, "One page.") { Reasoning = "only one" }
        ];

        using (var off = await Sent(Local(), history))
            Assert.DoesNotContain("reasoning_content", off.RootElement.GetRawText(), StringComparison.Ordinal);

        using var on = await Sent(Local(reasoning: true), history);
        var sent = on.RootElement.GetProperty("messages");
        Assert.Equal("look in wiki first", sent[1].GetProperty("reasoning_content").GetString());
        Assert.Equal("list_dir", sent[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("only one", sent[3].GetProperty("reasoning_content").GetString());
        Assert.False(sent[0].TryGetProperty("reasoning_content", out _));
    }

    [Theory]
    [InlineData("", null, false)]
    [InlineData("0.6", 0.6, false)]
    [InlineData("server", null, true)]
    [InlineData("SERVER", null, true)]
    [InlineData("hot", null, false)]
    public void The_setting_reads_as_the_engines_a_number_or_the_servers(string text, double? temperature, bool server)
    {
        var settings = new AppSettings();
        settings.Providers.Add(new ProviderConfig { Id = "local", Kind = ProviderKind.OpenAiCompatible, BaseUrl = "http://127.0.0.1:8080/v1",
            Temperature = text, SendReasoningBack = true });

        var descriptor = EngineComposition.Descriptors(settings).Single(d => d.Id == "local");

        Assert.Equal(temperature, descriptor.Temperature);
        Assert.Equal(server, descriptor.ServerTemperature);
        Assert.True(descriptor.SendReasoningBack);
    }

    [Fact]
    public async Task The_models_reasoning_is_kept_with_its_turn()
    {
        using var fx = new EngineFixture();
        fx.Write("sums.txt", "6 x 7 = 42");
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"answer"}"""),
            new Turn(null, [new ToolCall("r1", "read_file", """{"path":"sums.txt"}""")], Thinking: "six times seven"),
            Turn.Says("It is forty-two."));

        await fx.RunAsync(fx.Build(worker), "what is six times seven");

        var turn = Assert.Single(worker.Requests[2].Messages, m => m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 });
        Assert.Equal("six times seven", turn.Reasoning);
    }
}
