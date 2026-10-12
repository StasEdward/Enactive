namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Agents;
using Enactive.Application;
using Enactive.Application.Projections;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Enactive.Server.Accounts;
using Enactive.Workspace;

/// <summary>
/// An inbox item as a page shows it: where it is from, the question it waits on, if any, and the header of the run behind
/// it - null when that run is no longer in the store.
/// </summary>
internal sealed record InboxEntry(string Workspace, InboxItem Item, ParkedDecision? Waiting, RunSummary? Run = null);

/// <summary>
/// The inbox of the service's workspaces (ADR 0002 §9): what runs nobody was watching reported back - finished
/// background runs, and questions a run stopped at because nobody answered in time. A question is answered here by the
/// run's owner or any Operator, and the run carries on under the same task (InboxDecisions, as the window's inbox).
///
/// <para>Only the workspaces the service knows, with the role checked at each call - as for the history. A Viewer reads;
/// what is read and what is not is the service's, the same for everybody, so only an Operator changes it.</para>
/// </summary>
internal sealed class ServerInbox(UserStore users, ServerEngine engine, IRunControl runs)
{
    /// <summary>
    /// Something read, marked read or answered: the unread count is no longer what it was. The rail's badge listens, as the
    /// window's follows its Inbox (UnreadChanged) - it was read again only at a navigation, and Mark all read is none.
    /// Raised on the thread that changed it, for every person's page: what is read is the service's, not one page's.
    /// </summary>
    public event Action? Changed;

    /// <summary>The newest items of every known workspace, newest first, at most <paramref name="newest"/>.</summary>
    public async Task<IReadOnlyList<InboxEntry>> ListAsync(ClaimsPrincipal user, CancellationToken ct, int newest = 200)
    {
        if (ServerAuth.StillValid(users, user) is null)
            return [];

        var all = new List<InboxEntry>();
        foreach (var workspace in engine.Workspaces.Entries)
        {
            IReadOnlyList<InboxItem> items;
            IReadOnlyList<RunSummary> summaries;
            try
            {
                var info = WorkspaceInfo.For(workspace.RootPath);
                items = await InboxStoreFactory.Create(info).LoadAllAsync(ct);
                summaries = await RunStoreFactory.Create(info).LoadSummariesAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                continue;   // a folder whose stores cannot be read now is not a reason to show nobody's inbox
            }

            // The question an item waits on is found by its run's TASK: the answer is to the task, and a run carried
            // on is a new run under the same one (InboxDecisions).
            var decisions = new InboxDecisions(workspace.RootPath, carryOn: null);
            foreach (var item in items)
            {
                var run = summaries.FirstOrDefault(s => s.RunId == item.RunId);
                all.Add(new InboxEntry(workspace.RootPath, item, run is null ? null : decisions.WaitingFor(run.TaskId), run));
            }
        }
        return all.OrderByDescending(e => e.Item.At).Take(newest).ToList();
    }

    /// <summary>
    /// The timeline of the run behind an item, read for that run alone (as the window does when an item is opened); null
    /// when the folder is not one the service knows, or the run is not in its store.
    /// </summary>
    public async Task<IReadOnlyList<RunEventViewModel>?> TimelineAsync(ClaimsPrincipal user, string workspace, Guid runId, CancellationToken ct)
    {
        if (ServerAuth.StillValid(users, user) is null || engine.Workspaces.Find(workspace) is not { } known)
            return null;
        RunRecord? run;
        try { run = await RunStoreFactory.Create(WorkspaceInfo.For(known.RootPath)).LoadAsync(runId, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
        return run is null ? null : RunTimeline.Fold(run);
    }

    /// <summary>Marks one item read, as opening it in the window does. What to tell the person, or null when done.</summary>
    public async Task<string?> MarkReadAsync(ClaimsPrincipal user, string workspace, Guid itemId, CancellationToken ct)
    {
        if (MayChange(user) is { } refused)
            return refused;
        if (engine.Workspaces.Find(workspace) is not { } known)
            return "The service does not know that folder as a workspace.";
        await InboxStoreFactory.Create(WorkspaceInfo.For(known.RootPath)).MarkReadAsync(itemId, ct);
        Changed?.Invoke();
        return null;
    }

    /// <summary>
    /// Marks every item read in every workspace the service knows - the whole inbox the page lists, not one folder's. What
    /// to tell the person, or null when done. A folder whose inbox cannot be written now is passed over, as the list
    /// passes over one it cannot read.
    /// </summary>
    public async Task<string?> MarkAllReadAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (MayChange(user) is { } refused)
            return refused;
        foreach (var workspace in engine.Workspaces.Entries)
        {
            try { await InboxStoreFactory.Create(WorkspaceInfo.For(workspace.RootPath)).MarkAllReadAsync(ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* see above */ }
        }
        Changed?.Invoke();
        return null;
    }

    /// <summary>
    /// Answers the question an item waits on and carries its run on. What to tell the person, and the run carried
    /// on when there is one.
    /// </summary>
    public async Task<(string Said, Guid? Run)> AnswerAsync(ClaimsPrincipal user, string workspace, Guid itemId, string optionId, CancellationToken ct)
    {
        if (ServerAuth.StillValid(users, user) is not { } account)
            return ("Your session has ended. Sign in again.", null);
        if (account.Role < Role.Operator)
            return ("Your role does not include the right to answer a run's question.", null);
        if (engine.Workspaces.Find(workspace) is not { } known)
            return ("The service does not know that folder as a workspace.", null);
        if ((await ListAsync(user, ct)).FirstOrDefault(e => e.Item.Id == itemId && e.Workspace == known.RootPath) is not { Waiting: { } waiting } entry)
            return ("This question is no longer waiting - it was answered already, or its run has ended.", null);
        // Asked before the answer is recorded: once it is, the question is no longer waiting, and a run refused after
        // that (no engine, a folder outside the roots) left nothing in the inbox to answer and nothing carried on.
        if (runs.Refusal(user, known.RootPath) is { } refused)
            return (refused, null);

        Guid? carried = null;
        var inbox = new InboxDecisions(known.RootPath, async (parked, item) =>
        {
            var (run, refusal) = await runs.ContinueAsync(user, known.RootPath, parked, item.Title);
            if (run is null)
                throw new InvalidOperationException(refusal);
            carried = run.Id;
        });
        var said = await inbox.AnswerAsync(waiting, entry.Item, optionId);
        users.Audit(account.Name, "question answered in the inbox", $"{waiting.Topic}: {optionId}");
        await InboxStoreFactory.Create(WorkspaceInfo.For(known.RootPath)).MarkReadAsync(itemId, ct);
        Changed?.Invoke();
        return (said, carried);
    }

    /// <summary>Why this person may not change what is read, or null: an Operator's, as answering is.</summary>
    private string? MayChange(ClaimsPrincipal user)
        => ServerAuth.StillValid(users, user) switch
        {
            null => "Your session has ended. Sign in again.",
            { Role: < Role.Operator } => "Your role does not include the right to mark the inbox read.",
            _ => null
        };
}
