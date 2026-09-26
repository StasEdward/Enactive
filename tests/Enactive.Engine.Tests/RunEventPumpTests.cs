namespace Enactive.Engine.Tests;

using System.Runtime.CompilerServices;
using Enactive.App.Ui;
using Enactive.Core.Events;
using Xunit;

public sealed class RunEventPumpTests
{
    [Fact]
    public async Task Stop_with_a_full_channel_still_delivers_the_terminal_event_once()
    {
        using var stop = new CancellationTokenSource();
        var rendering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendered = new List<WorkEvent>();
        var firstBatch = 0;
        async IAsyncEnumerable<WorkEvent> Events([EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; !ct.IsCancellationRequested; i++)
            {
                if (i == RunEventPump.Capacity) await rendering.Task;
                if (rendering.Task.IsCompleted && i >= Volatile.Read(ref firstBatch) + RunEventPump.Capacity)
                    full.TrySetResult();
                yield return new(Guid.NewGuid(), Guid.Empty, Guid.Empty, DateTimeOffset.UtcNow,
                    EventKind.AssistantDelta, i.ToString(), null);
            }
            yield return new(Guid.NewGuid(), Guid.Empty, Guid.Empty, DateTimeOffset.UtcNow,
                EventKind.TaskCompleted, "cancelled", WorkEventPayload.OutcomePayload(RunOutcomeKind.Cancelled, "Stop"));
            await Task.CompletedTask;
        }
        var run = RunEventPump.RunAsync(Events(), async batch =>
        {
            Interlocked.CompareExchange(ref firstBatch, batch.Count, 0);
            rendering.TrySetResult();
            await release.Task;
            rendered.AddRange(batch);
        }, stop.Token);
        try
        {
            await full.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stop.CancelAsync();
        }
        finally { release.TrySetResult(); }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(rendered, e => e.Kind == EventKind.TaskCompleted);
        Assert.Equal(RunOutcomeKind.Cancelled, rendered.Last().Outcome());
    }

    [Fact]
    public async Task Slow_renderer_bounds_pending_work_and_preserves_order()
    {
        var produced = 0;
        var rendered = new List<string>();
        var batches = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<WorkEvent> Events([EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < 1000; i++)
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref produced);
                yield return new(Guid.NewGuid(), Guid.Empty, Guid.Empty, DateTimeOffset.UtcNow,
                    EventKind.AssistantDelta, i.ToString(), null);
            }
            await Task.CompletedTask;
        }
        var run = RunEventPump.RunAsync(Events(), async batch =>
        {
            entered.TrySetResult();
            await release.Task;
            batches++;
            rendered.AddRange(batch.Select(e => e.Summary));
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(Volatile.Read(ref produced), 1, RunEventPump.Capacity * 2 + 1);
        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(0, 1000).Select(i => i.ToString()), rendered);
        Assert.True(batches < 20);
    }

    [Fact]
    public async Task Renderer_failure_disposes_the_producer()
    {
        var disposed = false;
        async IAsyncEnumerable<WorkEvent> Events([EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new(Guid.NewGuid(), Guid.Empty, Guid.Empty, DateTimeOffset.UtcNow,
                        EventKind.AssistantDelta, "text", null);
                    await Task.Yield();
                }
            }
            finally { disposed = true; }
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunEventPump.RunAsync(Events(),
            _ => throw new InvalidOperationException("renderer"), CancellationToken.None));
        Assert.True(disposed);
    }
}
