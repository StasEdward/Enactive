namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Core.Memory;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The JSON stores lose records when more than one instance writes (review finding #16). The
/// factories hand a fresh store to each background task, to the UI and to each run, all addressing
/// the same file, while the lock guarded one object.
/// </summary>
public sealed class JsonStoreConcurrencyTests
{
    [Fact]
    public async Task Concurrent_appends_from_separate_instances_keep_every_record()
    {
        using var fx = new EngineFixture();
        var workspace = fx.Workspace;

        // Separate instances, exactly as the factories produce. Enough of them that an unsynchronised
        // read-modify-write loses records every time rather than occasionally — a race this test can
        // only catch when it is overwhelming.
        var writers = Enumerable.Range(0, 32)
            .Select(_ => new JsonInboxStore(workspace))
            .ToArray();

        await Task.WhenAll(writers.Select((store, i) => store.AppendAsync(
            new InboxItem(Guid.NewGuid(), workspace.Id, "result", $"item {i}", "summary",
                          Guid.NewGuid(), "unread", DateTimeOffset.UtcNow),
            CancellationToken.None)));

        var all = await new JsonInboxStore(workspace).LoadAllAsync(CancellationToken.None);
        Assert.Equal(writers.Length, all.Count);
        Assert.Equal(writers.Length, all.Select(i => i.Title).Distinct().Count());
    }

    [Fact]
    public async Task Concurrent_memory_appends_keep_every_entry()
    {
        using var fx = new EngineFixture();
        var workspace = fx.Workspace;

        var writers = Enumerable.Range(0, 8)
            .Select(_ => new JsonMemoryStore(workspace))
            .ToArray();

        await Task.WhenAll(writers.Select((store, i) => store.AppendAsync(
            new MemoryEntry(Guid.NewGuid(), workspace.Id, "decision", $"entry {i}", null, DateTimeOffset.UtcNow),
            CancellationToken.None)));

        var all = await new JsonMemoryStore(workspace).LoadAllAsync(CancellationToken.None);
        Assert.Equal(writers.Length, all.Count);
    }

    // A read error used to come back as an empty list, and the very next save wrote that empty list
    // over the file — a store that could not be read became a store that had been emptied.
    [Fact]
    public async Task An_unreadable_file_is_kept_rather_than_overwritten()
    {
        using var fx = new EngineFixture();
        var path = Path.Combine(fx.Root, WorkspaceGuard.ReservedFolder, "inbox.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ this is not the array we expect");

        var store = new JsonInboxStore(fx.Workspace);
        await store.AppendAsync(
            new InboxItem(Guid.NewGuid(), fx.Workspace.Id, "result", "after the damage", "s",
                          Guid.NewGuid(), "unread", DateTimeOffset.UtcNow),
            CancellationToken.None);

        // The new item is stored...
        var all = await store.LoadAllAsync(CancellationToken.None);
        Assert.Single(all);

        // ...and the damaged content is still there to look at, not silently gone.
        Assert.True(File.Exists(path + ".unreadable"));
        Assert.Contains("not the array", await File.ReadAllTextAsync(path + ".unreadable"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_file_is_simply_an_empty_store()
    {
        using var fx = new EngineFixture();
        var store = new JsonMemoryStore(fx.Workspace);

        Assert.Empty(await store.LoadAllAsync(CancellationToken.None));
    }
}
