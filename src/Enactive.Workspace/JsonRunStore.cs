namespace Enactive.Workspace;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.History;

/// <summary>
/// File-backed run store: one JSON file per run under &lt;workspace&gt;/.enactive/runs. No external
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
        => _directory = Path.Combine(Path.GetFullPath(workspace.RootPath), ".enactive", "runs");

    public async Task SaveAsync(RunRecord record, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var name = $"{record.StartedAt.ToLocalTime():yyyyMMdd_HHmmss}_{record.RunId:N}.json";
        var path = Path.Combine(_directory, name);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, record, Options, ct);
    }

    /// <summary>
    /// Deletes the file this run lives in. The id is in the NAME, so nothing has to be opened to
    /// find it - which also means a file too corrupt to read can still be removed, and that is
    /// exactly the one somebody most wants gone.
    /// </summary>
    public Task DeleteAsync(Guid runId, CancellationToken ct)
    {
        if (Directory.Exists(_directory))
            foreach (var file in Directory.EnumerateFiles(_directory, $"*_{runId:N}.json"))
                try { File.Delete(file); }
                catch (IOException) { throw; }
                catch (UnauthorizedAccessException) { throw; }

        return Task.CompletedTask;
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
