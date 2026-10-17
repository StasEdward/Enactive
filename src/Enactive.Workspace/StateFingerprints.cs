namespace Enactive.Workspace;

using System.Security.Cryptography;
using System.Text;
using Enactive.Core.Builds;
using Enactive.Core.Checks;

/// <summary>
/// The state of a workspace a "Run checks now" record is about (Plan Phase 3.2, staged runs), as one digest - taken when
/// the checks ran and taken again, the same way, to tell whether the record still describes the workspace. A record whose
/// fingerprint no longer matches is outdated; one that matches is "checked at its time", never a standing green.
///
/// <para><b>In git</b>: HEAD, a hash of <c>git status --porcelain</c> and the content of every file it lists - any change
/// in the tracked tree changes it, and so does a new file git does not ignore. <b>Outside git</b>: the content of the paths
/// the run proposed and of the files the checks' matches cover - which does not catch a change anywhere else, and the
/// fingerprint says so (<see cref="StateFingerprint.Caveat"/>).</para>
///
/// <para>Taking one never writes: git runs through AutomaticGit, which sets <c>GIT_OPTIONAL_LOCKS=0</c> - a plain
/// <c>git status</c> refreshes the index and writes it back, a write in the person's repository for a read they asked for -
/// and with none of the repository's own helpers (hooks, fsmonitor, filters).</para>
/// </summary>
public static class StateFingerprints
{
    /// <summary>
    /// The most files whose contents go into one fingerprint (as many as a run protects, ProtectedFiles.MaxFiles). Every one
    /// is read whole each time a record is shown: a glob over a large tree, or thousands of untracked files, would hold up
    /// every page that lists the records. Past it the rest count by name only, and the caveat says so.
    /// </summary>
    public const int MaxFiles = ProtectedFiles.MaxFiles;

    /// <summary>What a fingerprint outside git does not prove - always said with it.</summary>
    public const string FilesCaveat = "Not a git workspace: only the files the run proposed and the files the checks' matches "
        + "cover were fingerprinted, so it does not catch everything - a change anywhere else leaves this record looking current.";

    /// <summary>What a git fingerprint does not see.</summary>
    public const string GitCaveat = "Files git ignores are not part of the fingerprint.";

    /// <summary>The engine's own folder: its runs, its records, the check output just written - never the work.</summary>
    private const string Own = ".enactive";

