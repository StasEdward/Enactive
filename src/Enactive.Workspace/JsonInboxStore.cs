namespace Enactive.Workspace;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Inbox;

/// <summary>
/// File-based AI Inbox: a JSON array at <c>&lt;workspace&gt;/.enactive/inbox.json</c>. Same shape and
/// guarantees as the memory store. Locking, atomic writes and the refusal to overwrite a file it
/// could not read live in <see cref="JsonFileStore"/> — see the note there for what was wrong.
/// </summary>
public sealed class JsonInboxStore : IInboxStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;

    public JsonInboxStore(WorkspaceInfo workspace)
    {
        var dir = Path.Combine(workspace.RootPath, WorkspaceGuard.ReservedFolder);
        try { Directory.CreateDirectory(dir); } catch { /* best-effort */ }
        _path = Path.Combine(dir, "inbox.json");
    }

    public Task AppendAsync(InboxItem item, CancellationToken ct)
        => MutateAsync(list => { list.Add(item); return list; }, ct);

    public async Task<IReadOnlyList<InboxItem>> LoadAllAsync(CancellationToken ct)
    {
        var gate = JsonFileStore.GateFor(_path);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (items, _) = await JsonFileStore.LoadAsync<InboxItem>(_path, JsonOpts, ct).ConfigureAwait(false);
            return items;
        }
        finally { gate.Release(); }
    }

    public Task MarkReadAsync(Guid id, CancellationToken ct)
        => MutateAsync(list => list.Select(i =>
                i.Id == id && !string.Equals(i.Status, "read", StringComparison.OrdinalIgnoreCase)
                    ? i with { Status = "read" }
                    : i).ToList(),
            ct);

    public Task MarkAllReadAsync(CancellationToken ct)
        => MutateAsync(list => list.Select(i =>
                string.Equals(i.Status, "read", StringComparison.OrdinalIgnoreCase)
                    ? i
                    : i with { Status = "read" }).ToList(),
            ct);

    /// <summary>
    /// Read-modify-write under the file's own lock. The read and the write are one critical section,
    /// or two tasks each append to the list they read and the later write drops the earlier entry.
    /// </summary>
    private async Task MutateAsync(Func<List<InboxItem>, List<InboxItem>> change, CancellationToken ct)
    {
        var gate = JsonFileStore.GateFor(_path);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (items, readable) = await JsonFileStore.LoadAsync<InboxItem>(_path, JsonOpts, ct).ConfigureAwait(false);

            // The file exists but could not be parsed. Writing now would replace whatever is in there
            // with a list built from nothing — so move it aside first and start a fresh one, keeping
            // the damaged content for anyone who wants to look.
            if (!readable)
                JsonFileStore.QuarantineUnreadable(_path);

            await JsonFileStore.SaveAsync(_path, change(items), JsonOpts, ct).ConfigureAwait(false);
        }
        catch { /* best-effort: the inbox is never load-bearing */ }
        finally { gate.Release(); }
    }
}
