namespace Enactive.Engine.Tests;

using System.Runtime.CompilerServices;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Intents;
using Enactive.Core.Providers;

public sealed class EngineBackpressureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Slow_consumer_bounds_production_and_can_drain_or_dispose(bool quick, bool dispose)
    {
        using var fx = new EngineFixture();
        var provider = new Burst(quick);
        var engine = fx.Build(provider);
        var context = new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []);
        var intent = new Intent(Guid.NewGuid(), "answer", IntentSource.CommandBar, context, DateTimeOffset.UtcNow);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = engine.SubmitIntentAsync(intent, timeout.Token).GetAsyncEnumerator();
        var received = new List<WorkEvent>();
        try
        {
            while (await reader.MoveNextAsync())
            {
                received.Add(reader.Current);
                if (reader.Current.Kind == EventKind.AssistantDelta) break;
            }
            await provider.Filled.Task.WaitAsync(timeout.Token);
            Assert.InRange(Volatile.Read(ref provider.Produced), 129, 130);
            if (!dispose)
            {
                while (await reader.MoveNextAsync()) received.Add(reader.Current);
                Assert.Equal(1000, received.Count(e => e.Kind == EventKind.AssistantDelta));
                Assert.Single(received, e => e.Kind == EventKind.TaskCompleted);
            }
        }
        finally { await reader.DisposeAsync(); }
        Assert.True(provider.Disposed);
    }

    private sealed class Burst(bool quick) : IChatProvider
    {
        public int Produced;
        public bool Disposed;
        public TaskCompletionSource Filled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
            => Task.FromResult(new ChatCompletion(ChatMessage.Assistant(quick
                ? """{"disposition":"quick_action","title":"answer"}"""
                : """{"disposition":"task","title":"answer","steps":[{"title":"answer","dependsOn":[]}]}"""), "stop", null, null));
        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                for (var i = 0; i < 1000; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Interlocked.Increment(ref Produced) == 129) Filled.TrySetResult();
                    yield return new TextDelta("x");
                }
                yield return new FinishDelta("stop");
                await Task.CompletedTask;
            }
            finally { Disposed = true; }
        }
    }
}
