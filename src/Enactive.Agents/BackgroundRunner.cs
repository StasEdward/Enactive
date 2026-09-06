namespace Enactive.Agents;

using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Inbox;

/// <summary>
/// Drains an already-composed (recorded) run event stream to completion with no UI attached, then
/// files one Inbox item summarizing the outcome (PLAN_v2 §9: a background task runs headless, builds
/// a timeline via the recorder, and reports back through the Inbox). Never throws.
/// </summary>
public static class BackgroundRunner
{
    public static async Task RunAsync(
        IAsyncEnumerable<WorkEvent> recorded, IInboxStore inbox, WorkspaceInfo workspace, string title, CancellationToken ct)
    {
        var status = "Incomplete";
        var artifacts = 0;
        var decisions = 0;
        string? error = null;
        var runId = Guid.Empty;

        try
        {
            await foreach (var ev in recorded.WithCancellation(ct))
            {
                if (ev.RunId != Guid.Empty) runId = ev.RunId;
                switch (ev.Kind)
                {
                    case EventKind.ArtifactProduced: artifacts++; break;
                    case EventKind.DecisionRequested: decisions++; break;
                    case EventKind.ErrorObserved: error = ev.Summary; break;

                    // Read the typed outcome, not the event kind: "Incomplete" and "Failed" are
                    // different things to tell someone who was not watching, and the reason the
                    // engine recorded is more use in an Inbox line than the last error seen.
                    case EventKind.TaskCompleted:
                    case EventKind.TaskFailed:
                        var outcome = ev.Outcome()
                            ?? (ev.Kind == EventKind.TaskCompleted
                                ? RunOutcomeKind.Completed
                                : RunOutcomeKind.Failed);
                        status = outcome.ToString();
                        error = ev.OutcomeReason() ?? error;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            status = "Cancelled";
        }
        catch (Exception ex)
        {
            status = "Failed";
            error = ex.Message;
        }

        // Anything that is not a completed run is worth flagging as such: an Inbox line saying
        // "result" for a run that stopped half-way is the same lie as a green status pill.
        var kind = status is "Failed" or "Incomplete" ? "error"
                 : decisions > 0 ? "decision"
                 : "result";
        var summary = $"{status} · {artifacts} artifact(s)"
            + (decisions > 0 ? $" · {decisions} decision(s) needed your approval" : "")
            + (error is not null ? " · " + error : "");

        await inbox.AppendAsync(
            new InboxItem(Guid.NewGuid(), workspace.Id, kind, title, summary, runId, "unread", DateTimeOffset.UtcNow),
            CancellationToken.None).ConfigureAwait(false);
    }
}
