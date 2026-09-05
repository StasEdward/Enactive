namespace Enactive.Core.Events;

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
    UsageReported,
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

/// <summary>
/// Reading the extras the orchestrator puts in <see cref="WorkEvent.PayloadJson"/>. The step number
/// rides there as {"step":3} rather than in a typed field, so adding it needed no schema change; this
/// is the single place that knows that, for every consumer (log tap, UI step cards).
/// </summary>
public static class WorkEventPayload
{
    private static readonly System.Text.RegularExpressions.Regex StepNoRegex =
        new("\"step\"\\s*:\\s*(\\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex UsageRegex =
        new("\"in\"\\s*:\\s*(\\d+)\\s*,\\s*\"out\"\\s*:\\s*(\\d+)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The plan step this event belongs to, or null when it carries no step number.</summary>
    public static int? StepNo(this WorkEvent ev)
    {
        if (string.IsNullOrEmpty(ev.PayloadJson))
            return null;
        var m = StepNoRegex.Match(ev.PayloadJson);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    /// <summary>
    /// Builds the payload of a <see cref="EventKind.UsageReported"/> event. Here rather than in the
    /// orchestrator so the shape is written once and read once.
    /// </summary>
    public static string UsagePayload(int promptTokens, int completionTokens, int? stepNo)
        => stepNo is { } n
            ? $"{{\"step\":{n},\"in\":{promptTokens},\"out\":{completionTokens}}}"
            : $"{{\"in\":{promptTokens},\"out\":{completionTokens}}}";

    /// <summary>The tokens this event reports, or null when it is not a usage event.</summary>
    public static (int In, int Out)? Usage(this WorkEvent ev)
    {
        if (string.IsNullOrEmpty(ev.PayloadJson))
            return null;
        var m = UsageRegex.Match(ev.PayloadJson);
        return m.Success
               && int.TryParse(m.Groups[1].Value, out var input)
               && int.TryParse(m.Groups[2].Value, out var output)
            ? (input, output)
            : null;
    }
}
