namespace Enactive.Workspace;

using System.Text.Json;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Storage;

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
    private readonly string _root;

    public JsonRunStore(WorkspaceInfo workspace)
    {
        _root = Path.GetFullPath(workspace.RootPath);
        _directory = Path.Combine(_root, ".enactive", "runs");
    }

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
    ///
    /// <para>What the run's checks left behind goes with it, first (CheckRecords.RemoveRun): a folder that cannot be removed
    /// leaves the run listed, to be deleted again, rather than gone with its records left where nothing lists them.</para>
    /// </summary>
    public Task DeleteAsync(Guid runId, CancellationToken ct)
    {
        CheckRecords.RemoveRun(_root, runId);
        // Its "Run checks now" records go with it: they are about this run, and records no run lists are read by nobody.
        var checks = ChecksFileOf(runId);
        if (File.Exists(checks))
            File.Delete(checks);
        if (Directory.Exists(_directory))
            foreach (var file in Directory.EnumerateFiles(_directory, $"*_{runId:N}.json"))
                try { File.Delete(file); }
                catch (IOException) { throw; }
                catch (UnauthorizedAccessException) { throw; }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The headers. One file per run, so the events cannot be left unfetched the way they can on
    /// SQL: the whole file is still read off the disk and its events array is still parsed as JSON
    /// tokens. What this saves is the BINDING - thousands of <see cref="RunEventRecord"/> objects
    /// per run, each with its payload string, are never constructed, and the tokens are discarded
    /// as they are skipped. So the reading cost is unchanged and the memory cost is not; on SQL
    /// both fall, which is the difference between the stores and not a reason to pretend otherwise.
    /// </summary>
    public async Task<IReadOnlyList<RunSummary>> LoadSummariesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_directory))
            return Array.Empty<RunSummary>();

        var summaries = new List<RunSummary>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var summary = await JsonSerializer.DeserializeAsync<RunSummary>(stream, Options, ct);
                if (summary is not null)
                    summaries.Add(summary);
            }
            catch
            {
                // skip unreadable / corrupt files
            }
        }

        summaries.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
        return summaries;
    }

    /// <summary>One run, found by the id in its FILE NAME - nothing else is opened.</summary>
    public async Task<RunRecord?> LoadAsync(Guid runId, CancellationToken ct)
    {
        if (!Directory.Exists(_directory))
            return null;

        foreach (var file in Directory.EnumerateFiles(_directory, $"*_{runId:N}.json"))
        {
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                return await JsonSerializer.DeserializeAsync<RunRecord>(stream, Options, ct);
            }
            catch
            {
                return null;
            }
        }

        return null;
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

    /// <summary>
    /// Where a run's "Run checks now" records are kept: one file a run, in a folder of their own below the runs - never
    /// beside them, where every listing (<c>*.json</c>, top folder only) would read the file as a run.
    /// </summary>
    private string ChecksFileOf(Guid runId) => Path.Combine(_directory, "checks", $"{runId:N}.json");

    /// <summary>
    /// Adds a record to the run's file, read and written again under the file's lock: two presses at once - two pages of
    /// the service - must both be kept, not the second written over the first.
    /// </summary>
    public async Task AddCheckRecordAsync(CheckRecord record, CancellationToken ct)
    {
        var file = ChecksFileOf(record.RunId);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var hold = await FileLock.TakeAsync(file, ct);
        var records = (await ReadChecksAsync(file, ct)).ToList();
        records.Add(record);
        AtomicWrite.Replace(file, JsonSerializer.Serialize(records, Options));
    }

    public async Task<IReadOnlyList<CheckRecord>> LoadCheckRecordsAsync(Guid runId, CancellationToken ct)
        => await ReadChecksAsync(ChecksFileOf(runId), ct);

    private static async Task<IReadOnlyList<CheckRecord>> ReadChecksAsync(string file, CancellationToken ct)
    {
        if (!File.Exists(file))
            return [];
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return (await JsonSerializer.DeserializeAsync<List<CheckRecord>>(stream, Options, ct) ?? [])
            .OrderBy(r => r.At).ToArray();
    }
}
