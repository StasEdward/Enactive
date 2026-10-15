namespace Enactive.Application.Projections;

using Enactive.Core.Events;
using Enactive.Core.History;

/// <summary>Which model's pill is shown over the run: none yet, the coder working, or the reasoner reviewing.</summary>
internal enum AgentKind { None, Coder, Reasoner }

/// <summary>
/// Who is working on a run and what it is doing right now: the agent pill, the line under the title, the count of tool
/// calls - folded from the run's events.
///
/// <para>The window worked these out inside its event handler, between writes to its own panels, where no other host
/// could reach them; the browser would have needed a second copy of which event means the reviewer has the run. The
/// cards, title, phase and step count are <see cref="RunFeed"/>'s, and are not repeated here.</para>
/// </summary>
internal sealed class RunActivity
{
    [Flags]
    public enum Change { None = 0, Agent = 1, Action = 2, ToolCalls = 4, Ended = 8 }

    public AgentKind Agent { get; private set; }

    /// <summary>What the pill says.</summary>
    public string AgentLabel => Agent switch
    {
        AgentKind.Coder => "Coder",
        AgentKind.Reasoner => "Reasoner · review",
        _ => string.Empty
    };

    /// <summary>False once the run has ended: nobody is working on it any more.</summary>
    public bool AgentVisible { get; private set; }

    /// <summary>The line saying what is happening now - and, once the run ends, why it ended.</summary>
    public string CurrentAction { get; private set; } = string.Empty;

    public int ToolCalls { get; private set; }

    /// <summary>Folds one event in, and says what it changed - so a host redraws only that.</summary>
    public Change Apply(WorkEvent ev)
    {
        var changed = Change.None;
        switch (ev.Kind)
        {
            case EventKind.StepStarted:
                changed |= Working(AgentKind.Coder) | Doing(ev.Summary);
                break;
            case EventKind.AssistantDelta:
                changed |= Working(AgentKind.Coder);
                break;
            case EventKind.ToolInvoked:
                changed |= Working(AgentKind.Coder);
                ToolCalls++;
                changed |= Change.ToolCalls | Doing(ev.Summary);
                break;
            case EventKind.ReviewRequested:
            case EventKind.ReviewPassed:
            case EventKind.ReviewFailed:
                changed |= Working(AgentKind.Reasoner) | Doing(ev.Summary);
                break;
            case EventKind.DecisionRequested:
            case EventKind.DecisionResolved:
                changed |= Doing(ev.Summary);
                break;
            case EventKind.TaskCompleted:
            case EventKind.TaskFailed:
                var outcome = ev.Outcome()
                              ?? (ev.Kind == EventKind.TaskCompleted ? RunOutcomeKind.Completed : RunOutcomeKind.Failed);
                AgentVisible = false;
                // Why it stopped belongs on screen, not only in the log. A completed run has a reason only when
                // something must be added to "done" - a check the engine ran on the workspace and saw fail
                // (RunOutcomeDecision.Settle) - and that is shown too.
                CurrentAction = outcome == RunOutcomeKind.Completed
                    ? ev.OutcomeReason() ?? string.Empty
                    : ev.OutcomeReason() ?? ev.Summary;
                changed |= Change.Ended | Change.Action;
                break;
        }
        return changed;
    }

    /// <summary>Only a change of hands is a change: a pill redrawn for every token is a pill that flickers.</summary>
    private Change Working(AgentKind agent)
    {
        if (Agent == agent)
            return Change.None;
        Agent = agent;
        AgentVisible = true;
        return Change.Agent;
    }

    private Change Doing(string action)
    {
        CurrentAction = action;
        return Change.Action;
    }
}
