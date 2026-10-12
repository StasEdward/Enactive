namespace Enactive.Server.Runs;

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Server.Accounts;
using Enactive.Workspace;

/// <summary>A staged run's proposals as a list shows them.</summary>
internal sealed record StagedSet(string Workspace, string Id, string Owner, string Prompt, DateTimeOffset Started,
    int Pending, int Total, bool Running);

/// <summary>One proposal: a file's next content, and the difference from what it builds on - null when not text.</summary>
internal sealed record StagedFile(Guid Id, string Path, bool IsNew, bool Applied, bool Rejected, long Bytes, string? Diff);

/// <summary>
/// Persistent staging (server plan A4, phase 3): a run from the browser in a workspace set to stage keeps its proposals
/// under <c>&lt;workspace&gt;/.enactive/staging/&lt;run&gt;</c> - their bytes and the manifest StagingArtifactStore
/// writes, with who started the run beside them - and they are applied or rejected here, by the run's owner or any
/// Operator, each one in the audit trail.
///
/// <para>Before this the service refused staging altogether: in its memory, the proposals were lost with a closed tab or
/// a restart while the history said the changes were staged. The rules for applying - an earlier change to a file
/// first, the file as it was when proposed, later proposals rejected before the ones they build on - are the store's
/// own (StagingArtifactStore.Reopen), not a copy here.</para>
/// </summary>
internal sealed class ServerStaging(UserStore users, ServerEngine engine)
{
    private const string HeaderName = "run.json";

    private sealed record Header(string Owner, string Prompt, DateTimeOffset Started);

    /// <summary>Folders a run of this process is still writing: not to be applied from under it.</summary>
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One action at a time on one run's proposals: two pages applying at once must not both pass the checks.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public static string Root(string workspace) => Path.Combine(Path.GetFullPath(workspace), ".enactive", "staging");

    /// <summary>The folder a run's proposals are kept in, named who started it - before the run writes anything.</summary>
    public string Begin(string workspace, Guid run, string owner, string prompt)
    {
        var folder = Path.Combine(Root(workspace), run.ToString("N"));
        Directory.CreateDirectory(folder);
        AtomicWrite.Replace(Path.Combine(folder, HeaderName), JsonSerializer.Serialize(new Header(owner, prompt, DateTimeOffset.Now)));
        _running[folder] = 0;
        return folder;
    }

    /// <summary>
    /// The run is over: nothing proposed, nothing kept; otherwise the inbox says what waits - the person who started it
    /// may have closed the page long ago, and the inbox is where the service's people look (and what is mailed).
    /// </summary>
    public async Task EndAsync(string workspace, string folder, string title, Guid? runId, IArtifactStore? artifacts)
    {
        _running.TryRemove(folder, out _);
        var pending = artifacts is StagingArtifactStore store ? store.Changes.Count(c => c.Pending) : 0;
        if (pending == 0)
        {
            TryDelete(folder);
            return;
        }
        var info = WorkspaceInfo.For(workspace);
        await InboxStoreFactory.Create(info).AppendAsync(new InboxItem(Guid.NewGuid(), info.Id, "result",
            title, $"{pending} proposed change(s) wait to be applied or rejected", runId ?? Guid.Empty,
            "unread", DateTimeOffset.UtcNow), CancellationToken.None);
    }

    /// <summary>Every known workspace's kept proposals, newest first. Anybody signed in reads them, as the history.</summary>
    public IReadOnlyList<StagedSet> List(ClaimsPrincipal user)
    {
        if (ServerAuth.StillValid(users, user) is null)
            return [];
        var sets = new List<StagedSet>();
        foreach (var workspace in engine.Workspaces.Entries)
        {
            var root = Root(workspace.RootPath);
            if (!Directory.Exists(root))
                continue;
            foreach (var folder in Directory.EnumerateDirectories(root))
                // Only what still waits, or is still being written: a folder whose proposals were all decided is
                // left only when it could not be deleted.
                if (Read(workspace.RootPath, Path.GetFileName(folder)) is { } set && (set.Pending > 0 || set.Running))
                    sets.Add(set);
        }
        return sets.OrderByDescending(s => s.Started).ToList();
    }

    /// <summary>One run's proposals, each with its difference; null and why when there is nothing to show.</summary>
    public (StagedSet? Set, IReadOnlyList<StagedFile> Files, string? Problem) Open(ClaimsPrincipal user, string workspace, string id)
    {
        if (ServerAuth.StillValid(users, user) is null)
            return (null, [], "Your session has ended. Sign in again.");
        if (Locate(workspace, id) is not { } found)
            return (null, [], "These proposals are no longer kept - they were all applied or rejected, or the folder is not a workspace the service knows.");
        if (Read(found.Root, id) is not { } set)
            return (null, [], "These proposals cannot be read.");
        try
        {
            using var store = StagingArtifactStore.Reopen(found.Root, found.Folder);
            return (set, store.Changes.Select(Describe).ToList(), null);
        }
        catch (IOException ex)
        {
            return (null, [], "These proposals cannot be read: " + ex.Message);
        }
    }

