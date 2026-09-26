namespace Enactive.Engine.Tests;

using System.Runtime.CompilerServices;
using Enactive.App.Ui;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Workspace;
using Xunit;

public sealed class BackgroundShutdownTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task One_failed_run_does_not_skip_the_other_runs_cleanup()
    {
        var group = new BackgroundRunGroup();
        var fail = Signal();
        var release = Signal();
        var failed = group.TryStart(async _ => { await fail.Task; throw new IOException("save failed"); });
        var cleaning = group.TryStart(_ => release.Task);
        var stop = group.StopAsync();
        fail.SetResult();
        await Assert.ThrowsAsync<IOException>(() => failed!);
        Assert.False(stop.IsCompleted);
        release.SetResult();
        await cleaning!;
        await Assert.ThrowsAsync<IOException>(() => stop);
        Assert.Null(group.TryStart(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task Shutdown_cancels_all_runs_and_waits_for_their_cleanup()
    {
        var group = new BackgroundRunGroup();
        var entered = new[] { Signal(), Signal() };
        var cleaning = new[] { Signal(), Signal() };
        var release = new[] { Signal(), Signal() };
        var runs = Enumerable.Range(0, 2).Select(i => group.TryStart(async ct =>
        {
            entered[i].SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cleaning[i].SetResult(); await release[i].Task; }
        })!).ToArray();
        await Task.WhenAll(entered.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        var stop = group.StopAsync();
        Assert.Same(stop, group.StopAsync());
        Assert.Null(group.TryStart(_ => Task.CompletedTask));
        await Task.WhenAll(cleaning.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stop.IsCompleted);
        release[0].SetResult();
        await runs[0];
        Assert.False(stop.IsCompleted);
        release[1].SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(runs);
    }

    [Fact]
    public async Task Shutdown_before_composition_still_runs_cleanup()
    {
        var group = new BackgroundRunGroup();
        var release = Signal();
        var cleaned = false;
        var run = group.TryStart(async ct =>
        {
            try { await release.Task; ct.ThrowIfCancellationRequested(); }
            finally { cleaned = true; }
        });
        var stop = group.StopAsync();
        release.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await run!;
        Assert.True(cleaned);
    }

    [Fact]
    public async Task Foreground_shutdown_waits_for_owner_and_blocks_new_runs()
    {
        var foreground = new ForegroundRunSlot();
        using var owner = foreground.TryStart();
        Assert.NotNull(owner);
        var stop = foreground.StopAsync();
        Assert.True(owner.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        Assert.Null(foreground.TryStart());
        Assert.True(foreground.Finish(owner));
        await stop;
        Assert.Null(foreground.TryStart());
    }

    [Fact]
    public async Task Cancelled_background_run_is_recorded_and_inbox_saved_before_shutdown_returns()
    {
        using var fx = new EngineFixture();
        var store = new JsonRunStore(fx.Workspace);
        var inbox = new JsonInboxStore(fx.Workspace);
        var recorder = new RunRecorder(store, workspaceId: fx.Workspace.Id);
        var entered = Signal();
        var group = new BackgroundRunGroup();
        var runId = Guid.NewGuid();
        async IAsyncEnumerable<WorkEvent> Events([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), runId, DateTimeOffset.UtcNow,
                EventKind.IntentReceived, "background test", null);
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        var run = group.TryStart(ct => BackgroundRunner.RunAsync(
            recorder.RecordAsync(Events(ct), ct), inbox, fx.Workspace, "background test", ct));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await group.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await run!;
        var record = await store.LoadAsync(runId, default);
        Assert.NotNull(record);
        Assert.Equal("Cancelled", record.Status);
        var item = Assert.Single(await inbox.LoadAllAsync(default));
        Assert.Equal(runId, item.RunId);
        Assert.Contains("cancel", item.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
