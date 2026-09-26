namespace Enactive.Workspace;

using System.Text.Json;

/// <summary>
/// One folder the user has worked in, and how a run behaves in it. Whether it still EXISTS is not
/// stored: a drive that is not mounted this morning is not a workspace the user threw away.
///
/// <para>Autonomy, worker and staging live HERE rather than in the app settings because they are
/// properties of the place, not of the person: a scratch folder wants Autonomous, and the repo you
/// ship from does not - and nobody remembers to move the slider back. The worker is stored by id,
/// not by its position in a list that changes when the roles are edited.</para>
///
/// <para>Every field past the path is optional, so a registry written before it existed still
/// loads. The defaults are the cautious ones.</para>
/// </summary>
public sealed record WorkspaceEntry(
    string Name,
    string RootPath,
    DateTimeOffset LastOpenedAt,
    int Autonomy = 2,
    string? WorkerId = null,
    bool StageChanges = false);

/// <summary>
/// The list of workspaces the app knows about, in %APPDATA%/Enactive/workspaces.json.
///
/// A flat list on purpose. A tree would have to claim these folders are related, and they are not -
/// they are unrelated projects that happen to live on the same disk. What makes one recognisable is
/// its NAME, which is why the entry carries one instead of leaving the UI to show a truncated path.
/// </summary>
public sealed class WorkspaceRegistry
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly List<WorkspaceEntry> _entries = new();
    private readonly object _gate = new();

    private readonly CoalescingWriter<WorkspaceEntry[]>? _writer;

    private readonly string _filePath;

    private WorkspaceRegistry(string filePath, bool deferWrites = false)
    {
        _filePath = filePath;
        if (deferWrites) _writer = new CoalescingWriter<WorkspaceEntry[]>(SaveSnapshot, TimeSpan.FromMilliseconds(250));
    }

    public Task FlushAsync() => _writer?.FlushAsync() ?? Task.CompletedTask;

    /// <summary>Most recently opened first - which makes the first entry the one to restore.</summary>
    public IReadOnlyList<WorkspaceEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }

    public WorkspaceEntry? LastOpened { get { lock (_gate) return _entries.Count > 0 ? _entries[0] : null; } }

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Enactive", "workspaces.json");

    /// <summary>The old plain-text recents file. Read once, if the registry does not exist yet.</summary>
    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Enactive", "workspaces.txt");

    public static WorkspaceRegistry Load(bool deferWrites = false, string? filePath = null)
    {
        var registry = new WorkspaceRegistry(filePath ?? FilePath, deferWrites);

        try
        {
            if (File.Exists(registry._filePath))
            {
                var json = File.ReadAllText(registry._filePath);
                var entries = JsonSerializer.Deserialize<List<WorkspaceEntry>>(json, Options);
                if (entries is not null)
                    registry._entries.AddRange(entries.Where(e => !string.IsNullOrWhiteSpace(e.RootPath)));
            }
            else if (filePath is null)
            {
                registry.ImportLegacy();
            }
        }
        catch
        {
            // A registry that cannot be read is an empty one. Losing the list is annoying; refusing
            // to start over it would be worse.
        }

        registry.Sort();
        return registry;
    }

    /// <summary>
    /// Records that a workspace was opened: adds it if new, moves it to the front either way. The
    /// name is kept if the entry already had one, so a rename survives.
    /// </summary>
    public WorkspaceEntry Touch(string rootPath)
    {
        lock (_gate)
        {
            var full = Normalise(rootPath);
            var existing = Find(full);

            // Everything the entry already knew survives being reopened - the name it was given and how
            // a run behaves in it. Only the timestamp is news.
            var entry = existing is null
                ? new WorkspaceEntry(NameFor(full), full, DateTimeOffset.Now)
                : existing with { LastOpenedAt = DateTimeOffset.Now };

            if (existing is not null)
                _entries.Remove(existing);
            _entries.Insert(0, entry);

            Save();
            return entry;
        }
    }

    /// <summary>
    /// Gives a workspace a name of its own. The folder is not touched - this is what the app calls
    /// it, so two folders both named "src" can be told apart without reading their paths.
    /// </summary>
    public void Rename(string rootPath, string name)
    {
        lock (_gate)
        {
            var existing = Find(rootPath);
            if (existing is null)
                return;

            var trimmed = name.Trim();
            if (trimmed.Length == 0)
                trimmed = NameFor(existing.RootPath);

            Replace(existing, existing with { Name = trimmed });
        }
    }

    /// <summary>
    /// Records how a run should behave in this workspace. Creates the entry if the workspace is not
    /// listed yet: changing a setting for a folder is intent enough to remember the folder.
    /// </summary>
    public void SaveSettings(string rootPath, int autonomy, string? workerId, bool stageChanges)
    {
        lock (_gate)
        {
            var existing = Find(rootPath) ?? Touch(rootPath);
            Replace(existing, existing with
            {
                Autonomy = autonomy,
                WorkerId = workerId,
                StageChanges = stageChanges
            });
        }
    }

    /// <summary>Forgets a workspace. The folder and everything in it - including its .enactive
    /// history - is untouched: this list is the app's, the folder is the user's.</summary>
    public void Remove(string rootPath)
    {
        lock (_gate)
        {
            var existing = Find(Normalise(rootPath));
            if (existing is null)
                return;
            _entries.Remove(existing);
            Save();
        }
    }

    public WorkspaceEntry? Find(string rootPath)
    {
        lock (_gate)
        {
            var full = Normalise(rootPath);
            return _entries.FirstOrDefault(e => PathsEqual(e.RootPath, full));
        }
    }

    /// <summary>The folder's own name - "Enactive" out of c:\...\repos\StasEdward\Enactive.</summary>
    public static string NameFor(string rootPath)
    {
        var full = Normalise(rootPath);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
        // A drive root has no file name of its own; "C:\" is a better label than nothing.
        return string.IsNullOrEmpty(name) ? full : name;
    }

    public static string Normalise(string rootPath)
    {
        try { return Path.GetFullPath(rootPath.Trim()); }
        catch { return rootPath.Trim(); }
    }

    private void Replace(WorkspaceEntry old, WorkspaceEntry updated)
    {
        var at = _entries.IndexOf(old);
        if (at < 0)
            return;
        _entries[at] = updated;
        Save();
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private void ImportLegacy()
    {
        if (!File.Exists(LegacyFilePath))
            return;

        // The old file was paths, newest first, and nothing else - so the timestamps are invented
        // in that order purely to preserve it.
        var at = DateTimeOffset.Now;
        foreach (var line in File.ReadAllLines(LegacyFilePath))
        {
            var path = line.Trim();
            if (path.Length == 0 || Find(path) is not null)
                continue;
            _entries.Add(new WorkspaceEntry(NameFor(path), Normalise(path), at));
            at = at.AddSeconds(-1);
        }

        if (_entries.Count > 0)
            Save();
    }

    private void Sort()
    {
        var ordered = _entries.OrderByDescending(e => e.LastOpenedAt).ToList();
        _entries.Clear();
        _entries.AddRange(ordered);
    }

    private void Save()
    {
        var snapshot = _entries.ToArray();
        if (_writer is not null) _writer.Queue(snapshot);
        else SaveSnapshot(snapshot);
    }

    private void SaveSnapshot(WorkspaceEntry[] entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            AtomicWrite.Replace(_filePath, JsonSerializer.Serialize(entries, Options));
        }
        catch
        {
            // Best effort. The session still works with the list it has in memory.
        }
    }
}
