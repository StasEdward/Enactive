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

    /// <summary>
    /// A SECOND corruption, after the first was already quarantined, gets its OWN backup rather than
    /// being silently discarded.
    ///
    /// <para>Confirmed 2026-09-24:
    /// <c>QuarantineUnreadable</c> used to stop at the first backup - "one copy is enough" - and the
    /// caller wrote a fresh file over the second corruption regardless of whether it had actually been
    /// preserved. Content A: write corrupt, append (quarantines A, writes a fresh store). Content B:
    /// corrupt the fresh file AGAIN, append once more - B must get its own backup, distinct from A's,
    /// and the append must still land.</para>
    /// </summary>
    [Fact]
    public async Task A_second_corruption_gets_its_own_backup_not_lost_under_the_first()
    {
        using var fx = new EngineFixture();
        var path = Path.Combine(fx.Root, WorkspaceGuard.ReservedFolder, "inbox.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ CORRUPTION-A, not the array we expect");

        var store = new JsonInboxStore(fx.Workspace);
        await store.AppendAsync(
            new InboxItem(Guid.NewGuid(), fx.Workspace.Id, "result", "after A", "s",
                          Guid.NewGuid(), "unread", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.True(File.Exists(path + ".unreadable"));
        Assert.Contains("CORRUPTION-A", await File.ReadAllTextAsync(path + ".unreadable"), StringComparison.Ordinal);

        // Corrupt the now-healthy file a second time.
        await File.WriteAllTextAsync(path, "{ CORRUPTION-B, a different break entirely");

        await store.AppendAsync(
            new InboxItem(Guid.NewGuid(), fx.Workspace.Id, "result", "after B", "s",
                          Guid.NewGuid(), "unread", DateTimeOffset.UtcNow),
            CancellationToken.None);

        // A's backup is untouched, B got its OWN, and the append after B is not lost either.
        Assert.Contains("CORRUPTION-A", await File.ReadAllTextAsync(path + ".unreadable"), StringComparison.Ordinal);
        Assert.True(File.Exists(path + ".unreadable.2"), "the second corruption needs its own backup");
        Assert.Contains("CORRUPTION-B", await File.ReadAllTextAsync(path + ".unreadable.2"), StringComparison.Ordinal);

        var all = await store.LoadAllAsync(CancellationToken.None);
        Assert.Single(all);
        Assert.Equal("after B", all[0].Title);
    }

    /// <summary>
    /// THE BOUNDARY. When the damaged content genuinely could not be moved anywhere, the save must
    /// not run either - overwriting it would be exactly the loss this exists to prevent, just from a
    /// different cause.
    /// </summary>
    [Fact]
    public async Task When_the_damage_cannot_be_preserved_the_save_does_not_run_either()
    {
        using var fx = new EngineFixture();
        var path = Path.Combine(fx.Root, WorkspaceGuard.ReservedFolder, "inbox.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ CORRUPTION, not the array we expect");

        // Block the ONE backup name a move onto a DIRECTORY path fails, and File.Exists on a
        // directory reports false - so the guard against overwriting an existing backup does not
        // see it, and the move itself is what fails.
        Directory.CreateDirectory(path + ".unreadable");

        var store = new JsonInboxStore(fx.Workspace);
        await store.AppendAsync(
            new InboxItem(Guid.NewGuid(), fx.Workspace.Id, "result", "must not land", "s",
                          Guid.NewGuid(), "unread", DateTimeOffset.UtcNow),
            CancellationToken.None);

        // The damaged file is exactly as it was - not overwritten with a fresh list that lost it.
        Assert.Equal("{ CORRUPTION, not the array we expect", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_missing_file_is_simply_an_empty_store()
    {
        using var fx = new EngineFixture();
        var store = new JsonMemoryStore(fx.Workspace);

        Assert.Empty(await store.LoadAllAsync(CancellationToken.None));
    }
}
