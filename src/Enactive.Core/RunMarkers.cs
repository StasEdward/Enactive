namespace Enactive.Core.Schedules;

using System.Diagnostics;
using System.Text;
using System.Text.Json;

/// <summary>
/// Which schedules have a run in flight, visible ACROSS PROCESSES.
///
/// <para>It has to be across processes: the thing that runs a schedule is a separate process from
/// the app, and may be a separate process from the previous run of the same schedule. An in-memory
/// set would answer "nothing is running" to every one of them.</para>
///
/// <para><b>A marker is a claim, not a promise.</b> A run that is killed, or a machine that loses
/// power, leaves a file behind. If the file alone meant "running", that schedule would be blocked
/// for ever and the only symptom would be a task that quietly stopped happening. So a marker names
/// the process that made it, and is believed only while that process is still there.</para>
///
/// <para>The process is identified by its id AND its start time. Windows reuses process ids, and a
/// stale marker whose id has been handed to something unrelated would block a schedule just as
/// permanently — with the added charm of depending on what else the machine happened to start.</para>
///
/// <para><b>Nothing is written or deleted except under the schedule's lock.</b> "Is it free? Then
/// it is mine" is two steps, and so is "it is stale, so I remove it". Done bare, two overlapping
/// ticks each found the schedule free and each ran it; two recoverers of one dead marker each took
/// it over; and one of them could delete the marker the other had just written, believing it was
/// still the dead one. The lock is a second file, held open with no sharing for the few file
/// operations a decision takes. It is the operating system that refuses the second opener, in this
/// process or any other, and that lets go when the holder dies - so the lock, unlike the marker,
/// can never be left behind by a crash and needs no recovery of its own.</para>
/// </summary>
public sealed class RunMarkers
{
    /// <summary>
    /// How long to wait for a schedule's lock. It is held for a handful of file operations, so
    /// this is never reached by contention between runs. It is there because giving up at once
    /// turned every passing glance at the lock file - a virus scanner, an indexer - into "busy",
    /// and waiting for ever would hang a tick on a lock file nobody can open.
    /// </summary>
    private static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(2);

    private readonly string _folder;
    private readonly TimeSpan _lockWait;

    public RunMarkers(string folder) : this(folder, DefaultLockWait) { }

    /// <summary>With the patience chosen, so a test can ask what happens while the lock is held without sitting it out.</summary>
    internal RunMarkers(string folder, TimeSpan lockWait)
    {
        _folder = folder;
        _lockWait = lockWait;
    }

    public static RunMarkers Default { get; } = new(DefaultFolder());

    public static string DefaultFolder()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Enactive", "running");

    private string FileFor(Guid scheduleId) => Path.Combine(_folder, scheduleId.ToString("N") + ".json");

    private string LockFor(Guid scheduleId) => Path.Combine(_folder, scheduleId.ToString("N") + ".lock");

    /// <summary>
    /// The token is what makes a claim somebody's. Process and start time say whether the claimer
    /// is alive, not which claim this is: two claims from one process are told apart only by it. A
    /// marker written before tokens existed reads as the empty one, which no release holds.
    /// </summary>
    private sealed record Marker(int ProcessId, DateTimeOffset ProcessStartedAt, DateTimeOffset ClaimedAt, Guid Token);

    /// <summary>
    /// Whether a run of this schedule is still going.
    ///
    /// <para>A marker that is there but is no live claim - dead process, or unreadable - is removed
    /// on the way, so a crashed run does not leave its file for ever. But only under the lock, and
    /// only after reading it again there: the first look takes no lock, and what it saw may have
    /// been a claim half written, or may since have been replaced by a live one.</para>
    /// </summary>
    public bool IsRunning(Guid scheduleId)
    {
        if (Read(scheduleId, out var leftover) is not null)
            return true;
        if (!leftover)
            return false;

        using var gate = Lock(scheduleId);

        // Somebody is deciding this schedule right now - most likely writing the very marker that
        // did not read. Busy is the answer that cannot start a second run; "free" here once meant
        // deleting a claim from under the process that was making it.
        if (gate is null)
            return true;

        if (Read(scheduleId, out leftover) is not null)
            return true;

        // Unreadable means nothing is claimed. Erring the other way would block a schedule on a
        // corrupt file, permanently and silently.
        if (leftover)
            Forget(scheduleId);
        return false;
    }

