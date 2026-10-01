namespace Enactive.Engine.Tests;

using System.Net;
using System.Text;
using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;
using Enactive.Core.Providers;
using Enactive.Providers;
using Enactive.Workspace;

public sealed class BoundedWireLoggingTests
{
    [Fact]
    public void Disabled_logging_does_not_create_a_stream_buffer()
    {
        using var hub = new LogHub(minLevel: LogLevel.Info);
        Assert.Null(BoundedLogBuffer.Create(null, LogLevel.Trace));
        Assert.Null(BoundedLogBuffer.Create(NullLogSink.Instance, LogLevel.Trace));
        Assert.Null(BoundedLogBuffer.Create(hub, LogLevel.Trace));
        hub.MinLevel = LogLevel.Trace;
        Assert.NotNull(BoundedLogBuffer.Create(hub, LogLevel.Trace));
    }

    [Theory]
    [InlineData(ProviderKind.OpenAiCompatible, false)]
    [InlineData(ProviderKind.OpenAiCompatible, true)]
    [InlineData(ProviderKind.OllamaNative, false)]
    [InlineData(ProviderKind.OllamaNative, true)]
    public async Task Wire_capture_is_optional_and_bounded_without_changing_the_answer(ProviderKind kind, bool trace)
    {
        const int chunks = 5000;
        var line = kind == ProviderKind.OllamaNative
            ? "{\"message\":{\"content\":\"hello\"},\"done\":false}\n"
            : "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n";
        var end = kind == ProviderKind.OllamaNative ? "{\"done\":true}\n" : "data: [DONE]\n\n";
        var wire = string.Concat(Enumerable.Repeat(line, chunks)) + end;
        var log = new Recorder(trace ? LogLevel.Trace : LogLevel.Info);
        using var http = new HttpClient(new Handler(wire));
        var provider = Provider(http, kind, log);
        var texts = 0;
        await foreach (var evt in provider.StreamChatAsync(Request(), CancellationToken.None))
            if (evt is TextDelta text)
            {
                Assert.Equal("hello", text.Text);
                texts++;
            }

        Assert.Equal(chunks, texts);
        if (!trace)
            Assert.Empty(log.Entries);
        else
        {
            var response = Assert.Single(log.Entries, e => e.Category == "wire:response-stream");
            Assert.True(response.Detail!.Length * 2 <= LogPayload.MaxEntryBytes);
            Assert.Contains(LogPayload.Truncated, response.Detail);
            Assert.StartsWith(line.Split('\n')[0], response.Detail);
        }
    }

    [Theory]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.OllamaNative)]
    public async Task Disposing_a_partial_stream_keeps_the_trace_prefix(ProviderKind kind)
    {
        var wire = kind == ProviderKind.OllamaNative
            ? "{\"message\":{\"content\":\"hello\"},\"done\":false}\n"
            : "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n";
        var log = new Recorder(LogLevel.Trace);
        using var http = new HttpClient(new Handler(wire));
        await using (var stream = Provider(http, kind, log).StreamChatAsync(Request(), CancellationToken.None).GetAsyncEnumerator())
            Assert.True(await stream.MoveNextAsync());
        var entry = Assert.Single(log.Entries, e => e.Category == "wire:response-stream");
        Assert.Contains("hello", entry.Detail);
        Assert.DoesNotContain(LogPayload.Truncated, entry.Detail);
    }

    [Fact]
    public void A_single_large_chunk_is_bounded_and_surrogate_pairs_are_not_split()
    {
        var log = new Recorder(LogLevel.Trace);
        var buffer = BoundedLogBuffer.Create(log, LogLevel.Trace, 512)!;
        buffer.AppendLine(string.Concat(Enumerable.Repeat("😀", 10000)));
        var text = buffer.ToString();
        Assert.True(text.Length * 2 <= 512);
        Assert.EndsWith(LogPayload.Truncated, text);
        Assert.DoesNotContain("�", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.OllamaNative)]
    public async Task Turning_trace_off_during_a_stream_discards_the_capture(ProviderKind kind)
    {
        var line = kind == ProviderKind.OllamaNative
            ? "{\"message\":{\"content\":\"hello\"},\"done\":false}\n"
            : "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n";
        var end = kind == ProviderKind.OllamaNative ? "{\"done\":true}\n" : "data: [DONE]\n\n";
        using var hub = new LogHub();
        using var http = new HttpClient(new Handler(line + line + end));
        var seen = 0;
        await foreach (var evt in Provider(http, kind, hub).StreamChatAsync(Request(), CancellationToken.None))
        {
            if (evt is not TextDelta) continue;
            hub.MinLevel = ++seen == 1 ? LogLevel.Info : LogLevel.Trace;
        }
        Assert.Equal(2, seen);
        Assert.DoesNotContain(hub.Snapshot(), e => e.Category == "wire:response-stream");
    }

    [Fact]
    public void History_is_bounded_by_bytes_and_eviction_invalidates_prompt_references()
    {
        using var hub = new LogHub(maxPayloadBytes: 1024);
        var first = new string('a', 350);
        hub.Info(LogSource.Prompt, "first", first);
        Assert.True(hub.HoldsAll(new[] { first }));
        hub.Info(LogSource.Prompt, "second", new string('b', 350));
        Assert.False(hub.HoldsAll(new[] { first }));
        Assert.Single(hub.Snapshot());
        Assert.True(hub.RetainedPayloadBytes <= 1024);
        hub.Info(LogSource.Llm, "huge", new string('x', 100000));
        var clipped = Assert.Single(hub.Snapshot());
        Assert.Contains(LogPayload.Truncated, clipped.Detail);
        Assert.True(LogPayload.Bytes(clipped) <= 1024);
        hub.Clear();
        Assert.Empty(hub.Snapshot());
        Assert.Equal(0, hub.RetainedPayloadBytes);
    }

    [Fact]
    public async Task A_slow_subscriber_cannot_retain_an_unbounded_queue()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var hub = new LogHub(maxPayloadBytes: 1024);
        hub.Entry += _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        try
        {
            hub.Info(LogSource.System, "block pump");
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            Parallel.For(0, 100, i => hub.Info(LogSource.Llm, i.ToString(), new string('x', 350)));
            Assert.InRange(hub.PendingPayloadBytes, 1, 1024);
            Assert.InRange(hub.RetainedPayloadBytes, 1, 1024);
            Assert.True(hub.Snapshot().Sum(LogPayload.Bytes) <= 1024);
        }
        finally { release.Set(); }
    }

    private static ChatRequest Request() => new("model", new[] { ChatMessage.User("hi") });
    private static IChatProvider Provider(HttpClient http, ProviderKind kind, ILogSink log)
    {
        var descriptor = new ProviderDescriptor("p", "p", kind, "https://provider.test", null, new[] { "model" });
        return kind == ProviderKind.OllamaNative
            ? new OllamaNativeProvider(http, descriptor, log)
            : new OpenAiCompatibleProvider(http, descriptor, log);
    }

    private sealed class Recorder(LogLevel minimum) : ILogSink
    {
        public List<LogEntry> Entries { get; } = new();
        public bool IsEnabled(LogLevel level) => level >= minimum;
        public void Log(LogEntry entry) => Entries.Add(entry);
    }

    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
