namespace Enactive.Workspace;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Memory;

/// <summary>
/// File-based project-memory store: a JSON array at <c>&lt;workspace&gt;/.enactive/memory.json</c>.
/// Zero external dependencies, append-friendly, guarded by a semaphore for the single-user desktop
/// case. Every operation is best-effort — a memory failure must never break a run.
///
/// No longer the default: <see cref="MemoryStoreFactory"/> picks SQLite unless ENACTIVE_STORE says
/// otherwise. This store stays as the zero-dependency option, and as what the SQLite store imports
/// from the first time it opens a workspace that predates it.
/// </summary>
public sealed class JsonMemoryStore : IMemoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonMemoryStore(WorkspaceInfo workspace)
    {
        var dir = Path.Combine(workspace.RootPath, ".enactive");
        try { Directory.CreateDirectory(dir); } catch { /* best-effort */ }
        _path = Path.Combine(dir, "memory.json");
    }

    public async Task AppendAsync(MemoryEntry entry, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = await LoadInternalAsync(ct).ConfigureAwait(false);
            list.Add(entry);
            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, list, JsonOpts, ct).ConfigureAwait(false);
        }
        catch { /* best-effort: memory is never load-bearing */ }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await LoadInternalAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<List<MemoryEntry>> LoadInternalAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_path))
                return new List<MemoryEntry>();
            await using var stream = File.OpenRead(_path);
            var list = await JsonSerializer.DeserializeAsync<List<MemoryEntry>>(stream, JsonOpts, ct).ConfigureAwait(false);
            return list ?? new List<MemoryEntry>();
        }
        catch
        {
            return new List<MemoryEntry>();
        }
    }
}
