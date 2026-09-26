namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Xunit;

public sealed class StepDispatcherTests
{
    [Fact]
    public async Task Budget_abandons_pending_steps_without_cancelling_the_running_step()
    {
        var scheduler = new DagScheduler(LinearPlan.FromTitles(["first", "second"]));
        var budget = new RunBudget(new(MaxSteps: 1), DateTimeOffset.UtcNow);
        using var lifetime = new CancellationTokenSource();
        var ran = new List<string>();
        var abandoned = new List<string>();
        var completed = false;
        var reason = await StepDispatcher.RunAsync(scheduler, budget, 2, lifetime,
            (step, ct) =>
            {
                Assert.False(ct.IsCancellationRequested);
                ran.Add(step.Title);
                scheduler.MarkDone(step.Id);
                return Task.CompletedTask;
            }, _ =>
            {
                abandoned.AddRange(scheduler.AbandonPending().Select(s => s.Title));
                return Task.CompletedTask;
            }, () => completed = true);
        Assert.NotNull(reason);
        Assert.Equal(new[] { "first" }, ran);
        Assert.Equal(new[] { "second" }, abandoned);
        Assert.Equal(1, budget.StepsRun);
        Assert.True(completed);
    }

    [Fact]
    public async Task Dispatcher_drains_cancelled_siblings_before_signalling_completion()
    {
        var plan = DagPlan.FromSpecs([new("fail", [], DependenciesDeclared: true), new("sibling", [], DependenciesDeclared: true)]);
        var scheduler = new DagScheduler(plan);
        using var lifetime = new CancellationTokenSource();
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingFinished = false;
        var completed = false;
        await Assert.ThrowsAsync<IOException>(() => StepDispatcher.RunAsync(scheduler, RunBudget.Unlimited(), 2, lifetime,
            async (step, ct) =>
            {
                if (step.Title == "fail")
                {
                    await siblingStarted.Task;
                    throw new IOException("original failure");
                }
                siblingStarted.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { siblingFinished = true; }
            }, _ => throw new InvalidOperationException("unexpected budget limit"),
            () => { Assert.True(siblingFinished); completed = true; }).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(completed);
    }

    [Fact]
    public async Task Dependency_waits_for_completion_while_independent_steps_fill_capacity()
    {
        var plan = DagPlan.FromSpecs([new("A", [], DependenciesDeclared: true), new("B", [], DependenciesDeclared: true), new("C", [0, 1], DependenciesDeclared: true)]);
        var scheduler = new DagScheduler(plan);
        using var lifetime = new CancellationTokenSource();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var finished = 0;
        await StepDispatcher.RunAsync(scheduler, RunBudget.Unlimited(), 2, lifetime,
            async (step, _) =>
            {
                if (step.Title == "C") Assert.Equal(2, finished);
                else
                {
                    if (Interlocked.Increment(ref started) == 2) bothStarted.SetResult();
                    await bothStarted.Task;
                    Interlocked.Increment(ref finished);
                }
                scheduler.MarkDone(step.Id);
            }, _ => throw new InvalidOperationException("unexpected budget limit"), () => { }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, scheduler.DoneCount);
    }
}
