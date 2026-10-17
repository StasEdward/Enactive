namespace Enactive.Agents;

using Enactive.Core.Checks;
using Enactive.Core.Context;

/// <summary>What happened to one check manifest while a run ran.</summary>
public enum CheckManifestChangeKind
{
    Added,
    Changed,
    Deleted
}

/// <summary>One manifest a run added, changed or deleted, by its file name in the checks folder.</summary>
public sealed record CheckManifestChange(string File, CheckManifestChangeKind Kind);

/// <summary>What <see cref="CheckManifestGuard.Restore"/> found and put back - and what it could not.</summary>
/// <param name="Restored">False when nothing was put back on purpose - the folder leads outside the workspace.</param>
/// <param name="KeptIn">Where the versions the run wrote were kept, workspace-relative; null when none was.</param>
public sealed record CheckManifestRestore(
    IReadOnlyList<CheckManifestChange> Changes, IReadOnlyList<string> Failures, bool Restored = true, string? KeptIn = null)
{
    public static readonly CheckManifestRestore None = new([], []);

    /// <summary>
    /// The sentence the run says it with: which files, whether they were put back, and why. Null when nothing changed.
    /// </summary>
    public string? Describe()
    {
        if (Changes.Count == 0)
            return null;

        var named = Named(Changes);
        if (!Restored)
            return $"This run changed the workspace's check manifests in {CheckFolder} ({named}). They were NOT restored: "
                   + string.Join("; ", Failures) + ". A run may not change the checks that judge runs - check these by hand.";

        var said = $"This run changed the workspace's check manifests in {CheckFolder} ({named}). They were restored "
                   + "as they were when the run started, because a run may not change the checks that judge runs."
                   + (KeptIn is null ? "" : $" What the run wrote is kept in {KeptIn}, if it was wanted.");
        return Failures.Count == 0
            ? said
            : said + " Not all of it could be put back - check these by hand: " + string.Join("; ", Failures) + ".";
    }

    internal static string Named(IEnumerable<CheckManifestChange> changes)
        => string.Join(", ", changes.Select(c => $"{c.Kind.ToString().ToLowerInvariant()} {c.File}"));

    internal const string CheckFolder = ".enactive/checks";
}

/// <summary>
/// Keeps a run from changing the checks that judge runs (Plan Phase 3.1, anti-tamper).
///
/// <para><b>The gap it closes.</b> The worker's FILE tools cannot write <c>.enactive/</c> (WorkspaceGuard), but a shell
/// command can - nothing reads a command line for the paths it touches. A run judged by its snapshot (CheckSnapshot)
/// cannot change its OWN checks that way; what it can change is the manifests the NEXT run is composed from - a step
/// that loosens or deletes a blocking check would be judging its successors' work by its own rules.</para>
///
/// <para><b>So:</b> the manifests are read when the run starts - every byte of every <c>*.json</c> directly in
/// <c>.enactive/checks</c> - and when it ends, however it ends, anything added, changed or deleted is put back as it
/// was, and the run says so. Only the folder's own manifests: what is kept below it (a check's output, Plan Phase 3.3) is
/// the engine's and judges nothing. What the run wrote is not thrown away: it is kept under
/// <c>.enactive/checks/runs/&lt;runId&gt;/reverted/</c>, below the folder and so not a manifest - a run that was ASKED
/// to write a check leaves the person something to install by hand.</para>
///
/// <para><b>A person's own edits are safe</b> because the baseline is taken when THIS run starts: whatever was there
/// then - including an edit made a minute before - is what is restored to, and what was changed while no run was running
/// is never looked at. The one case it cannot tell apart is a person editing a manifest WHILE a run is running in that
/// workspace: that edit is restored too. It is the rare case, and the safe direction (the next run is judged by checks a
/// person wrote, not by a step's); the UI can refuse such an edit while a run is active.</para>
///
/// <para>Kept in memory, not in the checkpoint: a run that is resumed takes its own baseline when it starts, so an edit
/// made between the pause and the resume is a person's and stands (and is SAID - <see cref="ChangedSince"/>). The cost is
/// that a process killed mid-run restores nothing - the run that changed the manifests never reached an end.</para>
/// </summary>
public sealed class CheckManifestGuard
{
    private static readonly StringComparer Names =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _root;
    private readonly string _folder;
    private readonly Dictionary<string, byte[]> _kept;
    private readonly bool _wasLink;

