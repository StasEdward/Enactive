namespace AIClient.Agents;

using AIClient.Core.Context;
using AIClient.Core.Inbox;
using AIClient.Core.Permissions;

/// <summary>
/// Decision handler for headless/background runs, where no human is watching to answer a fork. It
/// records the decision to the Inbox (the "notification") and DECLINES the action — a background run
/// must never perform an unapproved step unattended. The user sees it in the Inbox and can re-run the
/// task interactively to approve. (Auto-approve could be a per-workspace setting later.)
/// </summary>
public sealed class BackgroundDecisionHandler : IDecisionHandler
{
    private readonly IInboxStore _inbox;
    private readonly WorkspaceInfo _workspace;

    public BackgroundDecisionHandler(IInboxStore inbox, WorkspaceInfo workspace)
    {
        _inbox = inbox;
        _workspace = workspace;
    }

    public async Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
    {
        await _inbox.AppendAsync(
            new InboxItem(
                Guid.NewGuid(), _workspace.Id, "decision", request.Topic,
                "A background task needed your approval and was declined automatically. "
                    + "Re-run it interactively to approve. Details: " + request.Detail,
                Guid.Empty, "unread", DateTimeOffset.UtcNow),
            CancellationToken.None).ConfigureAwait(false);

        // Choose an explicit "deny" option if offered, else the last option, else a literal "deny".
        var deny = request.Options.FirstOrDefault(o => o.Id.Contains("deny", StringComparison.OrdinalIgnoreCase))?.Id
                   ?? request.Options.LastOrDefault()?.Id
                   ?? "deny";
        return new DecisionOutcome(deny);
    }
}
