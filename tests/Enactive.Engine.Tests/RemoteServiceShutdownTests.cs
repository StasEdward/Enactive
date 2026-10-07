namespace Enactive.Engine.Tests;

using System.Reflection;
using Enactive.App.Ui;
using Enactive.Remote.Contracts;
using Enactive.Remote.Host;
using Enactive.Settings;
using Enactive.Workspace;

public sealed class RemoteServiceShutdownTests
{
    [Fact]
    public async Task Service_waits_for_terminal_persistence_before_disposing_host_store()
    {
        using var fx = new EngineFixture();
        var database = fx.PathOf("remote.db");
        var store = new HostStore(database);
        store.Accept(new HostCommand("command", "host", CommandKind.StartTask, "{}",
            CommandStatus.PendingDelivery, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)));
        Assert.True(store.BeginRun("command", "run"));
        await using var service = new RemoteAccessService(new RemoteAccessSettings(), new FixedHostKeys(),
            () => throw new InvalidOperationException("No network or composition expected"), () => [], fx.Decisions, database);
        typeof(RemoteAccessService).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, store);
        var runs = (BackgroundRunGroup)typeof(RemoteAccessService)
            .GetField("_runs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = runs.TryStart(async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally
            {
                cleaning.SetResult();
                await release.Task;
                store.Enqueue("run", RemoteEventKind.Cancelled, _ => "Stopped");
            }
        })!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = service.DisposeAsync().AsTask();
        try
        {
            await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(shutdown.IsCompleted);
            Assert.Null(runs.TryStart(_ => Task.CompletedTask));
        }
        finally { release.TrySetResult(); }
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        await run;
        using var reopened = new HostStore(database);
        Assert.Equal(RemoteEventKind.Cancelled, Assert.Single(reopened.NextOwed()).Event.Kind);
    }

    [Fact]
    public async Task Workspace_snapshots_stay_stable_during_parallel_updates()
    {
        using var fx = new EngineFixture();
        var path = fx.PathOf("registry.json");
        var registry = WorkspaceRegistry.Load(deferWrites: true, filePath: path);
        registry.Touch(fx.Root);
        var snapshot = registry.Entries;
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
        {
            var root = fx.PathOf("workspace-" + i);
            registry.Touch(root);
            registry.SaveSettings(root, i % 4, "worker-" + i, false);
            registry.Rename(root, "name-" + i);
            Assert.Single(registry.Entries, e => e.RootPath == root);
        })));
        await registry.FlushAsync();
        Assert.Single(snapshot);
        Assert.Equal(25, registry.Entries.Count);
        var saved = WorkspaceRegistry.Load(filePath: path);
        Assert.Equal(25, saved.Entries.Count);
        Assert.All(saved.Entries.Where(e => e.RootPath != fx.Root), e => Assert.StartsWith("name-", e.Name));
    }
}
