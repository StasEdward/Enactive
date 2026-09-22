namespace Enactive.Core.Context;

/// <summary>
/// The worker's own working area, and the only thing that ever removes anything from it.
///
/// <para><see cref="WorkspaceGuard.ScratchFolder"/> says where it is and why it exists. This says
/// what happens to it afterwards: nothing did, so a workspace used for a month accumulated every
/// helper script and every captured build log any run had ever written, inside the user's project
/// folder. That is the same complaint <c>RunHousekeeping</c> was written for - "nothing ever
/// removed a run, so the list only grew" - arriving in a second place.</para>
///
/// <para><b>Swept by AGE, not emptied per run.</b> Emptying it when a run starts would be simpler
/// and is wrong: a background run and a foreground one can be working in one workspace at the same
/// time, and the second to start would delete the first's scripts out from under it. Age cannot do
/// that to anything a live run is using unless that run has been going for a week.</para>
///
/// <para><b>It is one folder, not one per run.</b> A per-run folder would have to carry the run's
/// id in its name, and then the path stops being something a model can simply type - which was the
/// whole argument for putting the area at a fixed place inside the workspace.</para>
/// </summary>
public static class ScratchArea
{
    /// <summary>
    /// How long something left in the area survives.
    ///
    /// <para>Long enough that somebody coming back the next morning to ask what a run did still
    /// finds what it wrote, short enough that a workspace does not grow a museum. The same week
    /// <c>RunHousekeeping.AWeek</c> uses, for the same reason, and deliberately not shared with it:
    /// one is about records in a database and this is about files on a disk, and a constant two
    /// unrelated rules happen to agree on is not a constant they should have to agree on.</para>
    /// </summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    /// <summary>Where the area is for this workspace. The folder need not exist.</summary>
    public static string PathIn(string workspaceRoot)
        => Path.Combine(
            Path.GetFullPath(workspaceRoot), WorkspaceGuard.ReservedFolder, WorkspaceGuard.ScratchFolder);

    /// <summary>
    /// Makes the area exist, because the worker is TOLD it has one.
    ///
    /// <para>It used to appear only when something wrote into it, which left every instruction
    /// about it false until the moment it stopped mattering. Measured across three days: an agent
    /// looked for its own working area with <c>list_dir</c>, with <c>dir /b /s</c> and with
    /// <c>Get-ChildItem</c>, and each time was told there is no such place - on 2026-09-20 one of
    /// those was among the two unresolved calls that failed a run. A promise in a prompt and a
    /// folder that is not there are a defect however sensible the laziness was.</para>
    ///
    /// <para>Returns the path either way and never throws. A workspace on a read-only share, or
    /// one whose <c>.enactive</c> cannot be created, is a workspace where a great deal else will
    /// fail first and more loudly; this must not be the thing that stops a run before it starts.
    /// </para>
    /// </summary>
    public static string Ensure(string workspaceRoot)
    {
        var path = PathIn(workspaceRoot);

        try { Directory.CreateDirectory(path); }
        catch { /* see above: housekeeping never fails the work */ }

        return path;
    }

    /// <summary>
    /// Removes everything at the top of the area that has not been touched for
    /// <paramref name="keepFor"/>, and answers how many entries went.
    ///
    /// <para>Top level only, and by the entry's own last-write time: a folder of build output whose
    /// newest file is from this morning is not stale because the folder's own timestamp is older.
    /// A directory's LastWriteTimeUtc moves when its immediate children change, so this is checked
    /// against the newest thing inside it rather than against the folder itself.</para>
    ///
    /// <para>Every failure is swallowed, per entry and overall. This is housekeeping: it runs on
    /// the way to doing something the user asked for, and it must never be the reason that fails.
    /// The same rule <c>DiskArtifactStore.PruneOldBackups</c> follows.</para>
    /// </summary>
    public static int Sweep(string workspaceRoot, DateTimeOffset now, TimeSpan? keepFor = null)
    {
        var cutoff = now - (keepFor ?? KeepFor);
        var removed = 0;

        try
        {
            var area = new DirectoryInfo(PathIn(workspaceRoot));
            if (!area.Exists)
                return 0;

            foreach (var entry in area.GetFileSystemInfos())
            {
                if (NewestWriteIn(entry) > cutoff)
                    continue;

                try
                {
                    if (entry is DirectoryInfo directory) directory.Delete(recursive: true);
                    else entry.Delete();
                    removed++;
                }
                catch { /* in use, or gone already - neither is worth failing a run over */ }
            }
        }
        catch { /* housekeeping must never break the work it runs alongside */ }

        return removed;
    }

    /// <summary>
    /// The most recent write anywhere under this entry. For a file that is its own timestamp; for a
    /// folder it is the newest thing in it, because a folder's own timestamp only moves when its
    /// IMMEDIATE children change and a tree being actively written deeper down would look stale.
    /// </summary>
    private static DateTimeOffset NewestWriteIn(FileSystemInfo entry)
    {
        var newest = entry.LastWriteTimeUtc;

        if (entry is DirectoryInfo directory)
        {
            try
            {
                foreach (var child in directory.GetFiles("*", SearchOption.AllDirectories))
                    if (child.LastWriteTimeUtc > newest)
                        newest = child.LastWriteTimeUtc;
            }
            catch { /* unreadable: fall back to the folder's own time */ }
        }

        return new DateTimeOffset(newest, TimeSpan.Zero);
    }
}
