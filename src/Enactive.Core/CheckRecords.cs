namespace Enactive.Core.Checks;

using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Enactive.Core.Context;
using Enactive.Core.Execution;

/// <summary>
/// Where a declared check's result came from (Plan Phase 3.3): which check, where it fired, the boundary it belongs to,
/// why it waited when it did, and where its whole output is kept. Carried on the result (CriterionResult.Check) and into
/// the criterion event's payload as values - a reader of the run asks "which check, on which step" of the payload, never
/// of the label in the sentence.
/// </summary>
/// <param name="CheckId">The check, by its id in the run's <see cref="CheckSnapshot"/>.</param>
/// <param name="FiredOn">Where it ran: after a write, at a step's gate, at a wave's end, at the run's end - or, for a staged
/// run, the apply it is owed at.</param>
public sealed record CheckFiring(string CheckId, CheckBoundary FiredOn)
{
    /// <summary>The step it belongs to (a step check, a write check), by its number in the run; null for a quick action's one step.</summary>
    public int? Step { get; init; }

    /// <summary>The wave it was owed at, for one that fired on a wave.</summary>
    public int? Wave { get; init; }

    /// <summary>Why it fired here and not where it was first owed - the reason a person was told when it was put off.</summary>
    public string? Deferred { get; init; }

    /// <summary>Workspace-relative path of the file that holds its whole output, with '/'; null when there was none to keep.</summary>
    public string? DetailPath { get; init; }
}

/// <summary>
/// What a declared check leaves behind on disk (Plan Phase 3.3): its whole output, one file a run, under
/// <c>.enactive/checks/runs/&lt;runId&gt;/</c> - the folder the manifest guard keeps a run's reverted manifests in, so a run's
/// traces are in one place and go with it. Never a manifest: only the checks folder's own <c>*.json</c> are read as one
/// (CheckFolders), and the guard never looks below it.
/// </summary>
public static class CheckRecords
{
    /// <summary>
    /// The most of a check's output a detail file keeps, in characters (about a megabyte): its start and its end, with the
    /// cut said. A check's output is whatever its command prints - a test runner in verbose mode, or a command that loops,
    /// prints without end - and each fires once per step, wave or turn: kept whole, a run could fill the disk under a folder
    /// nobody looks in. The summary in the event is cut far shorter (SuccessEvaluator.Trim); this is for the person who
    /// opens the file to read past that.
    /// </summary>
    public const int MaxDetailChars = 1_000_000;

    /// <summary>The folder of one run's records, workspace-relative with '/' - the runId in the Guid "D" form, as the guard writes it.</summary>
    public static string RelativeRunFolder(Guid runId) => $".enactive/checks/runs/{runId:D}";

    /// <summary>The name a boundary goes by in a payload and a file name: write, step, wave, apply, run.</summary>
    public static string NameOf(CheckBoundary boundary) => boundary.ToString().ToLowerInvariant();

