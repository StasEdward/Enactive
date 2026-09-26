namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Xunit;

public sealed class DecisionIsolationTests
{
    private static DecisionRequest Ask(string root) => new(Guid.NewGuid(), "Approve", "detail",
        [new("allow", "Allow"), new("deny", "Deny")], "allow", Subject: "write_file",
        Action: new(Guid.NewGuid(), Guid.NewGuid().ToString(), "write_file", "{}", root));

    [Fact]
    public void Remembered_approval_cannot_cross_workspaces_or_authorize_unbound_requests()
    {
        using var fx = new EngineFixture();
        var approvals = new SessionApprovals();
        var first = Ask(fx.PathOf("one"));
        approvals.Remember(first);
        Assert.True(approvals.Approves(Ask(fx.PathOf("one/./"))));
        Assert.False(approvals.Approves(Ask(fx.PathOf("two"))));
        Assert.False(approvals.Approves(first with { Action = null }));
        Assert.False(approvals.Approves(first with { RequiresExplicitAnswer = true }));
        var remote = Ask(fx.PathOf("remote")) with { RequiresExplicitAnswer = true };
        approvals.Remember(remote);
        Assert.False(approvals.Approves(remote with { RequiresExplicitAnswer = false }));
    }

    [Fact]
    public async Task Cancelling_a_queued_request_does_not_replace_or_cancel_the_visible_one()
    {
        using var fx = new EngineFixture();
        var queue = new DecisionQueue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<DecisionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.RequestAsync(Ask(fx.Root), default, (_, _) => { entered.SetResult(); return answer.Task; });
        await entered.Task;
        using var cancel = new CancellationTokenSource();
        var shown = false;
        var second = queue.RequestAsync(Ask(fx.Root), cancel.Token, (_, _) =>
        { shown = true; return Task.FromResult(new DecisionOutcome("deny")); });
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(shown);
        Assert.False(first.IsCompleted);
        answer.SetResult(new DecisionOutcome("allow"));
        Assert.Equal("allow", (await first).OptionId);
        Assert.Equal("deny", (await queue.RequestAsync(Ask(fx.Root), default,
            (_, _) => Task.FromResult(new DecisionOutcome("deny")))).OptionId);
    }

    [Fact]
    public async Task Next_question_waits_until_cancelled_card_cleanup_finishes()
    {
        using var fx = new EngineFixture();
        using var cancel = new CancellationTokenSource();
        var queue = new DecisionQueue();
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.RequestAsync(Ask(fx.Root), cancel.Token, async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); return new DecisionOutcome("allow"); }
            finally { cleaning.SetResult(); await cleaned.Task; }
        });
        cancel.Cancel();
        await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shown = false;
        var second = queue.RequestAsync(Ask(fx.Root), default, (_, _) =>
        { shown = true; return Task.FromResult(new DecisionOutcome("deny")); });
        Assert.False(shown);
        cleaned.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("deny", (await second.WaitAsync(TimeSpan.FromSeconds(5))).OptionId);
    }

    [Fact]
    public async Task Cancelled_run_cannot_receive_allow_from_a_late_handler()
    {
        using var fx = new EngineFixture();
        using var cancellation = new CancellationTokenSource();
        var queue = new DecisionQueue();
        var answer = new TaskCompletionSource<DecisionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deciding = queue.RequestAsync(Ask(fx.Root), cancellation.Token, (_, _) => answer.Task);
        cancellation.Cancel();
        answer.SetResult(new("allow"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deciding);
        Assert.Equal("deny", (await queue.RequestAsync(Ask(fx.Root), default,
            (_, _) => Task.FromResult(new DecisionOutcome("deny")))).OptionId);
    }

    [Fact]
    public async Task Failed_visible_request_releases_the_queue()
    {
        using var fx = new EngineFixture();
        var queue = new DecisionQueue();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RequestAsync(Ask(fx.Root), default,
            (_, _) => throw new InvalidOperationException()));
        Assert.Equal("allow", (await queue.RequestAsync(Ask(fx.Root), default,
            (_, _) => Task.FromResult(new DecisionOutcome("allow")))).OptionId);
    }
}