    /// <summary>Applies or rejects one proposal; what to tell the person.</summary>
    public async Task<string> ActAsync(ClaimsPrincipal user, string workspace, string id, Guid change, bool apply, CancellationToken ct)
    {
        if (ServerAuth.StillValid(users, user) is not { } account)
            return "Your session has ended. Sign in again.";
        // The owner or any Operator - and only Operators start runs, so it is the Operators, the owner among them
        // while their role still includes it.
        if (account.Role < Role.Operator)
            return "Your role does not include applying or rejecting proposed changes.";
        if (Locate(workspace, id) is not { } found)
            return "These proposals are no longer kept.";
        if (_running.ContainsKey(found.Folder))
            return "The run is still going; its proposals can be applied or rejected when it has ended.";

        var gate = _gates.GetOrAdd(found.Folder, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            using var store = StagingArtifactStore.Reopen(found.Root, found.Folder);
            if (store.Changes.FirstOrDefault(c => c.Id == change) is not { } proposal)
                return "That change is not among these proposals.";

            string said;
            if (apply)
            {
                var result = store.Apply(change);
                said = result.Applied ? $"Applied: {proposal.RelativePath}." : $"Not applied - {result.Conflict}";
                if (result.Applied)
                    users.Audit(account.Name, "staged change applied", $"{found.Name}: {proposal.RelativePath}");
            }
            else
            {
                var result = store.Reject(change);
                said = result.Rejected ? $"Rejected: {proposal.RelativePath}." : $"Not rejected - {result.Conflict}";
                if (result.Rejected)
                    users.Audit(account.Name, "staged change rejected", $"{found.Name}: {proposal.RelativePath}");
            }

            // Everything decided: nothing left to keep. Its record is the audit trail and the files themselves.
            if (store.Changes.All(c => !c.Pending))
            {
                store.Dispose();
                TryDelete(found.Folder);
                said += " Every proposal of this run is decided.";
            }
            return said;
        }
        catch (IOException ex)
        {
            return "These proposals cannot be read: " + ex.Message;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Drops the proposals nobody decided within <paramref name="keptFor"/> of their run's start - every known workspace,
    /// never a run still writing - and says so in the workspace's inbox and the audit trail. The files are as they were:
    /// a proposal is only ever a proposal until applied. How many sets were dropped.
    /// </summary>
    public async Task<int> ExpireAsync(DateTimeOffset now, TimeSpan keptFor, CancellationToken ct)
    {
        var dropped = 0;
        foreach (var workspace in engine.Workspaces.Entries)
        {
            var root = Root(workspace.RootPath);
            if (!Directory.Exists(root))
                continue;
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var id = Path.GetFileName(folder);
                if (Read(workspace.RootPath, id) is not { Running: false, Pending: > 0 } set || now - set.Started < keptFor)
                    continue;

                // Under the set's gate: a person applying one of them this moment finishes first, and is not undone.
                var gate = _gates.GetOrAdd(folder, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct);
                try
                {
                    if (Read(workspace.RootPath, id) is not { Running: false, Pending: > 0 } still)
                        continue;
                    TryDelete(folder);
                    if (Directory.Exists(folder))
                        continue;   // tried again next time; said only once it is gone
                    var days = (int)keptFor.TotalDays;
                    users.Audit("(service)", "staged changes dropped",
                        $"{workspace.Name}: {still.Pending} proposal(s) of {still.Owner}'s run \"{still.Prompt}\", not decided in {days} days");
                    var info = WorkspaceInfo.For(workspace.RootPath);
                    await InboxStoreFactory.Create(info).AppendAsync(new InboxItem(Guid.NewGuid(), info.Id, "result",
                        still.Prompt, $"{still.Pending} proposed change(s) were not decided in {days} days and are dropped; the files are as they were",
                        Guid.Empty, "unread", DateTimeOffset.UtcNow), ct);
                    dropped++;
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        return dropped;
    }

    /// <summary>A known workspace's proposal folder by its id - only a run id, so no name walks out of the folder.</summary>
    private (string Root, string Folder, string Name)? Locate(string workspace, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _) || engine.Workspaces.Find(workspace) is not { } known)
            return null;
        var folder = Path.Combine(Root(known.RootPath), id);
        return Directory.Exists(folder) ? (known.RootPath, folder, known.Name) : null;
    }

    private StagedSet? Read(string workspace, string id)
    {
        var folder = Path.Combine(Root(workspace), id);
        try
        {
            var header = JsonSerializer.Deserialize<Header>(File.ReadAllText(Path.Combine(folder, HeaderName)));
            if (header is null)
                return null;
            // A run that has not proposed anything yet has no manifest: running, or ended with nothing kept.
            var running = _running.ContainsKey(folder);
            if (!File.Exists(Path.Combine(folder, "proposals.json")))
                return running ? new StagedSet(workspace, id, header.Owner, header.Prompt, header.Started, 0, 0, true) : null;
            using var store = StagingArtifactStore.Reopen(workspace, folder);
            return new StagedSet(workspace, id, header.Owner, header.Prompt, header.Started,
                store.Changes.Count(c => c.Pending), store.Changes.Count, running);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Text up to this size is shown as a difference; past it, or not text, by its size.</summary>
    private const long DiffLimit = 256 * 1024;

    private static StagedFile Describe(StagedChange c)
    {
        var diff = c.IsBinary || c.NewByteCount > DiffLimit || c.OldByteCount > DiffLimit
            ? null
            : TextDiff.Unified(c.OldContent, c.NewContent, c.RelativePath);
        return new StagedFile(c.Id, c.RelativePath, c.IsNew, c.Applied, c.Rejected, c.NewByteCount, diff);
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left as it is: a folder with no pending proposal is not listed (Read), and the next decision tries again.
        }
    }
}
