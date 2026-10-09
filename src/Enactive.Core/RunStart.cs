namespace Enactive.Core.Artifacts;

/// <summary>
/// The workspace as it was when the run began, measured - what tells a file the run found from one it made, whatever
/// made it.
///
/// <para><b>Why the store's record is not enough.</b> The store records what the file tools write; a command's writes
/// are not in it. Run 89aa8d1b, 2026-10-09: step 2 made a test project with <c>dotnet new xunit</c>, and the template
/// put <c>UnitTest1.cs</c> in it. The step's review saw it as new - the step's snapshots said so - but when step 3
/// deleted it, the person was asked to allow taking away a file that "existed before this run; this run has not
/// changed it": the store had no record of it, and no record read as "the run found it so". The same answer told the
/// change-limit guard the file was the person's, and told restore_file there was nothing to put back.</para>
///
/// <para>So the store's answer is held against this measurement. A path the measurement covers is answered from it: not
/// there when the run began is made by the run; there and changed since, with no copy kept, is changed and lost. A path
/// it does not cover - a folder git ignores, a workspace that could not be measured - is not known, rather than taken to
/// have been there. The bytes a file had are still the store's: it is the one that kept them.</para>
/// </summary>
public sealed class RunStart
{
    private readonly IWorkspaceChanges? _changes;
    private readonly WorkspaceSnapshot? _start;
    private IReadOnlySet<string>? _was;
    private bool _wasRead;

    /// <summary>A run whose start could not be measured: what the store does not know stays not known.</summary>
    public static RunStart Unmeasured { get; } = new(null, null);

    public RunStart(IWorkspaceChanges? changes, WorkspaceSnapshot? start)
    {
        _changes = changes;
        _start = changes is null ? null : start;
    }

    /// <summary>The measurement itself - kept with the run's record so a resumed run measures from the same start.</summary>
    public WorkspaceSnapshot? Snapshot => _start;

    /// <summary>The store's answer for a path, held against how the workspace was when the run began.</summary>
    public async Task<BeforeRun> CorrectAsync(string relativePath, BeforeRun stored, CancellationToken ct)
    {
        var key = Key(relativePath);
        var notKnown = stored.State == BeforeRunState.Untouched ? BeforeRun.Unknown : stored;
        if (await WasAsync(ct) is not { } was)
            return notKnown;

        if (was.Contains(key))
            return stored.State switch
            {
                // There when the run began, yet a file tool found no file: something else of the run had taken it away.
                BeforeRunState.Absent => BeforeRun.Lost,
                BeforeRunState.Untouched => await NowAsync(ct) is { } now
                    ? now.Changed.Contains(key) ? BeforeRun.Lost : BeforeRun.Untouched
                    : BeforeRun.Unknown,
                _ => stored
            };

        if (stored.State == BeforeRunState.Absent)
            return stored;
        // Not there when the run began, and the measurement covers where it is now: the run made it - a command, or a
        // file tool writing over what a command had made.
        return await NowAsync(ct) is { } measured && measured.Paths.Contains(key) ? BeforeRun.Absent : notKnown;
    }

    private async Task<IReadOnlySet<string>?> WasAsync(CancellationToken ct)
    {
        if (_wasRead) return _was;
        if (_changes is null || _start is null) return null;
        try { _was = await _changes.PathsAsync(_start, ct) is { } paths ? paths.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase) : null; }
        catch (ObjectDisposedException) { return null; }   // asked after the run that measured it has ended
        _wasRead = true;
        return _was;
    }

    /// <summary>The paths measured now, and which of them differ from the start - one snapshot, taken only when needed.</summary>
    private async Task<(HashSet<string> Paths, HashSet<string> Changed)?> NowAsync(CancellationToken ct)
    {
        try
        {
            if (await _changes!.TakeAsync(ct) is not { } now
                || await _changes.PathsAsync(now, ct) is not { } paths
                || await _changes.ComparePathsAsync(_start!, now, ct) is not { } changes)
                return null;
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes)
            {
                changed.Add(Key(change.Path));
                if (change.OldPath is { } old) changed.Add(Key(old));
            }
            return (paths.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase), changed);
        }
        catch (ObjectDisposedException) { return null; }
        catch (IOException) { return null; }
    }

    private static string Key(string path)
    {
        var key = path.Replace('\\', '/');
        while (key.StartsWith("./", StringComparison.Ordinal)) key = key[2..];
        return key.TrimStart('/');
    }
}
