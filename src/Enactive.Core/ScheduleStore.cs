namespace Enactive.Core.Schedules;

using System.Text.Json;
using System.Text.Json.Serialization;
using Enactive.Core.Context;

/// <summary>
/// The saved schedules, in <c>%APPDATA%/Enactive/schedules.json</c>.
///
/// <para><b>Outside every workspace, and keyed by the workspace PATH.</b> Both halves are
/// <see cref="Enactive.Core.Permissions.ApprovalStore"/>'s reasoning, and they apply here more
/// sharply: a schedule is permission to run commands in a folder, unattended, at a time nobody is
/// watching. Kept inside <c>.enactive/</c> it would be a file the agent can write, so a run could
/// schedule itself more runs. Keyed by the id the folder carries, a repository cloned from anywhere
/// would arrive with somebody else's schedules already in it.</para>
///
/// <para>The cost is the same as for approvals and is the right way round: renaming a folder loses
/// its schedules, which is an inconvenience, and inheriting schedules nobody set here is not.</para>
/// </summary>
public sealed class ScheduleStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // By name, like ResolvedTaskSpec's snapshot and for the same reason: a file read back in two
        // years must not depend on nobody having inserted a value into the middle of an enum.
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly string _file;

    /// <param name="filePath">
    /// A parameter so a test can point it somewhere temporary. The rules are the thing worth
    /// testing, and a test that wrote into the developer's real %APPDATA% is one nobody runs twice.
    /// </param>
    public ScheduleStore(string filePath) => _file = filePath;

    public static ScheduleStore Default { get; } = new(DefaultPath());

    public static string DefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Enactive", "schedules.json");

    /// <summary>Every schedule for this workspace, in the order they were added.</summary>
    public IReadOnlyList<Schedule> For(string workspaceRoot)
        => string.IsNullOrWhiteSpace(workspaceRoot)
            ? Array.Empty<Schedule>()
            : Load().TryGetValue(KeyFor(workspaceRoot), out var found)
                ? found
                : Array.Empty<Schedule>();

    /// <summary>Adds a schedule, or replaces the one with the same id.</summary>
    public void Save(Schedule schedule)
    {
        if (!schedule.Work.IsValid || string.IsNullOrWhiteSpace(schedule.WorkspaceRoot))
            return;

        lock (_gate)
        {
            var all = Load();
            var key = KeyFor(schedule.WorkspaceRoot);
            var list = all.TryGetValue(key, out var found) ? new List<Schedule>(found) : new List<Schedule>();

            var at = list.FindIndex(s => s.Id == schedule.Id);
            if (at >= 0) list[at] = schedule; else list.Add(schedule);

            all[key] = list;
            Write(all);
        }
    }

    /// <summary>Forgets one. Silent when it is not there: deleting twice is not an error.</summary>
    public void Remove(string workspaceRoot, Guid id)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return;

        lock (_gate)
        {
            var all = Load();
            var key = KeyFor(workspaceRoot);
            if (!all.TryGetValue(key, out var found))
                return;

            var list = found.Where(s => s.Id != id).ToList();
            if (list.Count == found.Count)
                return;

            all[key] = list;
            Write(all);
        }
    }

    private static string KeyFor(string workspaceRoot)
        => WorkspaceInfo.IdFor(workspaceRoot).ToString("N");

    private Dictionary<string, List<Schedule>> Load()
    {
        try
        {
            if (!File.Exists(_file))
                return new Dictionary<string, List<Schedule>>();

            return JsonSerializer.Deserialize<Dictionary<string, List<Schedule>>>(File.ReadAllText(_file), Json)
                ?? new Dictionary<string, List<Schedule>>();
        }
        catch
        {
            // An unreadable file means NO schedules. The other direction - guessing at what a
            // corrupt file meant - would run something nobody asked for at a time nobody chose.
            return new Dictionary<string, List<Schedule>>();
        }
    }

    private void Write(Dictionary<string, List<Schedule>> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);

            // Beside the target and moved into place, like every other state file here: an
            // interrupted save must not leave half a file that reads as "no schedules".
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));
            File.Move(temp, _file, overwrite: true);
        }
        catch { /* a lost edit is a nuisance; taking the app down over it is not */ }
    }
}
