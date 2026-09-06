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
/// How a run ended. The engine had no such type: it only streamed events, so "this failed" had
/// nowhere to live and every consumer inferred a status from prose. A step could throw, a reviewer
/// could reject the work, the model could stop at its token limit — and the run still finished with
/// TaskCompleted, which the UI, the history and the Inbox all read as success.
/// </summary>
public enum RunOutcomeKind
{
    /// <summary>Every required step ran and was accepted.</summary>
    Completed,

    /// <summary>Something went wrong: a step threw, or a reviewer rejected the work.</summary>
    Failed,

    /// <summary>Nothing failed, but the work is not finished — a token limit, an iteration cap, a plan cycle.</summary>
    Incomplete,

    /// <summary>The user stopped it.</summary>
    Cancelled
}

/// <summary>How one unit of work ended. Aggregated into a <see cref="RunOutcomeKind"/>.</summary>
public enum StepOutcomeKind
{
    Succeeded,

    /// <summary>The step threw — a provider error, a tool that could not run.</summary>
    Failed,

    /// <summary>The reviewer rejected every attempt. Dependent steps must not build on it.</summary>
    ReviewRejected,

    /// <summary>Ran, but did not finish: cut off at the token limit, or out of iterations.</summary>
    Incomplete,

    /// <summary>Never ran, because something it depended on did not succeed.</summary>
    Skipped
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

    /// <summary>
    /// Builds the payload of a <see cref="EventKind.StepCompleted"/> event: the step number plus how
    /// that step ended. The UI used to decide whether a card goes green by searching the summary for
    /// "FAILED:" and "skipped (dependency failed)", so rewording a message silently turned a red card
    /// green. A value cannot be reworded.
    /// </summary>
    public static string StepPayload(int? stepNo, StepOutcomeKind outcome)
        => stepNo is { } n
            ? $"{{\"step\":{n},\"stepOutcome\":\"{outcome}\"}}"
            : $"{{\"stepOutcome\":\"{outcome}\"}}";

    /// <summary>How the step this event reports ended, when it says so.</summary>
    public static StepOutcomeKind? StepOutcome(this WorkEvent ev)
    {
        if (string.IsNullOrEmpty(ev.PayloadJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ev.PayloadJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("stepOutcome", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                && Enum.TryParse<StepOutcomeKind>(value.GetString(), out var kind)
                ? kind
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the payload of a terminal event (<see cref="EventKind.TaskCompleted"/> /
    /// <see cref="EventKind.TaskFailed"/>). The KIND is what consumers should read; the summary text
    /// is for display. Status used to be inferred from wording, which is why a failed run could read
    /// as a successful one just by reaching the wrong final event.
    /// </summary>
    public static string OutcomePayload(RunOutcomeKind kind, string? reason = null)
        => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["outcome"] = kind.ToString(),
            ["reason"] = string.IsNullOrWhiteSpace(reason) ? null : reason
        });

    /// <summary>The outcome this event carries, or null when it is not a terminal event.</summary>
    public static RunOutcomeKind? Outcome(this WorkEvent ev)
    {
        if (string.IsNullOrEmpty(ev.PayloadJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ev.PayloadJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("outcome", out var value)
                || value.ValueKind != System.Text.Json.JsonValueKind.String)
                return null;

            return Enum.TryParse<RunOutcomeKind>(value.GetString(), out var kind) ? kind : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Why the run ended the way it did, when the terminal event says so.</summary>
    public static string? OutcomeReason(this WorkEvent ev)
    {
        if (string.IsNullOrEmpty(ev.PayloadJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ev.PayloadJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("reason", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

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
