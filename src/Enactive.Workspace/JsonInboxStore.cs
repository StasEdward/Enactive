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
        // Under the lock as well: a read that overlaps another process's move-into-place sees the
        // file vanish out from under it, and an inbox that occasionally reads as empty is worse
        // than one that waits a few milliseconds.
        await using var hold = await JsonFileStore.HoldAsync(_path, ct).ConfigureAwait(false);

        var (items, _) = await JsonFileStore.LoadAsync<InboxItem>(_path, JsonOpts, ct).ConfigureAwait(false);
        return items;
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
    /// or two writers each append to the list they read and the later write drops the earlier entry.
    ///
    /// <para>"Writers" here means processes as well as tasks. It used to mean only tasks, and the
    /// scheduler made that wrong: measured on 2026-09-10, two processes appending 200 items each
    /// left 200 in the file and reported no error at all. See <see cref="JsonFileStore"/>.</para>
    /// </summary>
    private async Task MutateAsync(Func<List<InboxItem>, List<InboxItem>> change, CancellationToken ct)
    {
        try
        {
            await using var hold = await JsonFileStore.HoldAsync(_path, ct).ConfigureAwait(false);

            var (items, readable) = await JsonFileStore.LoadAsync<InboxItem>(_path, JsonOpts, ct).ConfigureAwait(false);

            // The file exists but could not be parsed. Writing now would replace whatever is in there
            // with a list built from nothing — so move it aside first and start a fresh one, keeping
            // the damaged content for anyone who wants to look. If it could not be moved aside, do
            // NOT write over it: an unpreserved corruption a save then overwrote would be gone with
            // no trace it ever existed - see JsonFileStore.QuarantineUnreadable.
            if (!readable && !JsonFileStore.QuarantineUnreadable(_path))
                return;

            await JsonFileStore.SaveAsync(_path, change(items), JsonOpts, ct).ConfigureAwait(false);
        }
        catch { /* best-effort: the inbox is never load-bearing */ }
    }
}
