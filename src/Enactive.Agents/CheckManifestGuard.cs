namespace Enactive.Agents;

using Enactive.Core.Checks;

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
public sealed record CheckManifestRestore(IReadOnlyList<CheckManifestChange> Changes, IReadOnlyList<string> Failures)
{
    public static readonly CheckManifestRestore None = new([], []);

    /// <summary>
    /// The sentence the run says it with: which files, that they were put back, and why. Null when nothing changed.
    /// </summary>
    public string? Describe()
    {
        if (Changes.Count == 0)
            return null;

        var named = string.Join(", ", Changes.Select(c => $"{c.Kind.ToString().ToLowerInvariant()} {c.File}"));
        var said = $"This run changed the workspace's check manifests in {CheckStoreFolder} ({named}). They were restored "
                   + "as they were when the run started, because a run may not change the checks that judge runs.";
        return Failures.Count == 0
            ? said
            : said + " Not all of it could be put back - check these by hand: " + string.Join("; ", Failures) + ".";
    }

    private const string CheckStoreFolder = ".enactive/checks";
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
/// the engine's and judges nothing.</para>
///
/// <para><b>A person's own edits are safe</b> because the baseline is taken when THIS run starts: whatever was there
/// then - including an edit made a minute before - is what is restored to, and what was changed while no run was running
/// is never looked at. The one case it cannot tell apart is a person editing a manifest WHILE a run is running in that
/// workspace: that edit is restored too. It is the rare case, and the safe direction (the next run is judged by checks a
/// person wrote, not by a step's); the UI can refuse such an edit while a run is active.</para>
///
/// <para>Kept in memory, not in the checkpoint: a run that is resumed takes its own baseline when it starts, so an edit
/// made between the pause and the resume is a person's and stands. The cost is that a process killed mid-run restores
/// nothing - the run that changed the manifests never reached an end.</para>
/// </summary>
public sealed class CheckManifestGuard
{
    private static readonly StringComparer Names =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _folder;
    private readonly Dictionary<string, byte[]> _kept;
    private readonly bool _wasLink;

    private CheckManifestGuard(string folder, Dictionary<string, byte[]> kept, bool wasLink)
    {
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
        return new CheckManifestGuard(folder, kept, IsLink(folder));
    }

    /// <summary>
    /// Puts back anything a run added, changed or deleted since <see cref="Take"/>, and says what. Idempotent: called
    /// again with nothing changed since, it finds nothing.
    /// </summary>
    public CheckManifestRestore Restore()
    {
        var changes = new List<CheckManifestChange>();
        var failures = new List<string>();

        // The folder replaced by a link (a junction to somewhere else) is not written THROUGH: deleting "added" files
        // there would delete files outside the workspace. The link itself goes, and the folder is made again.
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

        var now = new Dictionary<string, string>(Names);
        foreach (var file in Manifests(_folder))
            now[Path.GetFileName(file)] = file;

        foreach (var (name, path) in now.Where(n => !_kept.ContainsKey(n.Key)))
            Put(name, CheckManifestChangeKind.Added, () => File.Delete(path));

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
                Put(name, CheckManifestChangeKind.Changed, () => Write(path, bytes));
        }

        return changes.Count == 0 && failures.Count == 0
            ? CheckManifestRestore.None
            : new CheckManifestRestore(changes.OrderBy(c => c.File, StringComparer.OrdinalIgnoreCase).ToArray(), failures);

        void Put(string name, CheckManifestChangeKind kind, Action restore)
        {
            changes.Add(new CheckManifestChange(name, kind));
            try { restore(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{name} ({ex.Message})");
            }
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
