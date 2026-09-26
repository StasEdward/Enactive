namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.History;
using Xunit;

public sealed class RunCheckpointWriterTests
{
    private static RunCheckpoint Snapshot(int n = 0) => new(Guid.NewGuid(), Guid.NewGuid(),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "request", "title", null, null, [], [], [], [], n, 0);

    [Fact]
    public async Task Forget_waits_for_save_and_late_saves_cannot_resurrect_the_checkpoint()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store { Save = async _ => { entered.SetResult(); await release.Task; } };
        using var writer = new RunCheckpointWriter(store, _ => Assert.Fail("unexpected failure"));
        var run = Guid.NewGuid();
        var save = writer.SaveAsync(() => Snapshot() with { RunId = run });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var forget = writer.ForgetAsync(run, null);
        Assert.False(forget.IsCompleted);
        Assert.Empty(store.Deleted);
        release.SetResult();
        await Task.WhenAll(save, forget).WaitAsync(TimeSpan.FromSeconds(5));
        await writer.SaveAsync(() => throw new InvalidOperationException("late capture must never run"));
        Assert.Equal(new[] { run }, store.Deleted);
    }

    [Fact]
    public async Task Capture_waits_for_the_previous_save_so_old_state_cannot_overwrite_new()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<int>();
        var store = new Store { Save = async c =>
        {
            if (saved.Count == 0) { entered.SetResult(); await release.Task; }
            saved.Add(c.StepsRun);
        }};
        using var writer = new RunCheckpointWriter(store, _ => Assert.Fail("unexpected failure"));
        var state = 1;
        var first = writer.SaveAsync(() => Snapshot(state));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var captured = false;
        var second = writer.SaveAsync(() => { captured = true; return Snapshot(state); });
        Assert.False(captured);
        state = 2;
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1, 2 }, saved);
    }

    [Fact]
    public async Task Capture_or_save_failure_reports_error_and_releases_gate()
    {
        var errors = new List<string>();
        var store = new Store { Save = _ => throw new JsonException("serialize") };
        using var writer = new RunCheckpointWriter(store, errors.Add);
        await writer.SaveAsync(() => throw new IOException("capture"));
        await writer.SaveAsync(() => Snapshot());
        store.Save = _ => Task.CompletedTask;
        await writer.SaveAsync(() => Snapshot());
        Assert.Equal(2, errors.Count);
        Assert.Contains("capture", errors[0]);
        Assert.Contains("serialize", errors[1]);
    }

    [Fact]
    public async Task Cancellation_propagates_and_releases_gate()
    {
        using var writer = new RunCheckpointWriter(new Store(), _ => Assert.Fail("cancel is not an error"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => writer.SaveAsync(() => throw new OperationCanceledException()));
        await writer.SaveAsync(() => Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forget_removes_both_run_ids_but_never_deletes_the_same_one_twice(bool same)
    {
        var store = new Store();
        using var writer = new RunCheckpointWriter(store, _ => { });
        var run = Guid.NewGuid();
        var prior = same ? run : Guid.NewGuid();
        await writer.ForgetAsync(run, prior);
        Assert.Equal(same ? new[] { run } : new[] { run, prior }, store.Deleted);
    }

    private sealed class Store : IRunCheckpointStore
    {
        public Func<RunCheckpoint, Task> Save = _ => Task.CompletedTask;
        public List<Guid> Deleted = new();
        public Task SaveAsync(RunCheckpoint c, CancellationToken ct)
        { Assert.False(ct.CanBeCanceled); return Save(c); }
        public Task DeleteAsync(Guid id, CancellationToken ct) { Deleted.Add(id); return Task.CompletedTask; }
        public Task<RunCheckpoint?> LoadAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
