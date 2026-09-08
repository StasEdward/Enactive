namespace Enactive.Workspace;

using System.Text.Json;
using System.Text.Json.Serialization;
using Enactive.Core.Context;
using Enactive.Core.History;

/// <summary>
/// Interrupted runs, one JSON file per run under &lt;workspace&gt;/.enactive/checkpoints.
///
/// <para>The only implementation of <see cref="IRunCheckpointStore"/>, and there is no factory
/// beside <c>RunStoreFactory</c> choosing between three. That is the design and not a gap: a
/// checkpoint describes a process that died on THIS machine with THIS folder half-changed, and it is
/// worth nothing anywhere else. A workspace whose history lives in MySQL still keeps its checkpoints
/// next to the files they are about.</para>
///
/// <para>Written on every step boundary, so it is on the hot path of a run and deliberately dull:
/// serialize, write to a temporary file, replace. The replace is what makes a checkpoint safe to
/// read - a process killed halfway through writing one leaves the PREVIOUS checkpoint intact rather
/// than a truncated file that parses to a plan with half its steps.</para>
/// </summary>
public sealed class JsonCheckpointStore : IRunCheckpointStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // ChatRole and the tool-call shapes travel by NAME. A checkpoint outlives the build that
        // wrote it only if adding an enum member cannot renumber what is already on disk.
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;

    public JsonCheckpointStore(WorkspaceInfo workspace)
        => _directory = Path.Combine(Path.GetFullPath(workspace.RootPath), ".enactive", "checkpoints");

    /// <summary>
    /// Records where a run has got to, replacing what was there.
    ///
    /// <para>A STAGED run is not recorded, and this is the one place that decides it. Staged changes
    /// live in memory, so a resumed staged run would build later steps on top of earlier ones that
    /// were never written; that is a wrong resume rather than a partial one. Refusing here rather
    /// than at the call site means no host can forget to.</para>
    /// </summary>
    public async Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct)
    {
        if (checkpoint.Staged)
            return;

        Directory.CreateDirectory(_directory);
        var path = PathFor(checkpoint.RunId);
        var temporary = path + ".tmp";

        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, checkpoint, Options, ct);

        File.Move(temporary, path, overwrite: true);
    }

    public async Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_directory))
            return Array.Empty<RunCheckpoint>();

        var found = new List<RunCheckpoint>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var checkpoint = await ReadAsync(file, ct);
            if (checkpoint is not null)
                found.Add(checkpoint);
        }

        found.Sort((a, b) => b.At.CompareTo(a.At));
        return found;
    }

    public async Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct)
    {
        var path = PathFor(runId);
        return File.Exists(path) ? await ReadAsync(path, ct) : null;
    }

    /// <summary>
    /// Forgets a run's checkpoint. A run that has no checkpoint is the goal, so a file that is
    /// already gone is success - two hosts finishing the same run, or a person who deleted it, are
    /// not errors to report.
    /// </summary>
    public Task DeleteAsync(Guid runId, CancellationToken ct)
    {
        try { File.Delete(PathFor(runId)); }
        catch (DirectoryNotFoundException) { /* nothing was ever written here */ }

        return Task.CompletedTask;
    }

    private string PathFor(Guid runId) => Path.Combine(_directory, $"{runId:N}.json");

    /// <summary>
    /// One checkpoint, or null if it cannot be read.
    ///
    /// <para>An unreadable checkpoint is not offered as a resume. The alternative - salvaging what
    /// parses - would hand somebody a plan with an unknown number of its steps missing and call it
    /// their run; a run that cannot be resumed is a disappointment, and one resumed from a plan with
    /// holes in it does damage.</para>
    /// </summary>
    private static async Task<RunCheckpoint?> ReadAsync(string file, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<RunCheckpoint>(stream, Options, ct);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