    /// <summary>
    /// Writes a check's whole output to <c>&lt;run folder&gt;/&lt;at&gt;-&lt;checkId&gt;.txt</c> and returns its workspace-relative
    /// path, or null when nothing could be written - the result still stands, it only has no file. Each result its own file:
    /// a check run again on a step's second attempt is numbered beside the first, not written over it, so the first event
    /// still points at what it said. With <paramref name="replace"/> the file is written over: a write-time check runs every
    /// turn of an edit, and keeping each would be a file per turn for a finding the next turn makes old.
    /// </summary>
    /// <param name="at">Where it fired, as a file name's start: "step-3", "wave-1", "run", "step-3-write".</param>
    public static string? WriteDetail(string root, Guid runId, string at, string checkId, string text, bool replace = false)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            var relative = RelativeRunFolder(runId);
            // Through WorkspaceGuard, as the guard restores manifests: a state folder a command replaced with a link must
            // not have the engine write through it to wherever it leads.
            var folder = WorkspaceGuard.ResolveInside(root, relative, allowReserved: true);
            Directory.CreateDirectory(folder);
            var stem = $"{at}-{Safe(checkId)}";
            var name = stem + ".txt";
            if (!replace)
                for (var n = 2; File.Exists(Path.Combine(folder, name)); n++)
                    name = $"{stem}.{n}.txt";
            var file = Path.Combine(folder, name);
            if (File.Exists(file) && new FileInfo(file).LinkTarget is not null) File.Delete(file);
            File.WriteAllText(file, Shortening.HeadAndTail(text, MaxDetailChars));
            return relative + "/" + name;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes a run's records with the run (Plan Phase 3.3) - the detail files and the manifests the guard kept. Called by
    /// each run store where it deletes the run, BEFORE the run itself: a folder that cannot be removed throws, and the run
    /// is left in the history for a person to delete again, instead of gone with its files left where nothing lists them.
    /// A folder that leads outside the workspace is never deleted through: what is there is not the run's.
    /// </summary>
    public static void RemoveRun(string root, Guid runId)
    {
        string folder;
        try { folder = WorkspaceGuard.ResolveInside(root, RelativeRunFolder(runId), allowReserved: true); }
        catch (ArgumentException) { return; }
        if (!Directory.Exists(folder)) return;
        // A link put where the run's folder was is removed as a link: deleting recursively through it would delete what
        // it leads to.
        if (new DirectoryInfo(folder).LinkTarget is not null) Directory.Delete(folder);
        else Directory.Delete(folder, recursive: true);
    }

    /// <summary>A check id as part of a file name. Ids are slugs already (CheckStore refuses others); this is the backstop.</summary>
    private static string Safe(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(id.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray());
        return safe.Length == 0 ? "check" : safe;
    }
}

/// <summary>A file a check protects (Plan Phase 3.4), as the run's baseline found it.</summary>
/// <param name="Path">Workspace-relative, with '/'.</param>
/// <param name="Sha256">Its content's SHA-256, lower-case hex.</param>
/// <param name="CheckIds">The enabled checks whose <see cref="CheckDefinition.Protects"/> cover it, ordered.</param>
public sealed record ProtectedFile(string Path, string Sha256, IReadOnlyList<string> CheckIds)
{
    /// <summary>The check a finding about this file names: the first of those that protect it. Not stored: it is read off the list.</summary>
    [JsonIgnore]
    public string CheckId => CheckIds.Count > 0 ? CheckIds[0] : "";
}

/// <summary>
/// The files a run's checks protect (Plan Phase 3.4), hashed when the run takes its baseline. A step that removes or
/// renames one is sent back; one that edits one has it shown to the reviewer. What "removed" means is decided against
/// THESE hashes, taken once, and kept with the baseline - a resumed run must not take the files as they are after its
/// first half for the ones it started with.
/// </summary>
public static class ProtectedFiles
{
    /// <summary>
    /// The most files a run protects, hashed when it starts. Every file a glob covers is read whole at every run's start: a
    /// glob over a large tree (<c>**/*.cs</c> in a big repository) would hold up the start of every run, and what it
    /// protects is a list a person cannot read. Past it the rest are not protected, and the run says so.
    /// </summary>
    public const int MaxFiles = 2000;

    /// <summary>
    /// The files the enabled checks' <see cref="CheckDefinition.Protects"/> cover now, with their hashes, ordered by path.
    /// Walks what a workspace is (WorkspaceGuard.SkippedFolders are not: build output, dependencies and the engine's own
    /// state are nobody's tests), and never into a link. Empty when no check protects anything.
    /// </summary>
    /// <param name="note">What was not protected and why - past <see cref="MaxFiles"/> - or null.</param>
    public static IReadOnlyList<ProtectedFile> Take(string root, CheckSnapshot? snapshot, out string? note)
    {
        note = null;
        var globs = (snapshot?.Checks ?? [])
            .Where(c => c.Definition.Enabled && c.Definition.Protects is { Count: > 0 })
            .SelectMany(c => c.Definition.Protects!.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => (c.Id, Glob: g)))
            .ToArray();
        if (globs.Length == 0) return [];

