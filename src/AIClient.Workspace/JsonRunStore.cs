namespace AIClient.Workspace;

using System.Text.Json;
using AIClient.Core.Context;
using AIClient.Core.History;

/// <summary>
/// File-backed run store: one JSON file per run under &lt;workspace&gt;/.aiclient/runs. No external
/// packages; EF Core + SQLite/MySQL is the planned upgrade behind the same interface.
/// </summary>
public sealed class JsonRunStore : IRunStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;

    public JsonRunStore(WorkspaceInfo workspace)
        => _directory = Path.Combine(Path.GetFullPath(workspace.RootPath), ".aiclient", "runs");

    public async Task SaveAsync(RunRecord record, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var name = $"{record.StartedAt.ToLocalTime():yyyyMMdd_HHmmss}_{record.RunId:N}.json";
        var path = Path.Combine(_directory, name);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, record, Options, ct);
    }

    public async Task<IReadOnlyList<RunRecord>> LoadAllAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_directory))
            return Array.Empty<RunRecord>();

        var records = new List<RunRecord>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var record = await JsonSerializer.DeserializeAsync<RunRecord>(stream, Options, ct);
                if (record is not null)
                    records.Add(record);
            }
            catch
            {
                // skip unreadable / corrupt files
            }
        }

        records.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
        return records;
    }
}
