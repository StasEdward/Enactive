namespace Enactive.Application.Projections;

using Enactive.Core.Events;
using Enactive.Core.Execution;

/// <summary>Where a run's "Pause after this step" stands.</summary>
internal enum PauseStanding
{
    /// <summary>Not offered: the run cannot be paused, will never be (a quick action, a staged run), or has ended.</summary>
    Hidden,

    /// <summary>Shown greyed: the run has no plan yet, so there is no step boundary to pause at - it may get one.</summary>
    NotYet,

    /// <summary>Offered: a planned run that does not stage its changes is going.</summary>
    Offered,

    /// <summary>Asked for: the steps running now finish and the run ends Paused. It cannot be taken back.</summary>
    Pending
}

/// <summary>
/// A run's "Pause after this step" (Plan Phase 4.2) as every host offers it - the window's run header, the service's run
/// page, the console's P key - folded from the run's events, around the <see cref="RunPause"/> the host handed in with
/// the run's request.
///
/// <para><b>Why one rule.</b> The engine takes a pause only where there is a step boundary to stop at and a checkpoint to
/// resume from: a quick action has no steps and runs to its end, and a run that stages its changes cannot be resumed, so
/// it ignores the pause and says so in one line. A control offered there would be a button that does nothing, and each
/// host deciding that for itself is three places to forget one of them.</para>
///
/// <para>Thread-safe enough for its hosts: the service folds events on the run's thread while a page asks from its own,
/// and every flag is a single write read with a volatile read; the request itself is <see cref="RunPause"/>'s.</para>
/// </summary>
internal sealed class PauseOffer(RunPause? pause)
{
    public const string Label = "Pause after this step";
    public const string PendingLabel = "Pausing after the steps running now…";

    private volatile bool _planned, _quick, _staged, _ended;

    /// <summary>The run's pause, which the host handed in with its request; null for a run that cannot be paused.</summary>
    public RunPause? Pause { get; } = pause;

    public PauseStanding Standing
        => Pause is null || _ended || _quick || _staged ? PauseStanding.Hidden
            : Pause.IsRequested ? PauseStanding.Pending
            : _planned ? PauseStanding.Offered
            : PauseStanding.NotYet;

    /// <summary>What the control says: what it does, or that it is under way.</summary>
    public string Text => Standing == PauseStanding.Pending ? PendingLabel : Label;

    /// <summary>The control's hint: what pressing it does, or why it cannot be pressed.</summary>
    public string Why => Standing switch
    {
        PauseStanding.Offered => "Lets the steps running now finish, starts no new one, and ends the run Paused. "
                                 + "Resume it from UNFINISHED to carry on with the steps that are left.",
        PauseStanding.Pending => "The steps running now finish, then the run ends Paused. Stop still cancels it at once.",
        PauseStanding.NotYet => "Offered once the run has a plan: a run pauses between its steps.",
        _ => NotOffered
    };

    /// <summary>Why the run is not offered a pause at all.</summary>
    private string NotOffered
        => Pause is null ? "This run cannot be paused."
            : _ended ? "The run has already ended."
            : _quick ? "A quick action has no steps to pause between; it runs to its end."
            : _staged ? "A run that stages its changes cannot be paused: what it proposes is kept only until it ends."
            : "";

    /// <summary>Folds one event in; true when the control changed, so a host redraws only then.</summary>
    public bool Apply(WorkEvent ev)
    {
        var before = Standing;
        switch (ev.Kind)
        {
            // Only a planned run has step boundaries. A resumed run announces its plan again, so it is offered again.
            case EventKind.PlanCreated:
                _planned = true;
                break;
            // The engine's announcement of a quick action (Orchestrator.RunQuickActionAsync): no plan will follow.
            case EventKind.Routed when ev.QuickActionTitle() is { Length: > 0 }:
                _quick = true;
                break;
            case EventKind.TaskCompleted or EventKind.TaskFailed:
                _ended = true;
                break;
        }
        return Standing != before;
    }

    /// <summary>The run stages its changes - known once it is composed (the host's StagingArtifactStore).</summary>
    public void Staging() => _staged = true;

    /// <summary>The run is over without saying how - cancelled, or broken: no terminal event will come.</summary>
    public void Ended() => _ended = true;

    /// <summary>
    /// Asks the run to pause after the steps running now: null when it is asked (or already was - two people pressing,
    /// or a key pressed twice, is not an error), otherwise why not, in words to show the person who asked.
    /// </summary>
    public string? Request()
    {
        switch (Standing)
        {
            case PauseStanding.Offered:
                Pause!.Request();
                return null;
            case PauseStanding.Pending:
                return null;
            case PauseStanding.NotYet:
                return "The run has no plan yet: it can be paused once it has one, between its steps.";
            default:
                return NotOffered;
        }
    }
}
