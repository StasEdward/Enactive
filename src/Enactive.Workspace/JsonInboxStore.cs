namespace Enactive.Workspace;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Inbox;

/// <summary>
/// File-based AI Inbox: a JSON array at <c>&lt;workspace&gt;/.enactive/inbox.json</c>. Same shape and
/// guarantees as the memory store — semaphore-guarded, best-effort, zero external dependencies.
/// </summary>
public sealed class JsonInboxStore : IInboxStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonInboxStore(WorkspaceInfo workspace)
    {
        var dir = Path.Combine(workspace.RootPath, ".enactive");
        try { Directory.CreateDirectory(dir); } catch { /* best-effort */ }
        _path = Path.Combine(dir, "inbox.json");
    }

    public async Task AppendAsync(InboxItem item, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = await LoadInternalAsync(ct).ConfigureAwait(false);
            list.Add(item);
            await SaveInternalAsync(list, ct).ConfigureAwait(false);
        }
        catch { /* best-effort: the inbox is never load-bearing */ }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<InboxItem>> LoadAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await LoadInternalAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task MarkAllReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = await LoadInternalAsync(ct).ConfigureAwait(false);
            var changed = list.Select(i =>
                string.Equals(i.Status, "read", StringComparison.OrdinalIgnoreCase) ? i : i with { Status = "read" }).ToList();
            await SaveInternalAsync(changed, ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
        finally { _gate.Release(); }
    }

    private async Task<List<InboxItem>> LoadInternalAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_path))
                return new List<InboxItem>();
            await using var stream = File.OpenRead(_path);
            var list = await JsonSerializer.DeserializeAsync<List<InboxItem>>(stream, JsonOpts, ct).ConfigureAwait(false);
            return list ?? new List<InboxItem>();
        }
        catch
        {
            return new List<InboxItem>();
        }
    }

    private async Task SaveInternalAsync(List<InboxItem> list, CancellationToken ct)
    {
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, list, JsonOpts, ct).ConfigureAwait(false);
    }
}
