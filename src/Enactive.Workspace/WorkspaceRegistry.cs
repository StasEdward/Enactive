namespace Enactive.Workspace;

using System.Text.Json;

/// <summary>One folder the user has worked in. Whether it still EXISTS is not stored: a drive that
/// is not mounted this morning is not a workspace the user threw away.</summary>
public sealed record WorkspaceEntry(string Name, string RootPath, DateTimeOffset LastOpenedAt);

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

    private WorkspaceRegistry() { }

    /// <summary>Most recently opened first - which makes the first entry the one to restore.</summary>
    public IReadOnlyList<WorkspaceEntry> Entries => _entries;

    public WorkspaceEntry? LastOpened => _entries.Count > 0 ? _entries[0] : null;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Enactive", "workspaces.json");

    /// <summary>The old plain-text recents file. Read once, if the registry does not exist yet.</summary>
    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Enactive", "workspaces.txt");

    public static WorkspaceRegistry Load()
    {
        var registry = new WorkspaceRegistry();

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var entries = JsonSerializer.Deserialize<List<WorkspaceEntry>>(json, Options);
                if (entries is not null)
                    registry._entries.AddRange(entries.Where(e => !string.IsNullOrWhiteSpace(e.RootPath)));
            }
            else
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
        var full = Normalise(rootPath);
        var existing = Find(full);
        var entry = new WorkspaceEntry(existing?.Name ?? NameFor(full), full, DateTimeOffset.Now);

        if (existing is not null)
            _entries.Remove(existing);
        _entries.Insert(0, entry);

        Save();
        return entry;
    }

    /// <summary>Forgets a workspace. The folder and everything in it - including its .enactive
    /// history - is untouched: this list is the app's, the folder is the user's.</summary>
    public void Remove(string rootPath)
    {
        var existing = Find(Normalise(rootPath));
        if (existing is null)
            return;
        _entries.Remove(existing);
        Save();
    }

    public WorkspaceEntry? Find(string rootPath)
    {
        var full = Normalise(rootPath);
        return _entries.FirstOrDefault(e => PathsEqual(e.RootPath, full));
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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries, Options));
        }
        catch
        {
            // Best effort. The session still works with the list it has in memory.
        }
    }
}
