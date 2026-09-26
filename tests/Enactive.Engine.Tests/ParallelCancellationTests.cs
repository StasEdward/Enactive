namespace Enactive.Engine.Tests;

using System.Runtime.CompilerServices;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Intents;
using Enactive.Core.Providers;
using Xunit;

public sealed class ParallelCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposing_the_event_stream_waits_for_all_running_steps(bool quick)
    {
        using var fx = new EngineFixture();
        var provider = new WaitingProvider(quick);
        var engine = fx.Build(provider, maxParallelSteps: 2);
        var context = new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []);
        var intent = new Intent(Guid.NewGuid(), "do both", IntentSource.CommandBar, context, DateTimeOffset.UtcNow);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sawDelta = false;
        await foreach (var ev in engine.SubmitIntentAsync(intent, timeout.Token))
        {
            if (ev.Kind != EventKind.AssistantDelta) continue;
            sawDelta = true;
            break;
        }
        Assert.True(sawDelta);
        Assert.Equal(0, provider.Active);
        Assert.Equal(quick ? 1 : 2, provider.Disposed);
    }

    private sealed class WaitingProvider(bool quick) : IChatProvider
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active;
        public int Disposed;
        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
            => Task.FromResult(new ChatCompletion(ChatMessage.Assistant(quick ? """{"disposition":"quick_action","title":"quick"}""" : """
                {"disposition":"task","title":"parallel","steps":[
                {"title":"left","dependsOn":[]},{"title":"right","dependsOn":[]}]}
                """), "stop", null, null));

        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (Interlocked.Increment(ref Active) == (quick ? 1 : 2)) _both.TrySetResult();
            try
            {
                await _both.Task.WaitAsync(ct);
                yield return new TextDelta("working");
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                // Model transport cleanup can outlive the cancellation signal.
                await Task.Delay(100, CancellationToken.None);
                Interlocked.Decrement(ref Active);
                Interlocked.Increment(ref Disposed);
            }
        }
    }
}
