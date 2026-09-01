namespace AIClient.Core.Events;

/// <summary>Kinds of events emitted during a run. The Timeline is a projection over these.</summary>
public enum EventKind
{
    IntentReceived,
    ContextAssembled,
    Routed,
    AssistantDelta,
    PlanCreated,
    StepStarted,
    StepCompleted,
    ToolInvoked,
    ToolResult,
    ErrorObserved,
    DecisionRequested,
    DecisionResolved,
    ReviewRequested,
    ReviewPassed,
    ReviewFailed,
    ArtifactProduced,
    TaskCompleted,
    TaskFailed
}

/// <summary>
/// An append-only record of something that happened. Carries RunId so each AgentRun has its
/// own timeline. Key data is typed in payloads (PLAN_v2 §2A.7); PayloadJson is for extras only.
/// </summary>
public sealed record WorkEvent(
    Guid Id,
    Guid TaskId,
    Guid RunId,
    DateTimeOffset At,
    EventKind Kind,
    string Summary,
    string? PayloadJson);
