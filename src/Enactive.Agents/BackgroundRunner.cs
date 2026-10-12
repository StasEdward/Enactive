namespace Enactive.Agents;

using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Inbox;

/// <summary>
/// Drains an already-composed (recorded) run event stream to completion with no UI attached, then
/// files one Inbox item summarizing the outcome (a background task runs headless, builds
/// a timeline via the recorder, and reports back through the Inbox). Never throws.
/// </summary>
public static class BackgroundRunner
{
    public static async Task RunAsync(
        IAsyncEnumerable<WorkEvent> recorded, IInboxStore inbox, WorkspaceInfo workspace, string title, CancellationToken ct,
        // The schedule this run is for, when one started it: the schedules window lists a schedule's outcomes by it.
        Guid? scheduleId = null)
    {
        var tally = new BackgroundTally();

        try
        {
            await foreach (var ev in recorded.WithCancellation(ct))
                tally.Apply(ev);
        }
        catch (OperationCanceledException)
        {
            tally.Cancelled();
        }
        catch (Exception ex)
        {
            tally.Failed(ex.Message);
        }

        await inbox.AppendAsync(tally.Item(workspace, title, scheduleId), CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>
/// What a run nobody watched leaves in the Inbox, counted as its events go by: how it ended, what it produced, what went
/// against it.
///
/// <para>Out of <see cref="BackgroundRunner"/> so the service can count its own background runs the same way. The service
/// drains a run into its session, for a page to open, and cannot hand the events to the runner as well; counting them
/// again with a second copy of these rules would have made a run the service ran in the background read differently in
/// the Inbox from one the window ran.</para>
/// </summary>
public sealed class BackgroundTally
{
    private string _status = "Incomplete";
    private int _artifacts;
    private int _decisions;
    private string? _error;

    /// <summary>The engine's id for the run, once an event has said it; empty before.</summary>
    public Guid RunId { get; private set; }

    /// <summary>The engine has said how the run ended: a terminal event went by.</summary>
    public bool Ended { get; private set; }

    public void Apply(WorkEvent ev)
    {
        if (ev.RunId != Guid.Empty) RunId = ev.RunId;
        switch (ev.Kind)
        {
            case EventKind.ArtifactProduced: _artifacts++; break;
            // The decisions that went against the run: refused, because nobody could say
            // yes. It counted every question asked, which was the same thing while every
            // background question was refused on the spot. Now a question either stops the
            // run (and is filed as the question) or is given the answer recorded for it,
            // and a run that was answered and completed has nothing waiting on anybody.
            case EventKind.DecisionResolved when ev.WasRefused() == true: _decisions++; break;
            case EventKind.ErrorObserved: _error = ev.Summary; break;

            // Read the typed outcome, not the event kind: "Incomplete" and "Failed" are
            // different things to tell someone who was not watching, and the reason the
            // engine recorded is more use in an Inbox line than the last error seen.
            case EventKind.TaskCompleted:
            case EventKind.TaskFailed:
                var outcome = ev.Outcome()
                    ?? (ev.Kind == EventKind.TaskCompleted
                        ? RunOutcomeKind.Completed
                        : RunOutcomeKind.Failed);
                _status = outcome.ToString();
                _error = ev.OutcomeReason() ?? _error;
                Ended = true;
                break;
        }
    }

    /// <summary>The run was stopped before its events ended.</summary>
    public void Cancelled() => _status = "Cancelled";

    /// <summary>The run's events ended in an error the engine threw.</summary>
    public void Failed(string message)
    {
        _status = "Failed";
        _error = message;
    }

    /// <summary>The run's one Inbox item, under <paramref name="title"/>.</summary>
    public InboxItem Item(WorkspaceInfo workspace, string title, Guid? scheduleId = null)
    {
        // The wording and the kind come from InboxLines, which the scheduled path uses too. A run
        // that ends unwatched should read the same way whether the app was running or not.
        var line = InboxLines.For(_status, _artifacts, _decisions, _error);
        return new InboxItem(Guid.NewGuid(), workspace.Id, line.Kind, title, line.Summary, RunId, "unread", DateTimeOffset.UtcNow, scheduleId);
    }
}