    /// <summary>
    /// Claims this schedule for the current process, or returns null when somebody live already has
    /// it. Dispose to release; a claim not released is taken over by the next claim, or cleaned up
    /// by the check above, once the process is gone.
    /// </summary>
    public IDisposable? Claim(Guid scheduleId)
    {
        try
        {
            Directory.CreateDirectory(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using var gate = Lock(scheduleId);

        // No lock, no claim: whoever holds it is claiming, releasing or cleaning up this schedule,
        // and the next tick will see what they decided.
        if (gate is null)
            return null;

        if (Read(scheduleId, out _) is not null)
            return null;

        try
        {
            using var self = Process.GetCurrentProcess();
            var marker = new Marker(self.Id, new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero),
                                    DateTimeOffset.UtcNow, Guid.NewGuid());

            // Written over whatever dead marker is there rather than after deleting it: a delete
            // followed by a create is refused on Windows while a reader still has the old file
            // open, and that would turn a harmless glance into a lost tick.
            using var file = new FileStream(FileFor(scheduleId), FileMode.Create, FileAccess.Write, Lenient);
            file.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(marker)));

            return new Release(this, scheduleId, marker.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A claim that cannot be written is not a claim. Returning a release here would let the
            // run proceed believing it holds something it does not, which is worse than not running.
            // Whatever part of it did reach the disk does not read, and is cleaned up as a leftover.
            return null;
        }
    }

    /// <summary>
    /// Readers and the writer of a marker refuse each other nothing. With the default sharing, a
    /// reader that happened to have the file open made the owner's delete fail, and the schedule
    /// stayed claimed for as long as the owner lived. Exclusion is the lock file's job alone.
    /// </summary>
    private const FileShare Lenient = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>
    /// The live marker, or null. <paramref name="leftover"/> says that a file is there all the same -
    /// one naming a process that is gone, or one that does not read. Deletes nothing: whether a
    /// leftover may go depends on who holds the lock, which is the caller's to know.
    /// </summary>
    private Marker? Read(Guid scheduleId, out bool leftover)
    {
        leftover = false;

        string text;
        try
        {
            using var file = new FileStream(FileFor(scheduleId), FileMode.Open, FileAccess.Read, Lenient);
            using var reader = new StreamReader(file, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            leftover = true;
            return null;
        }

        Marker? marker;
        try
        {
            marker = JsonSerializer.Deserialize<Marker>(text);
        }
        catch (JsonException)
        {
            marker = null;
        }

        if (marker is not null && IsAlive(marker))
            return marker;

        leftover = true;
        return null;
    }

    /// <summary>
    /// Whether the process that made this marker is still there.
    ///
    /// <para>The start time is compared as well as the id: process ids are reused, and believing a
    /// stale marker because its number was handed to something unrelated would block a schedule
    /// permanently, on a machine-dependent coin toss.</para>
    /// </summary>
    private static bool IsAlive(Marker marker)
    {
        try
        {
            using var process = Process.GetProcessById(marker.ProcessId);
            var started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);

            // A second of slack: the two readings come from different calls and the value is stored
            // through JSON, which is not a place to demand tick-for-tick equality.
            return (started - marker.ProcessStartedAt).Duration() < TimeSpan.FromSeconds(1);
        }
        catch (ArgumentException)
        {
            return false;   // no such process
        }
        catch (InvalidOperationException)
        {
            return false;   // it exited between the lookup and the question
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            // Cannot see it - most plausibly another user's process with the same reused id. Not
            // ours, so not a live claim of ours.
            return false;
        }
    }

    /// <summary>
    /// This schedule's lock, or null when it could not be had in time. Dispose to let go.
    ///
    /// <para>The lock file is never deleted. Removing it would bring back the race it is there to
    /// close: one process still holding the file that was removed, another holding the new file of
    /// the same name, each sure it is alone. An empty file per schedule is the price.</para>
    /// </summary>
    private FileStream? Lock(Guid scheduleId)
    {
        var waited = Stopwatch.StartNew();
        var pause = new SpinWait();

        while (true)
        {
            try
            {
                return new FileStream(LockFor(scheduleId), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                      FileShare.None, bufferSize: 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held by somebody else, or not openable at all (no folder yet, no rights). The
                // two are not told apart: either way it is not ours, and the wait is bounded.
                if (waited.Elapsed >= _lockWait)
                    return null;
            }

            pause.SpinOnce();
        }
    }

    /// <summary>Removes the marker file. Only ever called with the lock held.</summary>
    private void Forget(Guid scheduleId)
    {
        try { File.Delete(FileFor(scheduleId)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Ends the claim with this token, and only that one. A release used to delete whatever marker
    /// the schedule had: when the marker was no longer its own - lost and claimed again by somebody
    /// else - it cleared theirs, and the schedule read as free with their run still going.
    /// </summary>
    private void EndClaim(Guid scheduleId, Guid token)
    {
        using var gate = Lock(scheduleId);

        // Without the lock nothing is deleted, not even after a look at the token: between the
        // look and the delete the marker could become somebody else's. The marker then stays until
        // this process is gone, and is recovered as any crashed run's is.
        if (gate is null)
            return;

        if (Read(scheduleId, out _) is { } marker && marker.Token == token)
            Forget(scheduleId);
    }

    private sealed class Release(RunMarkers markers, Guid scheduleId, Guid token) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                markers.EndClaim(scheduleId, token);
        }
    }
}