    private CheckManifestGuard(string root, string folder, Dictionary<string, byte[]> kept, bool wasLink)
    {
        _root = root;
        _folder = folder;
        _kept = kept;
        _wasLink = wasLink;
    }

    /// <summary>The manifests as they are now - the run's baseline. Never throws: a manifest that cannot be read is not kept.</summary>
    public static CheckManifestGuard Take(string workspaceRoot)
    {
        var folder = CheckFolders.WorkspaceFolderOf(workspaceRoot);
        var kept = new Dictionary<string, byte[]>(Names);
        foreach (var file in Manifests(folder))
        {
            try { kept[Path.GetFileName(file)] = File.ReadAllBytes(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable: nothing to restore it to */ }
        }
        return new CheckManifestGuard(workspaceRoot, folder, kept, IsLink(folder));
    }

    /// <summary>
    /// How the workspace's manifests differ from what a snapshot hashed (Plan Phase 3.1): on a resume, what changed while
    /// the run was interrupted. Only said, never put back - the snapshot keeps hashes, not bytes, and a change made while
    /// no run was running is a person's.
    /// </summary>
    public static IReadOnlyList<CheckManifestChange> ChangedSince(IReadOnlyList<CheckManifestHash> hashed, string workspaceRoot)
    {
        var was = hashed.ToDictionary(h => h.File, h => h.Sha256, Names);
        var now = CheckFolders.HashManifests(CheckFolders.WorkspaceFolderOf(workspaceRoot)).ToDictionary(h => h.File, h => h.Sha256, Names);
        return now.Where(n => !was.ContainsKey(n.Key)).Select(n => new CheckManifestChange(n.Key, CheckManifestChangeKind.Added))
            .Concat(was.Where(w => !now.ContainsKey(w.Key)).Select(w => new CheckManifestChange(w.Key, CheckManifestChangeKind.Deleted)))
            .Concat(was.Where(w => now.TryGetValue(w.Key, out var h) && !string.Equals(h, w.Value, StringComparison.OrdinalIgnoreCase))
                .Select(w => new CheckManifestChange(w.Key, CheckManifestChangeKind.Changed)))
            .OrderBy(c => c.File, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The sentence a resumed run says about manifests changed while it was interrupted, or null.</summary>
    public static string? DescribeChangedSince(IReadOnlyList<CheckManifestChange> changes)
        => changes.Count == 0
            ? null
            : $"The checks changed while the run was interrupted ({CheckManifestRestore.Named(changes)} in "
              + $"{CheckManifestRestore.CheckFolder}). This run is still judged by the checks it was started with; the files "
              + "were left as they are, and will judge the next run.";

    /// <summary>
    /// Puts back anything a run added, changed or deleted since <see cref="Take"/>, keeps what the run wrote under
    /// <c>runs/&lt;runId&gt;/reverted/</c>, and says what. Idempotent: called again with nothing changed since, it finds nothing.
    /// </summary>
    /// <param name="runId">The run whose writes are kept; null when no event named one.</param>
    public CheckManifestRestore Restore(Guid? runId = null)
    {
        var changes = new List<CheckManifestChange>();
        var failures = new List<string>();

        // A state folder - or a checks folder that was already a link when the run started - that leads OUT of the
        // workspace is not written through: deleting "added" files there would delete files somewhere else entirely. The
        // changes are still found (reading is harmless) and said; nothing is put back.
        var outside = LeadsOutside(WorkspaceGuard.ReservedFolder)
                      ?? (_wasLink ? LeadsOutside(WorkspaceGuard.ReservedFolder + "/checks") : null);
        if (outside is not null)
        {
            var found = Compare();
            return found.Count == 0
                ? CheckManifestRestore.None
                : new CheckManifestRestore(found, [outside], Restored: false);
        }

        // The checks folder replaced by a link during the run is not written THROUGH either: the link itself goes - which
        // touches nothing it points at - and the folder is made again.
        if (!_wasLink && IsLink(_folder))
        {
            try
            {
                var seen = Manifests(_folder).Select(Path.GetFileName).OfType<string>().ToArray();
                Directory.Delete(_folder);
                foreach (var name in seen.Where(n => !_kept.ContainsKey(n)))
                    changes.Add(new CheckManifestChange(name, CheckManifestChangeKind.Added));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{_folder} is now a link and could not be removed ({ex.Message})");
                return new CheckManifestRestore(changes, failures);
            }
        }

        var keptIn = Path.Combine(_folder, "runs", runId is { } id ? id.ToString() : "unknown-run", "reverted");
        var keptAny = false;
        var now = new Dictionary<string, string>(Names);
        foreach (var file in Manifests(_folder))
            now[Path.GetFileName(file)] = file;

        foreach (var (name, path) in now.Where(n => !_kept.ContainsKey(n.Key)))
            Put(name, CheckManifestChangeKind.Added, () =>
            {
                Keep(path, name);
                File.Delete(path);
            });

        foreach (var (name, bytes) in _kept)
        {
            if (!now.TryGetValue(name, out var path))
            {
                Put(name, CheckManifestChangeKind.Deleted, () => Write(Path.Combine(_folder, name), bytes));
                continue;
            }

            byte[]? current;
            try { current = File.ReadAllBytes(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { current = null; }
            if (current is null || !current.AsSpan().SequenceEqual(bytes))
                Put(name, CheckManifestChangeKind.Changed, () =>
                {
                    if (current is not null) Keep(current, name);
                    Write(path, bytes);
                });
        }

        return changes.Count == 0 && failures.Count == 0
            ? CheckManifestRestore.None
            : new CheckManifestRestore(changes.OrderBy(c => c.File, StringComparer.OrdinalIgnoreCase).ToArray(), failures,
                KeptIn: keptAny ? WorkspaceGuard.KeyFor(_root, keptIn) + "/" : null);

        void Put(string name, CheckManifestChangeKind kind, Action restore)
        {
            changes.Add(new CheckManifestChange(name, kind));
            try { restore(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{name} ({ex.Message})");
            }
        }

        // A copy that cannot be kept does not stop the restore: the next run must not be judged by the run's checks, and
        // losing the run's version is the smaller harm. It is said.
        void Keep(object from, string name)
        {
            try
            {
                Directory.CreateDirectory(keptIn);
                var target = Path.Combine(keptIn, name);
                for (var n = 1; File.Exists(target); n++)
                    target = Path.Combine(keptIn, $"{Path.GetFileNameWithoutExtension(name)}.{n}{Path.GetExtension(name)}");
                if (from is string file) File.Copy(file, target);
                else File.WriteAllBytes(target, (byte[])from);
                keptAny = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"the run's {name} could not be kept ({ex.Message})");
            }
        }
    }

    /// <summary>What differs from the baseline, without touching anything.</summary>
    private List<CheckManifestChange> Compare()
    {
        var now = Manifests(_folder).Select(Path.GetFileName).OfType<string>().ToHashSet(Names);
        var found = now.Where(n => !_kept.ContainsKey(n)).Select(n => new CheckManifestChange(n, CheckManifestChangeKind.Added)).ToList();
        foreach (var (name, bytes) in _kept)
        {
            if (!now.Contains(name))
            {
                found.Add(new CheckManifestChange(name, CheckManifestChangeKind.Deleted));
                continue;
            }
            byte[]? current;
            try { current = File.ReadAllBytes(Path.Combine(_folder, name)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { current = null; }
            if (current is null || !current.AsSpan().SequenceEqual(bytes))
                found.Add(new CheckManifestChange(name, CheckManifestChangeKind.Changed));
        }
        return found.OrderBy(c => c.File, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Why a workspace-relative folder leads outside the workspace once every link on the way is followed, or null when
    /// it does not (or does not exist). WorkspaceGuard's own resolution, so "inside" means here what it means to a tool.
    /// </summary>
    private string? LeadsOutside(string relative)
    {
        try
        {
            WorkspaceGuard.ResolveInside(_root, relative, allowReserved: true);
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"{relative.Replace('\\', '/')} leads outside the workspace ({ex.Message})";
        }
    }

    /// <summary>
    /// Writes the file anew rather than over what is there: a manifest replaced by a link must not be written through.
    /// </summary>
    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) || IsLink(path)) File.Delete(path);
        File.WriteAllBytes(path, bytes);
    }

    private static IEnumerable<string> Manifests(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.GetFiles(folder).Where(f => CheckFolders.IsManifest(Path.GetFileName(f))).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
