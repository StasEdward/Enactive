namespace Enactive.Engine.Tests;

using System.Net;
using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Providers;
using Enactive.Settings;
using Xunit;

public sealed class ModelBudgetReviewTests
{
    [Theory]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.OllamaNative)]
    public async Task Completion_deadline_covers_headers_and_body_and_never_retries(ProviderKind kind)
    {
        foreach (var body in new[] { false, true })
        {
            using var handler = new WaitingHandler(body);
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var provider = new ResilientChatProvider(Provider(http, kind, seconds: 1), delay: (_, _) => Task.CompletedTask);
            var error = await Assert.ThrowsAsync<TimeoutException>(() => provider.CompleteAsync(new("model", []), default));
            Assert.Contains("completion", error.Message);
            Assert.Equal(1, handler.Calls);
        }
    }

    [Theory]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.OllamaNative)]
    public async Task Stop_remains_cancellation_and_stream_headers_are_bounded(ProviderKind kind)
    {
        using var handler = new WaitingHandler(false);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var provider = Provider(http, kind, seconds: 1);
        using var stop = new CancellationTokenSource();
        var completion = provider.CompleteAsync(new("model", []), stop.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in provider.StreamChatAsync(new("model", []), default)) { }
        });
    }

    [Theory]
    [InlineData(ProviderKind.OpenAiCompatible)]
    [InlineData(ProviderKind.Anthropic)]
    [InlineData(ProviderKind.OllamaNative)]
    public void Reasoning_allowance_survives_wrappers_and_cannot_raise_configured_or_context_caps(ProviderKind kind)
    {
        using var http = new HttpClient();
        var descriptor = new ProviderDescriptor("test", "test", kind, "https://fixture.test", null, [],
            MaxTokens: 900, ContextWindowTokens: 3000, ReasoningTokenAllowance: 8192);
        var raw = Provider(http, descriptor);
        IChatProvider wrapped = new ExplainedChatProvider(new ResilientChatProvider(raw), "test", null);
        var request = GenerationAllowance.Fit(new("model", [ChatMessage.User("hello")],
            OutputTokenLimit: 4096, Purpose: GenerationPurpose.Action), wrapped);
        Assert.Equal(8192, wrapped.ReasoningAllowance(request));
        Assert.InRange(request.OutputTokenLimit!.Value, 128, 3000);
        Assert.Equal(900, OutputTokenBudget.Resolve(request, descriptor));
    }

    [Fact]
    public void Reasoning_defaults_require_a_profile_or_explicit_think_and_support_opt_out()
    {
        using var http = new HttpClient();
        var request = new ChatRequest("model", []);
        var d = new ProviderDescriptor("test", "test", ProviderKind.OpenAiCompatible, "https://fixture.test", null, []);
        Assert.Equal(0, Provider(http, d).ReasoningAllowance(request));
        Assert.Equal(8192, Provider(http, d with { OpenAiReasoningProfile = true }).ReasoningAllowance(request));
        Assert.Equal(0, Provider(http, d with { OpenAiReasoningProfile = true, ReasoningTokenAllowance = 0 }).ReasoningAllowance(request));
        Assert.Equal(8192, Provider(http, d with { Kind = ProviderKind.OllamaNative }).ReasoningAllowance(request with { Think = true }));
    }

    [Fact]
    public void Provider_budget_settings_clone_round_trip_and_reach_descriptors()
    {
        var config = new ProviderConfig { Id = "fixture", CompletionTimeoutSeconds = 1234, ReasoningTokenAllowance = 7654 };
        var copy = JsonSerializer.Deserialize<ProviderConfig>(JsonSerializer.Serialize(config.Clone()))!;
        var descriptor = Assert.Single(EngineComposition.Descriptors(new AppSettings { Providers = [copy] }));
        Assert.Equal(1234, descriptor.CompletionTimeoutSeconds);
        Assert.Equal(7654, descriptor.ReasoningTokenAllowance);
    }

    [Fact]
    public async Task Combined_review_budget_scales_with_claims_and_reports_no_context_as_incomplete()
    {
        var obligations = RequestObligations.Create(string.Join("\n", Enumerable.Range(1, 40).Select(i => $"Requirement {i}")));
        var journal = new ExecutionJournal();
        var provider = new FakeChatProvider(Turn.Says("invalid"), Turn.Says("invalid"));
        var review = new Reviewer();
        await review.ReviewWithProofAsync("title", "report", journal.Describe(), [], [], obligations, provider, "model", default);
        Assert.All(provider.Requests, r =>
        {
            Assert.Equal(GenerationPurpose.Review, r.Purpose);
            Assert.InRange(r.OutputTokenLimit!.Value, 40 * 512, 32768);
        });
        var tiny = new FakeChatProvider() { Window = 100 };
        var result = await review.ReviewWithProofAsync("title", "report", journal.Describe(), [], [], obligations, tiny, "model", default);
        Assert.NotNull(result.IncompleteReason);
        Assert.Empty(tiny.Requests);
    }

    [Fact]
    public async Task Truncated_combined_review_gets_one_larger_attempt_and_stays_incomplete()
    {
        var provider = new FakeChatProvider(new Turn("partial", FinishReason: "length"), new Turn("partial", FinishReason: "length"));
        var result = await new Reviewer().ReviewWithProofAsync("title", "report", new ExecutionJournal().Describe(), [], [],
            RequestObligations.Create("one obligation"), provider, "model", default);
        Assert.Equal(2, provider.Requests.Count);
        Assert.True(provider.Requests[1].OutputTokenLimit > provider.Requests[0].OutputTokenLimit);
        Assert.All(provider.Requests, r => Assert.InRange(r.OutputTokenLimit!.Value, 4096, 32768));
        Assert.False(result.Pass);
        Assert.Contains("output token limit", result.IncompleteReason);
    }

    [Fact]
    public async Task Handover_does_not_spend_when_exhausted_or_out_of_context()
    {
        var provider = new FakeChatProvider(Turn.Says("note")) { Window = 100 };
        Assert.Null(await new Handover().GenerateAsync(provider, new("model", [ChatMessage.User(new string('x', 1000))]), RunBudget.Unlimited(), default));
        var exhausted = new RunBudget(new(MaxTokens: 1), DateTimeOffset.UtcNow, tokensAlreadySpent: 1);
        Assert.Null(await new Handover().GenerateAsync(provider, new("model", []), exhausted, default));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Two_failed_handovers_use_bounded_engine_evidence_and_preserve_written_files()
    {
        using var fx = new EngineFixture();
        var turns = new List<Turn> { Turn.Says("""{"disposition":"quick_action","title":"write"}""") };
        for (var i = 0; i < 80; i++)
        {
            if (i is 60 or 70) turns.Add(new Turn("partial", FinishReason: "length"));
            turns.Add(Turn.Calls1("write_file", $$"""{"path":"n{{i}}.txt","content":"{{i}}"}"""));
        }
        turns.Add(Turn.Says("done"));
        var provider = new FakeChatProvider(turns.ToArray());
        await fx.RunAsync(fx.Build(provider), "write numbered files");
        var handovers = provider.Requests.Where(r => r.Purpose == GenerationPurpose.Handover).ToArray();
        Assert.Equal(2, handovers.Length);
        Assert.Equal(2048, handovers[0].OutputTokenLimit);
        Assert.Equal(4096, handovers[1].OutputTokenLimit);
        Assert.Contains(provider.Requests.SelectMany(r => r.Messages), m => m.Content?.Contains("Only the engine's measured evidence follows") == true);
        for (var i = 0; i < 80; i++) Assert.Equal(i.ToString(), fx.Read($"n{i}.txt"));
    }

    [Fact]
    public async Task Worker_and_handover_receive_reasoning_allowance_before_context_clamp()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"answer"}"""), Turn.Says("done"))
            { ReasoningTokens = 8192, Window = 65536 };
        await fx.RunAsync(fx.Build(worker), "answer");
        Assert.Equal(12288, worker.Requests.First(r => r.Purpose == GenerationPurpose.Action).OutputTokenLimit);
        var handover = new FakeChatProvider(Turn.Says("note")) { ReasoningTokens = 8192 };
        Assert.Equal("note", await new Handover().GenerateAsync(handover,
            new("model", [], OutputTokenLimit: 2048), RunBudget.Unlimited(), default));
        Assert.Equal(10240, Assert.Single(handover.Requests).OutputTokenLimit);
    }

    private static IChatProvider Provider(HttpClient http, ProviderKind kind, int seconds)
        => Provider(http, new("test", "test", kind, "https://fixture.test", null, [],
            StreamIdleTimeoutSeconds: seconds, CompletionTimeoutSeconds: seconds));
    private static IChatProvider Provider(HttpClient http, ProviderDescriptor descriptor) => descriptor.Kind switch
    {
        ProviderKind.Anthropic => new AnthropicProvider(http, descriptor),
        ProviderKind.OllamaNative => new OllamaNativeProvider(http, descriptor),
        _ => new OpenAiCompatibleProvider(http, descriptor)
    };
    private sealed class WaitingHandler(bool body) : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Entered.TrySetResult();
            if (!body) await Task.Delay(Timeout.Infinite, ct);
            return new(HttpStatusCode.OK) { Content = new WaitingContent() };
        }
    }
    private sealed class WaitingContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new InvalidOperationException("Expected cancellable body read");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
            => Task.Delay(Timeout.Infinite, ct);
    }
}
