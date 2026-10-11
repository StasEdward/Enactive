namespace Enactive.Application;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Enactive.Settings;
using Enactive.Workspace;

/// <summary>
/// A run nobody is watching, from composing it to its line in the workspace's Inbox - whichever host starts it: the
/// window's "run in background", a question answered in the Inbox, and next the service's scheduler.
///
/// <para>The window held this inside the lambda it handed its background group, beside its own rows and redraws; the
/// service would have needed it again, and the two copies would have disagreed about what a run that never started
/// leaves behind. It leaves an Inbox item, always: a background run is one nobody is watching, so the Inbox is the
/// only place its end - even an end before it began - can be read.</para>
/// </summary>
internal static class BackgroundRun
{
    /// <summary>Runs the request to its end. Never throws: what went wrong becomes the Inbox item.</summary>
    /// <param name="title">What the Inbox item is called: the request as it was asked.</param>
    public static async Task RunAsync(ComposedEngine engine, RunRequest request, string title, CancellationToken ct)
    {
        var workspace = request.Workspace;
        var inbox = InboxStoreFactory.Create(workspace);
        try
        {
            // A question nobody is here for is kept, not refused: the run stops at it, the Inbox shows it where it can
            // be answered, and answering carries the run on (ContinueFrom). It used to be answered "no" on the spot,
            // and the only way to get "yes" in was to start the whole task again, in the foreground.
            await using var composed = await RunComposer.ComposeAsync(engine, request, new ParkingDecisionHandler(), ct);
            await BackgroundRunner.RunAsync(composed.Events(ct), inbox, workspace, title, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Composition can be cancelled before the recorder ever receives a run id.
            var line = InboxLines.For("Cancelled", 0, 0, null);
            await inbox.AppendAsync(new InboxItem(Guid.NewGuid(), workspace.Id, line.Kind, title,
                line.Summary, Guid.Empty, "unread", DateTimeOffset.UtcNow), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A folder another process is running in (WorkspaceLease), a model that is not there, a tool server that
            // would not start: said where the person will look for this run.
            await inbox.AppendAsync(new InboxItem(Guid.NewGuid(), workspace.Id, "error", title,
                "Failed to start: " + ex.Message, Guid.Empty, "unread", DateTimeOffset.UtcNow), CancellationToken.None);
        }
    }

    /// <summary>
    /// Where a run that stopped at a question, now answered, carries on from: its last step boundary when it has one,
    /// otherwise null - the same request is started again under the same task. Either way the step that asked is done
    /// again and, asking the same thing, is given the answer.
    /// </summary>
    /// <param name="warn">Told when the checkpoints could not be read; the run then starts again rather than not at all.</param>
    public static async Task<RunCheckpoint?> ContinueFromAsync(ParkedDecision parked, string root, Action<string> warn, CancellationToken ct)
    {
        try
        {
            return await ParkedRuns.CheckpointForAsync(new JsonCheckpointStore(WorkspaceInfo.For(root)), parked.TaskId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            warn("Could not read the checkpoints to carry a run on: " + ex.Message);
            return null;
        }
    }
}
