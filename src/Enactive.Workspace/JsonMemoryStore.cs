namespace Enactive.Workspace;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Memory;

/// <summary>
/// File-based project-memory store: a JSON array at <c>&lt;workspace&gt;/.enactive/memory.json</c>.
/// Zero external dependencies and best-effort — a memory failure must never break a run.
///
/// No longer the default: <see cref="MemoryStoreFactory"/> picks SQLite unless ENACTIVE_STORE says
/// otherwise. This store stays as the zero-dependency option, and as what the SQLite store imports
/// from the first time it opens a workspace that predates it.
///
/// Locking, atomic writes and the refusal to overwrite a file it could not read live in
/// <see cref="JsonFileStore"/> — see the note there for what was wrong with doing it per instance.
/// </summary>
public sealed class JsonMemoryStore : IMemoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;

    public JsonMemoryStore(WorkspaceInfo workspace)
    {
        var dir = Path.Combine(workspace.RootPath, WorkspaceGuard.ReservedFolder);
        try { Directory.CreateDirectory(dir); } catch { /* best-effort */ }
        _path = Path.Combine(dir, "memory.json");
    }

    public async Task AppendAsync(MemoryEntry entry, CancellationToken ct)
    {
        var gate = JsonFileStore.GateFor(_path);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (items, readable) = await JsonFileStore.LoadAsync<MemoryEntry>(_path, JsonOpts, ct).ConfigureAwait(false);

            // Unparseable is not empty. Overwriting here would replace the file's real contents with
            // a list built from nothing, so the damaged file is moved aside first and kept.
            if (!readable)
                JsonFileStore.QuarantineUnreadable(_path);

            items.Add(entry);
            await JsonFileStore.SaveAsync(_path, items, JsonOpts, ct).ConfigureAwait(false);
        }
        catch { /* best-effort: memory is never load-bearing */ }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken ct)
    {
        var gate = JsonFileStore.GateFor(_path);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (items, _) = await JsonFileStore.LoadAsync<MemoryEntry>(_path, JsonOpts, ct).ConfigureAwait(false);
            return items;
        }
        finally { gate.Release(); }
    }
}
