namespace Enactive.Core.Storage;

using System.Diagnostics;
using System.Text.Json;

/// <summary>Who holds a <see cref="ProcessLease"/>, as the holder wrote it down when it took it.</summary>
/// <param name="Host">The holding program, by its process name (enactive-ui, enactive, the service).</param>
public sealed record LeaseHolder(string Host, int ProcessId, string User, DateTimeOffset Since)
{
    /// <summary>The holder in the words a refusal shows a person.</summary>
    public string Describe()
        => $"{Host} (process {ProcessId}, user {User}) since {Since.ToLocalTime():yyyy-MM-dd HH:mm}";
}

/// <summary>
/// A place one PROCESS works in while it works there, held against the other processes on this machine - which
/// can read who holds it, and are refused rather than made to wait.
///
/// <para><b>Why not <see cref="FileLock"/>.</b> That one guards a read-modify-write of a few milliseconds, so the
/// second process waits its turn. This one is held for as long as a run lasts, so waiting is not an answer, and a
/// refusal must say who is in the way: "in use" with no name sends a person hunting through every program on the
/// machine. The holder writes itself into the file and leaves it readable to others.</para>
///
/// <para><b>Held per process, counted within it.</b> The window runs a foreground and background runs side by
/// side, in one workspace if the person wants; the run queue decides that, not this. Here a second take by the same
/// process only counts, and the file is let go when the last one is.</para>
///
/// <para><b>What a crash leaves.</b> Windows closes a dead process's handles, so the next taker opens the file
/// and writes itself over the old holder - no stale lease to clear by hand.</para>
///
/// <para><b>A place that cannot take a lease file</b> is refused. An access error can hide another holder;
/// proceeding without a handle allowed two hosts to edit the same workspace at once.</para>
/// </summary>
public sealed class ProcessLease : IDisposable
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Held> HeldHere = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private int _released;

    private ProcessLease(string path) => _path = path;

    /// <summary>
    /// Takes the lease, or returns null when it cannot be held exclusively. The holder may be unknown: the file
    /// can be unwritable, unreadable, or not yet filled in by the process that took it.
    /// </summary>
    public static ProcessLease? TryTake(string path, out LeaseHolder? holder)
    {
        var full = Path.GetFullPath(path);
        holder = null;

        lock (Gate)
        {
            if (HeldHere.TryGetValue(full, out var held))
            {
                held.Count++;
                return new ProcessLease(full);
            }

            FileStream handle;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                // FileShare.Read so another process can read who holds it; nothing else may open it for
                // writing, and that is the refusal. DeleteOnClose so a released lease leaves no file.
                handle = new FileStream(full, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No handle, no ownership. In particular, a read-only file can report access denied even while
                // another process holds it; treating that as success bypassed exclusion and cached the false take.
                holder = ReadHolder(full);
                return null;
            }

            Write(handle);
            HeldHere[full] = new Held(handle);
            return new ProcessLease(full);
        }
    }

    /// <summary>Who holds the lease at this path in another process, or null when nobody does.</summary>
    public static LeaseHolder? HolderOf(string path)
    {
        var full = Path.GetFullPath(path);
        lock (Gate)
        {
            if (HeldHere.ContainsKey(full))
                return null;
        }
        return IsHeldElsewhere(full) ? ReadHolder(full) : null;
    }

    /// <summary>
    /// Whether anybody holds the lease at this path now - a run of this process, or another process - and, when another
    /// holds it, who (null when it has not written its name). Unlike <see cref="HolderOf"/>, this process's own counts.
    /// </summary>
    public static bool IsHeld(string path, out LeaseHolder? elsewhere)
    {
        var full = Path.GetFullPath(path);
        elsewhere = null;
        lock (Gate)
        {
            if (HeldHere.ContainsKey(full))
                return true;
        }
        if (!IsHeldElsewhere(full))
            return false;
        elsewhere = ReadHolder(full);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;   // disposing twice must not count down twice

        lock (Gate)
        {
            if (!HeldHere.TryGetValue(_path, out var held) || --held.Count > 0)
                return;
            HeldHere.Remove(_path);
            try { held.Handle.Dispose(); }
            catch { /* DeleteOnClose can fail on a folder that vanished; the lease is gone either way */ }
        }
    }

    /// <summary>
    /// The file exists and cannot be opened for writing: another process has it. Asked again rather than trusted
    /// from the exception, because an IOException also means a disk that failed.
    /// </summary>
    private static bool IsHeldElsewhere(string full)
    {
        if (!File.Exists(full))
            return false;
        try
        {
            using var probe = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static LeaseHolder? ReadHolder(string full)
    {
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<LeaseHolder>(stream);
        }
        catch
        {
            return null;   // written a moment from now, or not by us: refused all the same, without a name
        }
    }

    private static void Write(FileStream handle)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var holder = new LeaseHolder(self.ProcessName, Environment.ProcessId, Environment.UserName, DateTimeOffset.UtcNow);
            handle.SetLength(0);
            JsonSerializer.Serialize(handle, holder);
            handle.Flush(flushToDisk: true);
        }
        catch { /* the lease holds without its name; a refusal then says "another process" */ }
    }

    private sealed class Held(FileStream handle)
    {
        public FileStream Handle { get; } = handle;
        public int Count { get; set; } = 1;
    }
}