        var found = new List<ProtectedFile>();
        var more = 0;
        foreach (var relative in Walk(root, Starts(globs.Select(g => g.Glob))))
        {
            var ids = globs.Where(g => CheckGlob.IsMatch(g.Glob, relative)).Select(g => g.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length == 0) continue;
            if (found.Count >= MaxFiles)
            {
                more++;
                continue;
            }
            if (HashOf(Path.Combine(root, relative)) is { } hash)
                found.Add(new ProtectedFile(relative, hash, ids));
        }
        if (more > 0)
            note = $"The checks protect more files than a run hashes: the first {MaxFiles} were taken, and {more} more are not "
                   + "protected in this run - a step may remove them unnoticed. Narrow the protects globs of "
                   + string.Join(", ", globs.Select(g => $"'{g.Id}'").Distinct(StringComparer.OrdinalIgnoreCase)) + ".";
        return found.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Of <paramref name="files"/> hashed for an earlier attempt of a task, the ones a check in <paramref name="snapshot"/>
    /// still protects - each with only those checks. A quick action carried on after a question is composed again, with
    /// the checks as they are now: a file protected only by a check since deleted, disabled or stripped of its protects
    /// would otherwise still send its step back, in the name of a check that no longer says so.
    /// </summary>
    public static IReadOnlyList<ProtectedFile> StillProtected(IReadOnlyList<ProtectedFile> files, CheckSnapshot? snapshot)
    {
        var protecting = (snapshot?.Checks ?? [])
            .Where(c => c.Definition.Enabled && c.Definition.Protects is { Count: > 0 })
            .Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return files.Select(f => f with { CheckIds = f.CheckIds.Where(protecting.Contains).ToArray() })
            .Where(f => f.CheckIds.Count > 0)
            .ToArray();
    }

    /// <summary>A file's SHA-256 now, lower-case hex - null when it cannot be read (or is not there).</summary>
    public static string? HashOf(string fullPath)
    {
        try
        {
            using var stream = File.OpenRead(fullPath);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Where to start walking: the folders the globs name before their first wildcard - <c>tests/**</c> walks <c>tests</c>,
    /// not the whole workspace. A glob with no folder (<c>*.spec.js</c>) matches a name anywhere, and walks everything.
    /// </summary>
    private static IReadOnlyList<string> Starts(IEnumerable<string> globs)
    {
        var starts = new List<string>();
        foreach (var raw in globs)
        {
            var glob = raw.Replace('\\', '/').TrimStart('/');
            if (!glob.Contains('/')) return [""];
            var literal = new List<string>();
            foreach (var segment in glob.Split('/')[..^1])
            {
                if (segment.IndexOfAny(['*', '?', '[', '{']) >= 0) break;
                literal.Add(segment);
            }
            var start = string.Join('/', literal);
            if (start.Length == 0) return [""];
            starts.Add(start);
        }
        // A start below another is walked with it.
        return starts.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => !starts.Any(o => !string.Equals(o, s, StringComparison.OrdinalIgnoreCase)
                                         && s.StartsWith(o + "/", StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Every file under the starts, workspace-relative with '/', in a stable order; skipped folders and links left out.</summary>
    private static IEnumerable<string> Walk(string root, IReadOnlyList<string> starts)
    {
        var skipped = new HashSet<string>(WorkspaceGuard.SkippedFolders, StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
        };
        var pending = new Stack<string>();
        foreach (var start in starts.Reverse())
        {
            if (start.Split('/').Any(skipped.Contains)) continue;
            var full = start.Length == 0 ? root : Path.Combine(root, start);
            if (Directory.Exists(full) && (start.Length == 0 || new DirectoryInfo(full).LinkTarget is null)) pending.Push(full);
        }
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            string[] files, folders;
            try
            {
                files = Directory.GetFiles(folder, "*", options);
                folders = Directory.GetDirectories(folder, "*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files.Order(StringComparer.Ordinal))
                yield return Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var sub in folders.Where(f => !skipped.Contains(Path.GetFileName(f))).OrderDescending(StringComparer.Ordinal))
                pending.Push(sub);
        }
    }
}
