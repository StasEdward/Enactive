namespace Enactive.Engine.Tests;

using System.Runtime.CompilerServices;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Providers;
using Enactive.Core.Templates;
using Enactive.Providers;
using Xunit;

public sealed class RetryBudgetIntegrationTests
{
    [Theory]
    [InlineData("planning")]
    [InlineData("quick")]
    [InlineData("dag")]
    public async Task Exhaustion_during_transport_failure_is_incomplete_without_another_attempt(string phase)
    {
        using var fx = new EngineFixture();
        var inner = new ExhaustingProvider(phase);
        var waits = 0;
        var provider = new ResilientChatProvider(inner, delay: (_, _) =>
        { waits++; return Task.CompletedTask; });
        var events = await fx.RunAsync(fx.Build(provider, limits: new ExecutionLimits(MaxTokens: 10)), "work");
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Contains("token(s)", events.Last().OutcomeReason());
        Assert.Equal(1, inner.Failures);
        Assert.Equal(0, waits);
    }

    private sealed class ExhaustingProvider(string phase) : IChatProvider
    {
        public int Failures;
        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
        {
            if (phase == "planning") Fail(request);
            return Task.FromResult(new ChatCompletion(ChatMessage.Assistant(phase == "quick"
                ? """{"disposition":"quick_action","title":"work","steps":[]}"""
                : """{"disposition":"task","title":"work","steps":[{"title":"one","dependsOn":[]}]}"""), "stop", null, null));
        }
        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Fail(request);
            yield break;
        }
        private void Fail(ChatRequest request)
        {
            Assert.NotNull(request.RetryBudget);
            request.RetryBudget.TokensUsed(10, 0);
            Failures++;
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "connection ended");
        }
    }
}
