namespace Enactive.Engine.Tests;

using Enactive.App.Ui;
using Xunit;

public sealed class UiRunOwnershipTests
{
    [Fact]
    public async Task Stop_returns_while_a_slow_throwing_callback_runs_outside_the_slot_lock()
    {
        var slot = new ForegroundRunSlot();
        using var owner = slot.TryStart();
        Assert.NotNull(owner);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = owner.Token.Register(() =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            throw new InvalidOperationException("callback failure");
        });
        var stopped = Task.Run(slot.Stop);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stopped.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(owner.IsCancellationRequested);
            Assert.True(await Task.Run(() => slot.Finish(owner)).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.Set(); }
        await slot.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Second_start_and_stale_cleanup_cannot_steal_stop()
    {
        var slot = new ForegroundRunSlot();
        using var first = slot.TryStart();
        Assert.NotNull(first);
        Assert.Null(slot.TryStart());
        slot.Stop();
        Assert.True(first.IsCancellationRequested);
        Assert.Null(slot.TryStart()); // Cancellation is not completion.
        Assert.True(slot.Finish(first));
        using var second = slot.TryStart();
        Assert.NotNull(second);
        Assert.False(slot.Finish(first));
        slot.Stop();
        Assert.True(second.IsCancellationRequested);
        Assert.True(slot.Finish(second));
    }

    [Fact]
    public async Task Cancelled_decision_cannot_remember_an_approval_from_a_late_click()
    {
        var completion = new DecisionCompletion();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        completion.Cancel(cancel.Token);
        var remembered = false;
        Assert.False(completion.Resolve("allow", () => remembered = true));
        Assert.False(remembered);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.Task);
    }

    [Fact]
    public async Task Remember_failure_finishes_the_decision_without_allowing_the_action()
    {
        var completion = new DecisionCompletion();
        Assert.False(completion.Resolve("allow", () => throw new IOException("cannot save approval")));
        await Assert.ThrowsAsync<IOException>(() => completion.Task);
        Assert.False(completion.Resolve("allow"));
    }

    [Fact]
    public async Task An_answer_wins_once_and_later_cancellation_does_not_change_it()
    {
        var completion = new DecisionCompletion();
        var remembers = 0;
        Assert.True(completion.Resolve("allow", () => remembers++));
        completion.Cancel(new CancellationToken(true));
        Assert.False(completion.Resolve("deny", () => remembers++));
        Assert.Equal("allow", (await completion.Task).OptionId);
        Assert.Equal(1, remembers);
    }
}
