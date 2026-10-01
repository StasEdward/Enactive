namespace Enactive.Core.Storage;

using System.Collections.Concurrent;

/// <summary>
/// One file's critical section, held against the other tasks in this process AND against the other
/// processes on this machine.
///
/// <para><b>Why this exists.</b> Every state file here is read-modify-write: read the list, change
/// one entry, write the list back. A <c>lock</c> statement or a <c>SemaphoreSlim</c> makes that safe
/// inside one process and does nothing at all across two, and the second writer's whole change is
/// simply gone - with no error, because each writer did exactly what it was asked.</para>
///
/// <para>That was a stated limitation while there was one process. The scheduler ended it: the
/// runner a schedule wakes is a second process writing the same files as the open window. Measured
/// on 2026-09-10, two processes saving 200 schedules each to one file: 137 of the 400 survived.</para>
///
/// <para>A lock FILE rather than a named <c>Mutex</c> because some of these paths are async: a Mutex
/// belongs to the thread that took it, and an await can resume on another thread, where releasing it
/// throws. A file handle belongs to the process.</para>
///
/// <para>This is correct for several processes on ONE MACHINE. A shared store, or writers that are
/// not this machine, is what SQLite and MySQL are for.</para>
/// </summary>
public static class FileLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long to wait for another process. The critical section is a few milliseconds, so this is
    /// not a queue length - it is how long we are willing to wait for a holder that died without
    /// cleaning up. Windows closes a dead process's handles, so that wait ends by itself.
    /// </summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private const int PollMs = 15;

    /// <summary>Takes the section, blocking. For the synchronous stores.</summary>
    /// <exception cref="TimeoutException">Another process held it for <see cref="Wait"/>.</exception>
    public static Hold Take(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var gate = GateFor(full);
        gate.Wait(ct);

        try
        {
            var deadline = DateTimeOffset.UtcNow + Wait;
            while (true)
            {
                if (TryOpen(full, out var handle, out var giveUp))
                    return new Hold(gate, handle);
                if (giveUp)
                    return new Hold(gate, handle: null);
                if (DateTimeOffset.UtcNow >= deadline)
                    throw Timeout(full);

                // A wait the token can end - the same contract TakeAsync keeps with Task.Delay. This
                // was Thread.Sleep, so a cancelled Take went on waiting up to the whole 30 seconds
                // and then RETURNED the lock it had been told to stop waiting for (found by
                // the code review of 2026-09-24). Throwing inside the try releases the gate.
                if (ct.CanBeCanceled)
                {
                    ct.WaitHandle.WaitOne(PollMs);
                    ct.ThrowIfCancellationRequested();
                }
                else
                {
                    Thread.Sleep(PollMs);
                }
            }
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    /// <summary>Takes the section without blocking a thread. For the asynchronous stores.</summary>
    /// <exception cref="TimeoutException">Another process held it for <see cref="Wait"/>.</exception>
    public static async Task<Hold> TakeAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var gate = GateFor(full);
        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var deadline = DateTimeOffset.UtcNow + Wait;
            while (true)
            {
                if (TryOpen(full, out var handle, out var giveUp))
                    return new Hold(gate, handle);
                if (giveUp)
                    return new Hold(gate, handle: null);
                if (DateTimeOffset.UtcNow >= deadline)
                    throw Timeout(full);

                await Task.Delay(PollMs, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    /// <param name="giveUp">
    /// The folder will not take a lock file at all - it is read-only, or the file belongs to another
    /// user. The store still works for the one process that can write it, and refusing to run
    /// because a lock could not be created would be a worse answer than the one being prevented.
    /// </param>
    private static bool TryOpen(string fullPath, out FileStream? handle, out bool giveUp)
    {
        giveUp = false;
        try
        {
            // DeleteOnClose so an ordinary exit leaves nothing behind; FileShare.None is what makes
            // the second process wait. A process killed outright leaves the file, and Windows closes
            // the handle with it, so the next opener gets in.
            handle = new FileStream(
                LockPathFor(fullPath), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
            return true;
        }
        // BEFORE the IOException below, which is its base type: a folder that is not there is not a
        // holder to wait for, and waiting 30 seconds for one would be 30 seconds of nothing.
        catch (DirectoryNotFoundException)
        {
            handle = null;
            giveUp = true;
            return false;
        }
        catch (IOException)
        {
            handle = null;
            return false;   // somebody else has it - wait
        }
        catch (UnauthorizedAccessException)
        {
            handle = null;
            giveUp = true;
            return false;
        }
    }

    /// <summary>The lock's own path, so a caller can see it in a message or a test.</summary>
    public static string LockPathFor(string path) => Path.GetFullPath(path) + ".lock";

    private static TimeoutException Timeout(string fullPath)
        => new($"Waited {Wait.TotalSeconds:0}s for another process to release '{LockPathFor(fullPath)}'.");

    private static SemaphoreSlim GateFor(string fullPath)
        => Gates.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));

    /// <summary>Both shapes, so a synchronous store and an asynchronous one hold the same thing.</summary>
    public sealed class Hold(SemaphoreSlim gate, FileStream? handle) : IDisposable, IAsyncDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;   // disposing twice must not release the gate twice

            try { handle?.Dispose(); }
            catch { /* DeleteOnClose can fail on a folder that vanished; the lock is gone either way */ }
            finally { gate.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
