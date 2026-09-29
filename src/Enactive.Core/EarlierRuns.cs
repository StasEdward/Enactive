namespace Enactive.Core.Context;

using System.Security.Cryptography;
using System.Text.Json;

/// <summary>A file an earlier run wrote: which run, when, for what request, and what the file held when it ended.</summary>
public sealed record EarlierWrite(string Path, Guid RunId, DateTimeOffset At, string Request, string Sha256);

/// <summary>What a run knows, from its start, about what earlier runs left in the workspace.</summary>
public sealed record EarlierRunsView(DateTimeOffset StartedAt, IReadOnlyDictionary<string, EarlierWrite> Written);

/// <summary>
/// What earlier runs left in the workspace, told as such when a run reads it.
///
/// <para><b>Why.</b> Run 341c2f, 2026-09-29: the analysis step read <c>coverage-report.md</c> - written by a run an
/// hour before, about a test project that had since been removed - and handed on its figures, 133 tests and 99.63%, as
/// this run's findings. A later run found no tests at all. What an earlier run wrote is that run's account of what it
/// found then; read as a file of the project it passes for a measurement of now. The shared scratch area is the same
/// thing in another place: one folder for every run (see <see cref="ScratchArea"/>), so a step finds the notes of
/// another task under a name it might have chosen itself.</para>
///
/// <para>The engine knows which it is - it wrote them - and says so where they are read: a file an earlier run wrote
/// (recorded here when that run ended, with what it held), or anything in the scratch area last written before this
/// run began. A file changed since this run began is this run's, and nothing is said.</para>
/// </summary>
public static class EarlierRuns
{
    internal const int MaxEntries = 500;

    public static string FileIn(string workspaceRoot)
        => Path.Combine(Path.GetFullPath(workspaceRoot), WorkspaceGuard.ReservedFolder, "earlier-runs.json");

    private static string Key(string relativePath) => relativePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    /// <summary>What earlier runs wrote, as recorded. Empty when nothing is, or the record cannot be read.</summary>
    public static IReadOnlyDictionary<string, EarlierWrite> Load(string workspaceRoot)
    {
        try
        {
            var file = FileIn(workspaceRoot);
            if (!File.Exists(file)) return new Dictionary<string, EarlierWrite>();
            var entries = JsonSerializer.Deserialize<List<EarlierWrite>>(File.ReadAllText(file)) ?? [];
            return entries.GroupBy(e => Key(e.Path)).ToDictionary(g => g.Key, g => g.OrderBy(e => e.At).Last());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, EarlierWrite>();
        }
    }

    /// <summary>
    /// Records what a run wrote, as it leaves it. Kept per path, the newest writer; files no longer there are dropped.
    /// Housekeeping: a failure here never fails the run.
    /// </summary>
    public static void Record(string workspaceRoot, Guid runId, DateTimeOffset at, string request, IEnumerable<string> relativePaths)
    {
        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            var entries = Load(root).ToDictionary(p => p.Key, p => p.Value);
            var said = request.Length <= 120 ? request : request[..120] + "…";
            foreach (var path in relativePaths.Select(p => p.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (WorkspaceGuard.IsScratchRelative(root, path)) continue;
                var full = Path.Combine(root, path);
                if (!File.Exists(full)) continue;
                entries[Key(path)] = new EarlierWrite(path, runId, at, said, Sha(full));
            }
            var kept = entries.Values.Where(e => File.Exists(Path.Combine(root, e.Path)))
                .OrderByDescending(e => e.At).Take(MaxEntries).ToList();
            var file = FileIn(root);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(kept));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    /// <summary>
    /// What to say about a file this run has just read, or null: nothing is said about the project's own files, or
    /// about anything written since this run began.
    /// </summary>
    public static string? NoteFor(string workspaceRoot, string relativePath, EarlierRunsView view)
    {
        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            var full = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!File.Exists(full)) return null;
            var lastWritten = File.GetLastWriteTimeUtc(full);
            if (lastWritten >= view.StartedAt.UtcDateTime) return null;
            var shown = relativePath.Replace('\\', '/');
            if (view.Written.TryGetValue(Key(relativePath), out var earlier) && earlier.At < view.StartedAt)
            {
                var same = Sha(full) == earlier.Sha256;
                return $"[Engine: '{shown}' was written by an earlier Enactive run ({earlier.At.ToLocalTime():yyyy-MM-dd HH:mm}, "
                    + $"request: \"{earlier.Request}\"), not by this run"
                    + (same ? ", and has not changed since" : ", and has been changed since")
                    + ". What it says is what that run found or wrote then - not a measurement of the workspace now. "
                    + "Establish again what this task depends on before relying on its figures.]";
            }
            if (WorkspaceGuard.IsScratchRelative(root, relativePath))
                return $"[Engine: '{shown}' was left in the shared scratch area before this run began "
                    + $"(last written {lastWritten.ToLocalTime():yyyy-MM-dd HH:mm}) - it is not this run's work, and may be "
                    + "another task's.]";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Sha(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
