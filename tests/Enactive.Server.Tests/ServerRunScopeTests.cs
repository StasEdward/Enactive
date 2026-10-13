namespace Enactive.Server.Tests;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;
using Enactive.Core.Events;
using Enactive.Core.Providers;
using Enactive.Providers;
using Enactive.Server.Runs;

/// <summary>
/// A model call's figures reach the run that made it. The engine is one for the whole service and is told of every call
/// through one callback, while several runs go at once: the callback finds its run from the flow it is called in.
/// </summary>
public sealed class ServerRunScopeTests
{
    /// <summary>Answers once both calls are in flight, so the two runs' calls overlap.</summary>
    private sealed class Overlapping(Barrier bothIn) : IChatProvider
    {
        public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
        {
            // Asserted: unchecked, a barrier that timed out let the calls through one after the other, and the test passed
            // without the overlap it is about.
            Assert.True(await Task.Run(() => bothIn.SignalAndWait(TimeSpan.FromSeconds(10)), ct), "The two calls were never in flight together.");
            return new ChatCompletion(ChatMessage.Assistant("done"), "stop", 100, 20);
        }

        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>
    /// Two runs at once, each calling its own model through the metered provider the engine is built with. Both runs
    /// carry the same run id here, so only the flow tells their calls apart - the test is of the flow, not of the
    /// performance figures' own filter by run.
    /// </summary>
    [Fact]
    public async Task Model_figures_reach_the_run_whose_flow_reported_them()
    {
        var run = Guid.NewGuid();
        var first = await Started(run);
        var second = await Started(run);
        using var bothIn = new Barrier(2);
        var provider = new MeteredChatProvider(new Overlapping(bothIn), "ollama", ServerRunScope.Report);

        Task Call(RunSession session, string model) => Task.Run(async () =>
        {
            ServerRunScope.Current = session;
            using var _ = LogScope.Begin(run, Guid.NewGuid());
            await provider.CompleteAsync(new ChatRequest(model, [ChatMessage.User("go")]), CancellationToken.None);
        });
        await Task.WhenAll(Call(first, "model-a"), Call(second, "model-b")).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(["model-a"], Models(first));
        Assert.Equal(["model-b"], Models(second));
        Assert.Null(ServerRunScope.Current);
    }

    private static async Task<RunSession> Started(Guid run)
    {
        var session = new RunSession("C:\\work", "alice", "t");
        var events = Channel.CreateUnbounded<WorkEvent>();
        events.Writer.TryWrite(new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), run, DateTimeOffset.UtcNow, EventKind.IntentReceived, "started", null));
        events.Writer.Complete();
        await session.DrainAsync(events.Reader.ReadAllAsync(), CancellationToken.None);
        return session;
    }

    private static List<string> Models(RunSession session)
        => session.Read((_, _, panel, _) => panel.Performance.Rows.Select(r => r.Model).ToList());
}
