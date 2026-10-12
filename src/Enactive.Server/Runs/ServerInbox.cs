namespace Enactive.Server.Runs;

using System.Security.Claims;
using Enactive.Agents;
using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Server.Accounts;
using Enactive.Workspace;

/// <summary>An inbox item as a page shows it: where it is from, and the question it waits on, if any.</summary>
internal sealed record InboxEntry(string Workspace, InboxItem Item, ParkedDecision? Waiting);

/// <summary>
/// The inbox of the service's workspaces (ADR 0002 §9): what runs nobody was watching reported back - finished
/// background runs, and questions a run stopped at because nobody answered in time. A question is answered here by the
/// run's owner or any Operator, and the run carries on under the same task (InboxDecisions, as the window's inbox).
///
/// <para>Only the workspaces the service knows, with the role checked at each call - as for the history.</para>
/// </summary>
internal sealed class ServerInbox(UserStore users, ServerEngine engine, IRunControl runs)
{
    /// <summary>The newest items of every known workspace, newest first, at most <paramref name="newest"/>.</summary>
    public async Task<IReadOnlyList<InboxEntry>> ListAsync(ClaimsPrincipal user, CancellationToken ct, int newest = 200)
    {
        if (ServerAuth.StillValid(users, user) is null)
            return [];

        var all = new List<InboxEntry>();
        foreach (var workspace in engine.Workspaces.Entries)
        {
            IReadOnlyList<InboxItem> items;
            IReadOnlyList<Enactive.Core.History.RunSummary> summaries;
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
                var task = summaries.FirstOrDefault(s => s.RunId == item.RunId)?.TaskId;
                all.Add(new InboxEntry(workspace.RootPath, item, task is { } id ? decisions.WaitingFor(id) : null));
            }
        }
        return all.OrderByDescending(e => e.Item.At).Take(newest).ToList();
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
        return (said, carried);
    }
}