    /// <summary>
    /// The workspace's fingerprint now: from git when it is a git work tree that git will answer for, otherwise from the
    /// files - the run's proposed <paramref name="proposed"/> (workspace-relative or full paths) and what
    /// <paramref name="matches"/> and <paramref name="ecosystems"/> cover. Never throws for a file it cannot read: an
    /// unreadable file is part of the state as "unreadable".
    /// </summary>
    /// <param name="ecosystems">The ecosystems of checks that run through one with no matches of their own: the files they own.</param>
    public static async Task<StateFingerprint> TakeAsync(string root, IEnumerable<string> proposed, IEnumerable<string> matches,
        IEnumerable<IEcosystem> ecosystems, CancellationToken ct)
    {
        root = Path.GetFullPath(root);
        return await GitAsync(root, ct)
               ?? Files(root, Relative(root, proposed), matches.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal)
                   .Order(StringComparer.Ordinal).ToArray(), ecosystems.ToArray());
    }

    /// <summary>
    /// Whether the workspace is still in the state <paramref name="recorded"/> was taken of: the fingerprint taken again,
    /// the same way and over the same inputs (the proposed paths and globs it kept), and compared. False when it cannot be
    /// told - a fingerprint that could not be taken, git that will not answer now: a record is never shown current on a
    /// guess.
    /// </summary>
    /// <param name="known">The ecosystems the engine knows, to find the ones the fingerprint names.</param>
    public static async Task<bool> MatchesAsync(string root, StateFingerprint recorded, IReadOnlyList<IEcosystem> known, CancellationToken ct)
        => await AgainAsync(root, recorded, known, ct) is { } now && Same(now, recorded);

    /// <summary>
    /// The fingerprint taken now the way <paramref name="recorded"/> was - in git from git, outside it over the paths and
    /// globs it kept - or null when it cannot be taken that way now. For a list of records that share their inputs, which
    /// are then compared with one fingerprint (<see cref="Same"/>) instead of each taking it again.
    /// </summary>
    public static async Task<StateFingerprint?> AgainAsync(string root, StateFingerprint recorded, IReadOnlyList<IEcosystem> known,
        CancellationToken ct)
    {
        if (recorded.Kind == FingerprintKind.None || !Directory.Exists(root))
            return null;
        root = Path.GetFullPath(root);
        return recorded.Kind == FingerprintKind.Git
            ? await GitAsync(root, ct)
            : Files(root, recorded.Proposed ?? [], recorded.Matches ?? [],
                known.Where(e => recorded.Ecosystems?.Contains(e.Name, StringComparer.OrdinalIgnoreCase) == true).ToArray());
    }

    /// <summary>Whether two fingerprints are of one state: the same kind and digest. One that holds no digest matches nothing.</summary>
    public static bool Same(StateFingerprint now, StateFingerprint recorded)
        => recorded.Kind != FingerprintKind.None && !string.IsNullOrEmpty(recorded.Digest)
           && now.Kind == recorded.Kind && string.Equals(now.Digest, recorded.Digest, StringComparison.Ordinal);

    /// <summary>The git fingerprint, or null when this is not a git work tree or git will not answer (AutomaticGit refuses some).</summary>
    private static async Task<StateFingerprint?> GitAsync(string root, CancellationToken ct)
    {
        // The repository's top, found on the disk as git finds it - the nearest folder up with a .git - and git held to
        // it. A workspace is often a FOLDER of a repository; held to the folder (AutomaticGit's default), git read it as
        // the whole tree and named the repository's files from its top, so every changed file was looked for under the
        // wrong path - all "(gone)", and a second edit of a file already changed went unseen.
        if (TopOf(root) is not { } top)
            return null;
        var prefix = Path.GetRelativePath(top, root).Replace('\\', '/');
        prefix = prefix == "." ? "" : prefix.TrimEnd('/') + "/";

        Task<(int Exit, string Output)> Git(params string[] args) => AutomaticGit.RunAsync(root, args, ct, worktree: top);
        var (insideExit, inside) = await Git("rev-parse", "--is-inside-work-tree");
        if (insideExit != 0 || inside.Trim() != "true")
            return null;
        var (headExit, head) = await Git("rev-parse", "--verify", "--quiet", "HEAD");
        // -z: a path with a space or a quote is given as it is, not quoted; --untracked-files=all: each new file, not its
        // folder - a folder's line says nothing when a second file appears in it. "-- .": this workspace's folder only,
        // run from it - another folder of the repository is not this workspace's work.
        var (statusExit, status) = await Git("status", "--porcelain=v1", "-z", "--untracked-files=all", "--", ".");
        if (statusExit != 0)
            return null;

        // Status names paths from the repository's top: made the workspace's own, or left out when outside it.
        string? Own(string path) => prefix.Length == 0 ? path
            : path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : null;

        var entries = new List<string>();
        var paths = new List<string>();
        var parts = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var entry = parts[i];
            if (entry.Length < 4) continue;
            var path = Own(entry[3..]);
            // A rename or a copy is followed by the path it came from.
            var from = (entry[0] is 'R' or 'C' || entry[1] is 'R' or 'C') && i + 1 < parts.Length ? parts[++i] : null;
            var fromOwn = from is null ? null : Own(from);
            if (path is null && fromOwn is null) continue;
            // The engine's own folder is left out: this record, its output files and the history are written there AFTER
            // the fingerprint is taken, and a workspace whose .gitignore does not name it would read as changed by the act
            // of keeping the record. Decided on the workspace's own path: the folder is the WORKSPACE's .enactive.
            if ((path is null || IsOwn(path)) && (fromOwn is null || IsOwn(fromOwn))) continue;
            entries.Add(entry[..3] + (path ?? "") + (from is null ? "" : "\t" + (fromOwn ?? from)));
            if (path is not null && !IsOwn(path))
                paths.Add(path);
        }

        var text = new StringBuilder("git\n");
        var headId = headExit == 0 && head.Trim() is { Length: > 0 } id ? id : null;
        text.Append(headId ?? "(no commit)").Append('\n');
        text.Append(Hex(Encoding.UTF8.GetBytes(string.Join("\n", entries)))).Append('\n');
        var hashed = 0;
        foreach (var path in paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (hashed >= MaxFiles) break;
            text.Append(path).Append('\t').Append(HashOrGone(root, path)).Append('\n');
            hashed++;
        }
        return new StateFingerprint(FingerprintKind.Git, Hex(Encoding.UTF8.GetBytes(text.ToString())))
        {
            Head = headId,
            Files = hashed,
            Caveat = paths.Count > MaxFiles
                ? $"{GitCaveat} Of the {paths.Count} files git lists as changed, the contents of the first {MaxFiles} were hashed; the rest count by name only."
                : GitCaveat
        };
    }

    /// <summary>The fingerprint outside git: the proposed paths and the covered files, each by its content.</summary>
    private static StateFingerprint Files(string root, IReadOnlyList<string> proposed, IReadOnlyList<string> matches,
        IReadOnlyList<IEcosystem> ecosystems)
    {
        Func<string, bool>? owned = ecosystems.Count == 0 ? null : path => ecosystems.Any(e => e.Owns(path));
        var paths = new SortedSet<string>(proposed, StringComparer.Ordinal);
        var more = 0;
        foreach (var path in ProtectedFiles.Covered(root, matches, owned))
        {
            if (paths.Contains(path)) continue;
            if (paths.Count >= MaxFiles)
            {
                more++;
                continue;
            }
            paths.Add(path);
        }

        var text = new StringBuilder("files\n");
        foreach (var path in paths)
            text.Append(path).Append('\t').Append(HashOrGone(root, path)).Append('\n');
        return new StateFingerprint(FingerprintKind.Files, Hex(Encoding.UTF8.GetBytes(text.ToString())))
        {
            Files = paths.Count,
            Proposed = proposed,
            Matches = matches,
            Ecosystems = ecosystems.Count == 0 ? null : ecosystems.Select(e => e.Name).ToArray(),
            Caveat = more > 0 ? $"{FilesCaveat} Of the files the checks cover, {more} past the first {MaxFiles} were not hashed." : FilesCaveat
        };
    }

    /// <summary>Paths as the fingerprint keeps them: workspace-relative with '/', inside the workspace, ordered and once each.</summary>
    private static IReadOnlyList<string> Relative(string root, IEnumerable<string> paths)
    {
        var relative = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path;
            try { path = Path.IsPathRooted(raw) ? Path.GetRelativePath(root, raw) : raw; }
            catch (ArgumentException) { continue; }
            path = path.Replace('\\', '/').TrimStart('/');
            if (path.Length == 0 || path == ".." || path.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(path) || IsOwn(path))
                continue;
            relative.Add(path);
        }
        return relative.ToArray();
    }

    /// <summary>
    /// The work tree's top above <paramref name="root"/> - the nearest folder holding a <c>.git</c> folder or file (a linked
    /// work tree's) - or null outside any repository.
    /// </summary>
    private static string? TopOf(string root)
    {
        for (var folder = new DirectoryInfo(root); folder is not null; folder = folder.Parent)
        {
            var git = Path.Combine(folder.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return folder.FullName;
        }
        return null;
    }

    private static bool IsOwn(string path)
        => path.Equals(Own, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Own + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>A file's content hash, or what stands in for it: gone, or there and unreadable - each a state of its own.</summary>
    private static string HashOrGone(string root, string relative)
    {
        var full = Path.Combine(root, relative);
        if (!File.Exists(full)) return "(gone)";
        return ProtectedFiles.HashOf(full) ?? "(unreadable)";
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
