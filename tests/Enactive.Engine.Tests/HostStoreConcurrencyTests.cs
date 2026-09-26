namespace Enactive.Engine.Tests;

using Enactive.Remote.Contracts;
using Enactive.Remote.Host;
using Xunit;

public sealed class HostStoreConcurrencyTests
{
    [Fact]
    public async Task Concurrent_producers_and_delivery_keep_unique_contiguous_sequences()
    {
        using var fx = new EngineFixture();
        using var store = new HostStore(fx.PathOf("host.db"));
        store.Accept(new HostCommand("command", "host", CommandKind.StartTask, "{}",
            CommandStatus.PendingDelivery, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)));
        Assert.True(store.BeginRun("command", "run"));
        var sequences = new System.Collections.Concurrent.ConcurrentBag<long>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                sequences.Add(store.Enqueue("run", RemoteEventKind.Progress).Sequence);
                foreach (var item in store.NextOwed()) store.RecordAttempt(item.EventId, "retry");
                Assert.Contains("run", store.RunsLeftInFlight());
            }
        })));
        Assert.Equal(Enumerable.Range(1, 200).Select(i => (long)i), sequences.Order());
        var delivered = new List<long>();
        while (store.NextOwed().FirstOrDefault() is { } item)
        {
            delivered.Add(item.Sequence);
            store.Discard(item.EventId);
        }
        Assert.Equal(Enumerable.Range(1, 200).Select(i => (long)i), delivered);
    }
}
