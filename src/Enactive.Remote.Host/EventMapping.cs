namespace Enactive.Remote.Host;

using Enactive.Core.Events;
using Enactive.Remote.Contracts;

/// <summary>
/// What of a run reaches the phone.
///
/// <para>Not everything. A run emits a token at a time while a model is speaking, counts usage
/// after every call, and reports each tool's full output; forwarding that would spend a person's
/// battery on a transcript they cannot read on a small screen, and would put every file this run
/// touched through a server that does not need to see it.</para>
///
/// <para>So the selection is an explicit list rather than a filter over what looks noisy, and a
/// test asserts that every <see cref="EventKind"/> is either carried or deliberately not. A kind
/// added to the engine without a decision here would otherwise be silently dropped - which is the
/// same defect as silently forwarding it, just quieter.</para>
/// </summary>
public static class EventMapping
{
    /// <summary>
    /// The kinds worth a line on a phone: the shape of the plan, how far it has got, what it ran,
    /// what it produced, and anything that went wrong.
    /// </summary>
    private static readonly EventKind[] Carried =
    [
        EventKind.PlanCreated,
        EventKind.StepStarted,
        EventKind.StepCompleted,
        EventKind.ToolInvoked,
        EventKind.ArtifactProduced,
        EventKind.ArtifactReverted,
        EventKind.ReviewPassed,
        EventKind.ReviewFailed,
        EventKind.CriterionEvaluated,
        EventKind.ErrorObserved,
        EventKind.ContextTrimmed
    ];

    /// <summary>
    /// Deliberately not carried, each for its own reason.
    ///
    /// <para><see cref="EventKind.AssistantDelta"/> is one token. <see cref="EventKind.ToolResult"/>
    /// is a command's whole output - the run's bulk, and the part most likely to contain something
    /// from the workspace that the gateway has no business holding.
    /// <see cref="EventKind.UsageReported"/>, <see cref="EventKind.Routed"/> and
    /// <see cref="EventKind.ContextAssembled"/> are about how the work was done rather than what
    /// happened. <see cref="EventKind.IntentReceived"/> is the request the owner just typed.
    /// <see cref="EventKind.TaskCompleted"/> and <see cref="EventKind.TaskFailed"/> are not progress
    /// at all - they are endings, and <see cref="Ending"/> handles them.</para>
    ///
    /// <para><see cref="EventKind.DecisionRequested"/> and
    /// <see cref="EventKind.DecisionResolved"/> are absent from both lists on purpose: they become
    /// approval events, not progress, and that is stage 5. Until then they carry no line, which is
    /// visible as a run that goes quiet while it waits - honest, and temporary.</para>
    /// </summary>
    private static readonly EventKind[] NotCarried =
    [
        EventKind.IntentReceived,
        EventKind.ContextAssembled,
        EventKind.Routed,
        EventKind.AssistantDelta,
        EventKind.ToolResult,
        EventKind.UsageReported,
        EventKind.DecisionRequested,
        EventKind.DecisionResolved,
        EventKind.ReviewRequested,
        EventKind.TaskCompleted,
        EventKind.TaskFailed
    ];

    /// <summary>Whether this kind is one the owner sees a line for.</summary>
    public static bool IsCarried(EventKind kind) => Array.IndexOf(Carried, kind) >= 0;

    /// <summary>Every kind this build has an opinion about, for the test that checks none was missed.</summary>
    public static IReadOnlyCollection<EventKind> Classified => [.. Carried, .. NotCarried];

    /// <summary>
    /// A progress line, or nothing.
    ///
    /// <para>The summary is carried as written. It is the sentence the desktop shows, and two
    /// wordings of the same event - one for here, one for there - is how the two views of a run
    /// start disagreeing about what it did.</para>
    /// </summary>
    public static string? Progress(WorkEvent published)
        => IsCarried(published.Kind) ? published.Summary : null;

    /// <summary>
    /// How the run ended, for a terminal engine event.
    ///
    /// <para>Read from the event's OUTCOME, never from which of the two terminal kinds it is. That
    /// distinction is the reason <see cref="RunOutcomeKind"/> exists: a run that a reviewer rejected
    /// used to arrive as TaskCompleted, and every consumer that read the kind called it a
    /// success.</para>
    ///
    /// <para>A terminal event carrying no outcome is not assumed to be one. It reports
    /// <see cref="RemoteEventKind.Incomplete"/> and says so, because an absence is not an answer and
    /// guessing "completed" here is guessing in the one direction that cannot be taken back.</para>
    /// </summary>
    public static (RemoteEventKind Kind, string Detail)? Ending(WorkEvent published)
    {
        if (published.Kind is not (EventKind.TaskCompleted or EventKind.TaskFailed))
        {
            return null;
        }

        var detail = published.OutcomeReason() is { Length: > 0 } reason
            ? $"{published.Summary} - {reason}"
            : published.Summary;

        return published.Outcome() switch
        {
            RunOutcomeKind.Completed => (RemoteEventKind.Completed, detail),
            RunOutcomeKind.Failed => (RemoteEventKind.Failed, detail),
            RunOutcomeKind.Incomplete => (RemoteEventKind.Incomplete, detail),
            RunOutcomeKind.Cancelled => (RemoteEventKind.Cancelled, detail),

            // The engine ended the run and did not say how. Reported as unfinished rather than as
            // either success or failure: both would be inventions, and this way the run stops
            // rather than sitting open for ever.
            _ => (RemoteEventKind.Incomplete,
                  $"{published.Summary} - the run ended without recording how, so it is reported unfinished."),
        };
    }
}
