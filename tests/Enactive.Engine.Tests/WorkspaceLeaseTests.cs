namespace Enactive.Engine.Tests;

using System.Diagnostics;
using System.Text.Json;
using Enactive.Core.Schedules;
using Enactive.Core.Storage;
using Xunit;

/// <summary>
/// One process running in a workspace at a time (WorkspaceLease). Each host kept its own runs apart and nothing kept
/// two hosts apart: a console started by the scheduler while the window ran a task in the same folder was two agents
/// editing the same files.
/// </summary>
public sealed class WorkspaceLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-lease", Guid.NewGuid().ToString("N"));

    public WorkspaceLeaseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private string Workspace(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Another holder of the lease file, as another process holds it: sharing is decided per handle, so a handle this
    /// test opens past ProcessLease is, to the file system, somebody else.
    /// </summary>
    internal static FileStream ForeignHolder(string workspaceRoot, string host = "other-host")
    {
        var path = WorkspaceLease.PathFor(workspaceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 1, FileOptions.DeleteOnClose);
        JsonSerializer.Serialize(handle, new LeaseHolder(host, 4242, "someone", DateTimeOffset.UtcNow));
        handle.Flush();
        return handle;
    }

    private static bool OthersCanTake(string workspaceRoot)
    {
        try
        {
            using var other = new FileStream(WorkspaceLease.PathFor(workspaceRoot), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (IOException) { return false; }
    }

    [Fact]
    public void A_workspace_another_process_holds_is_refused_with_who_holds_it()
    {
        var workspace = Workspace("held");
        using var other = ForeignHolder(workspace);

        Assert.Null(WorkspaceLease.TryTake(workspace, out var holder));
        Assert.Equal("other-host", holder?.Host);

        var refused = Assert.Throws<WorkspaceInUseException>(() => WorkspaceLease.Take(workspace));
        Assert.Contains("other-host (process 4242, user someone)", refused.Message);
    }

    /// <summary>
    /// The window runs a foreground run and background runs side by side, in one folder if the person wants. Which of
    /// them may run is the run queue's to decide; the lease keeps out other PROCESSES, and is let go with the last run.
    /// </summary>
    [Fact]
    public void Takes_by_one_process_count_and_the_last_release_lets_the_workspace_go()
    {
        var workspace = Workspace("counted");

        var first = WorkspaceLease.Take(workspace);
        var second = WorkspaceLease.Take(workspace);
        first.Dispose();
        first.Dispose();   // twice must not count down twice

        Assert.False(OthersCanTake(workspace), "The workspace was let go while a run of this process still held it.");

        second.Dispose();
        Assert.True(OthersCanTake(workspace));
        Assert.False(File.Exists(WorkspaceLease.PathFor(workspace)), "A released lease left its file behind.");
    }

    /// <summary>
    /// A host that dies without letting go must not leave its folder refused until somebody deletes a file by hand.
    /// Proved with a real process: one that holds the lease, says so, and is killed.
    /// </summary>
    [Fact]
    public async Task A_lease_held_by_a_process_that_dies_is_free_again()
    {
        var workspace = Workspace("crashed");
        var path = WorkspaceLease.PathFor(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var script = $"$f = [IO.File]::Open('{path.Replace("'", "''")}', 'OpenOrCreate', 'ReadWrite', 'Read'); "
                     + "$b = [Text.Encoding]::UTF8.GetBytes('{\"Host\":\"powershell\",\"ProcessId\":' + $PID + ',\"User\":\"x\",\"Since\":\"2026-10-11T00:00:00Z\"}'); "
                     + "$f.Write($b, 0, $b.Length); $f.Flush(); [Console]::WriteLine('held'); Start-Sleep -Seconds 60";
        using var holder = Process.Start(new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Assert.Equal("held", await holder.StandardOutput.ReadLineAsync(wait.Token));

            Assert.Null(WorkspaceLease.TryTake(workspace, out var named));
            Assert.Equal(holder.Id, named?.ProcessId);
        }
        finally
        {
            holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync();
        }

        using var taken = WorkspaceLease.TryTake(workspace, out _);
        Assert.NotNull(taken);
    }

    /// <summary>No handle means no ownership: an unwritable lease must never permit an unprotected run.</summary>
    [Fact]
    public void A_place_that_takes_no_lease_file_is_refused()
    {
        var notAFolder = Path.Combine(_root, "a-file");
        File.WriteAllText(notAFolder, "");

        using var lease = WorkspaceLease.TryTake(Path.Combine(notAFolder, "workspace"), out _);

        Assert.Null(lease);
    }

    [Fact]
    public void A_readonly_lease_held_elsewhere_is_refused_and_a_failed_take_is_not_cached()
    {
        var workspace = Workspace("readonly");
        var path = WorkspaceLease.PathFor(workspace);
        using (var other = ForeignHolder(workspace))
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                using var first = WorkspaceLease.TryTake(workspace, out _);
                using var second = WorkspaceLease.TryTake(workspace, out _);
                Assert.Null(first);
                Assert.Null(second);
            }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
        }
        using var released = WorkspaceLease.TryTake(workspace, out _);
        Assert.NotNull(released);
    }

    // ── a scheduled run ─────────────────────────────────────────────────────

    /// <summary>
    /// The schedule runner marks an occurrence fired before it composes the run. Refused its folder after that mark,
    /// a schedule whose folder was busy with the window's task skipped its day without a word.
    /// </summary>
    [Fact]
    public void A_schedule_whose_workspace_is_busy_is_left_due_and_unclaimed()
    {
        var workspace = Workspace("scheduled");
        var markers = new RunMarkers(Path.Combine(_root, "markers"));
        var schedule = Guid.NewGuid();
        using var other = ForeignHolder(workspace, host: "enactive-ui");

        Assert.Null(ScheduledStart.TryBegin(markers, schedule, workspace, out var busy));

        Assert.Contains("enactive-ui", busy);
        Assert.Contains("next tick", busy);
        Assert.False(markers.IsRunning(schedule), "The schedule stayed claimed, so the next tick would read it as running.");
    }

    [Fact]
    public void A_scheduled_run_holds_its_schedule_and_its_workspace_until_it_ends()
    {
        var workspace = Workspace("scheduled");
        var markers = new RunMarkers(Path.Combine(_root, "markers"));
        var schedule = Guid.NewGuid();

        var started = ScheduledStart.TryBegin(markers, schedule, workspace, out var busy);

        Assert.NotNull(started);
        Assert.Null(busy);
        Assert.True(markers.IsRunning(schedule));
        Assert.False(OthersCanTake(workspace));

        started.Dispose();
        Assert.False(markers.IsRunning(schedule));
        Assert.True(OthersCanTake(workspace));
    }
}
