namespace Enactive.Core.Schedules;

using System.Diagnostics;
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
/// </summary>
public sealed class RunMarkers
{
    private readonly string _folder;

    public RunMarkers(string folder) => _folder = folder;

    public static RunMarkers Default { get; } = new(DefaultFolder());

    public static string DefaultFolder()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Enactive", "running");

    private string FileFor(Guid scheduleId) => Path.Combine(_folder, scheduleId.ToString("N") + ".json");

    private sealed record Marker(int ProcessId, DateTimeOffset ProcessStartedAt, DateTimeOffset ClaimedAt);

    /// <summary>Whether a run of this schedule is still going.</summary>
    public bool IsRunning(Guid scheduleId) => Read(scheduleId) is not null;

    /// <summary>
    /// Claims this schedule for the current process, or returns null when somebody live already has
    /// it. Dispose to release; a claim not released is cleaned up by the check above once the
    /// process is gone.
    /// </summary>
    public IDisposable? Claim(Guid scheduleId)
    {
        if (IsRunning(scheduleId))
            return null;

        try
        {
            Directory.CreateDirectory(_folder);
            var self = Process.GetCurrentProcess();
            var marker = new Marker(self.Id, new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero),
                                    DateTimeOffset.UtcNow);

            File.WriteAllText(FileFor(scheduleId), JsonSerializer.Serialize(marker));
            return new Release(this, scheduleId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A claim that cannot be written is not a claim. Returning a release here would let the
            // run proceed believing it holds something it does not, which is worse than not running.
            return null;
        }
    }

    /// <summary>
    /// The live marker, or null - deleting the file on the way when it names a process that is
    /// gone, so a crashed run costs one tick rather than every future one.
    /// </summary>
    private Marker? Read(Guid scheduleId)
    {
        var file = FileFor(scheduleId);

        Marker? marker;
        try
        {
            if (!File.Exists(file))
                return null;

            marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(file));
        }
        catch
        {
            // Unreadable means nothing is claimed. Erring the other way would block a schedule on a
            // corrupt file, permanently and silently.
            Forget(scheduleId);
            return null;
        }

        if (marker is null || IsAlive(marker))
            return marker;

        Forget(scheduleId);
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

    private void Forget(Guid scheduleId)
    {
        try { File.Delete(FileFor(scheduleId)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Release(RunMarkers markers, Guid scheduleId) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                markers.Forget(scheduleId);
        }
    }
}
