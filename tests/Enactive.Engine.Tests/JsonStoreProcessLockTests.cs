namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The file stores against a SECOND PROCESS.
///
/// <para>Until the scheduler there was only one: the desktop, with several tasks inside it, and the
/// stores said so in as many words — "cross-process safety is out of scope". The runner a schedule
/// wakes is that second process, writing the same files as the open window.</para>
///
/// <para>What it cost, measured on 2026-09-10 before the fix: two processes appending 200 inbox
/// items each left 200 items in the file. Both reported success; neither saw an error. A scheduled
/// run's only durable report is an Inbox item, so this was the result going missing.</para>
///
/// <para>These tests do not start a process. They hold the LOCK FILE the way another process holds
/// it — same handle, same exclusion — which is the part a second process actually contributes. The
/// end-to-end measurement with two real processes is in <c>Docs/SCHEDULER_PLAN.md</c>.</para>
/// </summary>
public sealed class JsonStoreProcessLockTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "enactive-lock", Guid.NewGuid().ToString("N"));

    private readonly WorkspaceInfo _workspace;
    private readonly JsonInboxStore _inbox;
    private readonly string _lockPath;

    public JsonStoreProcessLockTests()
    {
        Directory.CreateDirectory(_root);
        _workspace = WorkspaceInfo.For(_root);
        _inbox = new JsonInboxStore(_workspace);
        _lockPath = Path.Combine(_root, ".enactive", "inbox.json.lock");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private InboxItem Item(string title)
        => new(Guid.NewGuid(), _workspace.Id, "result", title, "probe", Guid.NewGuid(),
               "unread", DateTimeOffset.UtcNow);

    /// <summary>How another process holds the file: one handle, shared with nobody.</summary>
    private FileStream HoldAsAnotherProcessWould()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    /// The write WAITS. Before the lock it went straight through, read the list somebody else was
    /// half-way through replacing, and wrote its own version over the result.
    /// </summary>
    [Fact]
    public async Task A_write_waits_while_another_process_holds_the_file()
    {
        await _inbox.AppendAsync(Item("first"), CancellationToken.None);

        using var foreignHold = HoldAsAnotherProcessWould();

        var blocked = _inbox.AppendAsync(Item("second"), CancellationToken.None);
        var finishedEarly = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromMilliseconds(500)));

        Assert.NotSame(blocked, finishedEarly);
    }

    /// <summary>
    /// And then it goes through, with BOTH entries. Waiting is only half the requirement: a writer
    /// that waits and then writes the list it read before waiting has lost the other one anyway.
    /// </summary>
    [Fact]
    public async Task The_write_completes_with_both_entries_once_the_other_process_lets_go()
    {
        await _inbox.AppendAsync(Item("first"), CancellationToken.None);

        var foreignHold = HoldAsAnotherProcessWould();
        var blocked = _inbox.AppendAsync(Item("second"), CancellationToken.None);

        await Task.Delay(200);
        foreignHold.Dispose();

        await blocked.WaitAsync(TimeSpan.FromSeconds(20));

        var items = await _inbox.LoadAllAsync(CancellationToken.None);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Title == "first");
        Assert.Contains(items, i => i.Title == "second");
    }

    /// <summary>
    /// Nothing is left lying about. A lock file that survives its holder is a store that will not
    /// open next time, which is a worse failure than the one being prevented — so the handle deletes
    /// itself on close.
    /// </summary>
    [Fact]
    public async Task The_lock_file_does_not_outlive_the_write()
    {
        await _inbox.AppendAsync(Item("first"), CancellationToken.None);

        Assert.False(File.Exists(_lockPath));
    }
}
