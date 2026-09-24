namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Core.Permissions;
using Enactive.Core.Schedules;
using Enactive.Core.Storage;
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
    ///
    /// <para>And the waiting writer is let go and WAITED FOR before the test ends. It used to be left
    /// running when the test returned, racing the fixture deleting its folder, and whatever it did
    /// after the handle closed - finish, throw, hang - went unseen
    /// (Docs/CORE_TESTS_REVIEW_2026-09-24.md #5).</para>
    /// </summary>
    [Fact]
    public async Task A_write_waits_while_another_process_holds_the_file()
    {
        await _inbox.AppendAsync(Item("first"), CancellationToken.None);

        var foreignHold = HoldAsAnotherProcessWould();
        Task? blocked = null;
        try
        {
            blocked = _inbox.AppendAsync(Item("second"), CancellationToken.None);
            var finishedEarly = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromMilliseconds(500)));

            Assert.NotSame(blocked, finishedEarly);
        }
        finally
        {
            foreignHold.Dispose();
            if (blocked is not null)
                await blocked.WaitAsync(TimeSpan.FromSeconds(20));
        }

        Assert.Equal(2, (await _inbox.LoadAllAsync(CancellationToken.None)).Count);
    }

    /// <summary>
    /// And then it goes through, KEEPING what the other process wrote while it waited. Waiting is
    /// only half the requirement: a writer that read the list before waiting and writes it back after
    /// has lost the other process's entry anyway.
    ///
    /// <para>This used to leave the file as it was during the wait, so a writer that read BEFORE
    /// taking the lock got exactly the list it needed and passed (Docs/CORE_TESTS_REVIEW_2026-09-24.md
    /// #2). Now the other process writes an entry while it holds the lock, as a real one does.</para>
    /// </summary>
    [Fact]
    public async Task The_write_keeps_what_the_other_process_wrote_while_it_waited()
    {
        var first = Item("first");
        await _inbox.AppendAsync(first, CancellationToken.None);

        // What the other process will have written by the time it lets go: "first" and "third".
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var theirs = new JsonInboxStore(WorkspaceInfo.For(elsewhere));
        await theirs.AppendAsync(first, CancellationToken.None);
        await theirs.AppendAsync(Item("third"), CancellationToken.None);

        var foreignHold = HoldAsAnotherProcessWould();
        Task? blocked = null;
        try
        {
            blocked = _inbox.AppendAsync(Item("second"), CancellationToken.None);

            // Time for a writer that reads BEFORE it waits to have read. A correct one reads only
            // after the lock is its own, so what it ends with does not depend on this delay at all.
            await Task.Delay(200);
            Assert.False(blocked.IsCompleted);

            // The other process's write, made while it holds the lock.
            File.Copy(Path.Combine(elsewhere, ".enactive", "inbox.json"),
                      Path.Combine(_root, ".enactive", "inbox.json"), overwrite: true);
        }
        finally
        {
            foreignHold.Dispose();
            if (blocked is not null)
                await blocked.WaitAsync(TimeSpan.FromSeconds(20));
        }

        var items = await _inbox.LoadAllAsync(CancellationToken.None);
        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.Title == "first");
        Assert.Contains(items, i => i.Title == "second");
        Assert.Contains(items, i => i.Title == "third");
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

    // ── the schedules file, which is the worse case ─────────────────────────
    //
    // Worse for two reasons. It is written from BOTH sides by design — the runner stamps LastFiredAt
    // every time a schedule fires, and the window adds, edits and disables — and what is lost is not
    // a notification. A lost schedule is work that never runs; a lost LastFiredAt is an occurrence
    // that fires a second time. Measured before the lock, two processes saving 200 schedules each:
    // 137 of 400 survived, losses on both sides, no error anywhere.

    private string SchedulesFile => Path.Combine(_root, "schedules.json");

    private Schedule Schedule(string name)
        => new(Guid.NewGuid(), _root, name,
               ScheduledWork.FromTemplate("tidy"),
               ScheduleTiming.Daily(new TimeOnly(3, 0), "UTC"),
               PermissionPolicy.PermissiveDefault, DateTimeOffset.Now);

    [Fact]
    public async Task A_schedule_save_waits_while_another_process_holds_the_file()
    {
        var store = new ScheduleStore(SchedulesFile);
        store.Save(Schedule("first"));

        var foreignHold = new FileStream(
            FileLock.LockPathFor(SchedulesFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task? blocked = null;
        try
        {
            // Task.Run because this store is synchronous: the wait is a blocked thread, which is what
            // the app and the runner both do here.
            blocked = Task.Run(() => store.Save(Schedule("second")));
            var finishedEarly = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromMilliseconds(500)));

            Assert.NotSame(blocked, finishedEarly);
        }
        finally
        {
            // Save takes no token, so the only way to end the wait is to let go - and then the
            // blocked thread is joined before the fixture deletes the folder it is writing to.
            foreignHold.Dispose();
            if (blocked is not null)
                await blocked.WaitAsync(TimeSpan.FromSeconds(20));
        }

        Assert.Equal(2, store.For(_root).Count);
    }

    /// <summary>
    /// The schedule another process added - or the LastFiredAt it stamped - while this save waited is
    /// still there afterwards. See the inbox test above: the other process now WRITES while it holds
    /// the lock, so a save that read the file before taking the lock loses that write.
    /// </summary>
    [Fact]
    public async Task A_schedule_the_other_process_saved_while_this_one_waited_is_kept()
    {
        var store = new ScheduleStore(SchedulesFile);
        store.Save(Schedule("first"));

        // What the other process will have written by the time it lets go: "first" and "third".
        var elsewhere = Path.Combine(_root, "elsewhere.json");
        File.Copy(SchedulesFile, elsewhere);
        new ScheduleStore(elsewhere).Save(Schedule("third"));

        var foreignHold = new FileStream(
            FileLock.LockPathFor(SchedulesFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task? blocked = null;
        try
        {
            blocked = Task.Run(() => store.Save(Schedule("second")));

            // Time for a save that reads BEFORE it waits to have read; a correct one does not care.
            await Task.Delay(200);
            Assert.False(blocked.IsCompleted);

            File.Copy(elsewhere, SchedulesFile, overwrite: true);
        }
        finally
        {
            foreignHold.Dispose();
            if (blocked is not null)
                await blocked.WaitAsync(TimeSpan.FromSeconds(20));
        }

        var saved = store.For(_root);
        Assert.Equal(3, saved.Count);
        Assert.Contains(saved, s => s.Name == "first");
        Assert.Contains(saved, s => s.Name == "second");
        Assert.Contains(saved, s => s.Name == "third");
    }
}
