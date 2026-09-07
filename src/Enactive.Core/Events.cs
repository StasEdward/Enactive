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

    /// <summary>
    /// Work a step produced was put back, because the step did not survive review. Without this the
    /// gate stopped the report and left the consequence: a rejected document sat in the workspace
    /// under a red status.
    /// </summary>
    ArtifactReverted,

    UsageReported,

    /// <summary>
    /// Old tool traffic was dropped from the conversation to keep it inside the model's context
    /// window. Its own kind because it is neither an error nor nothing: the step continues, but the
    /// model can no longer see what a file said earlier, and a person reading the run afterwards
    /// deserves to know that before wondering why it forgot.
    /// </summary>
    ContextTrimmed,

    /// <summary>
    /// One of the run's success criteria was checked. Its own kind because it is the only evidence
    /// in a run that is not a model's opinion of anything: a command ran and returned a number.
    /// A criterion that could not be checked emits this too - "not checked" is a result, and the
    /// silence it replaces is what let a run finish green over work nobody had verified.
    /// </summary>
    CriterionEvaluated,

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
    /// <param name="providerId">
    /// WHICH provider produced these tokens. Recorded here rather than worked out later because
    /// afterwards it cannot be: a run's totals said nothing about whether the local model did the
    /// work or the expensive one did, which is the only question the light/heavy routing exists to
    /// answer. Whether that provider is local or remote is deliberately NOT decided here — the
    /// engine does not know a provider's address, and the app that does can classify it later.
    /// </param>
    /// <param name="purpose">
    /// WHICH phase spent these tokens - see <see cref="WorkPurpose"/>. Planning and review call the
    /// provider outside the tool loop, so their tokens used to be spent and never counted: the run
    /// total was execute-only, which understates a run whose reviewer reads whole documents on an
    /// expensive model.
    /// </param>
    public static string UsagePayload(
        int promptTokens, int completionTokens, int? stepNo,
        string? providerId = null, string? model = null, string? purpose = null)
    {
        var parts = new List<string>(6);
        if (stepNo is { } n) parts.Add($"\"step\":{n}");
        parts.Add($"\"in\":{promptTokens}");
        parts.Add($"\"out\":{completionTokens}");
        if (!string.IsNullOrWhiteSpace(providerId)) parts.Add($"\"provider\":{Quote(providerId)}");
        if (!string.IsNullOrWhiteSpace(model)) parts.Add($"\"model\":{Quote(model)}");
        if (!string.IsNullOrWhiteSpace(purpose)) parts.Add($"\"purpose\":{Quote(purpose)}");
        return "{" + string.Join(',', parts) + "}";
    }

    /// <summary>
    /// Which phase an event belongs to. Written as a value rather than inferred from the summary,
    /// for the same reason step outcomes are: wording changes, values do not.
    /// </summary>
    public static class WorkPurpose
    {
        /// <summary>The planner's own call - one turn, before any step exists.</summary>
        public const string Plan = "plan";

        /// <summary>A worker turn inside the tool loop.</summary>
        public const string Execute = "execute";

        /// <summary>A reviewer call (including its one re-ask).</summary>
        public const string Review = "review";
    }

    /// <summary>The phase this event belongs to, or null when it does not say.</summary>
    public static string? Purpose(this WorkEvent ev) => Field(ev.PayloadJson, PurposeRegex);

    /// <summary>
    /// Builds the payload of a routing (<see cref="EventKind.Routed"/>) event: which model was
    /// chosen for what.
    ///
    /// <para>The UI used to learn a run's routing by parsing the summary - "Worker 'X' -> model p/m"
    /// - which made the wording load-bearing, and could only ever show the three BINDINGS. What a
    /// person actually wants to know is which model each STEP ran on: a run can bind a local worker
    /// and still send every step to the cloud because the planner rated them complex, and the panel
    /// would happily report the local binding it never used.</para>
    /// </summary>
    /// <param name="purpose">"worker", "plan", "review" or "step".</param>
    /// <param name="complexity">The step's rated complexity - the REASON a step went where it did.</param>
    public static string RoutePayload(
        string purpose, string providerId, string model, int? stepNo = null, string? complexity = null)
    {
        var parts = new List<string>(5);
        if (stepNo is { } n) parts.Add($"\"step\":{n}");
        parts.Add($"\"route\":{Quote(purpose)}");
        parts.Add($"\"provider\":{Quote(providerId)}");
        parts.Add($"\"model\":{Quote(model)}");
        if (!string.IsNullOrWhiteSpace(complexity)) parts.Add($"\"complexity\":{Quote(complexity)}");
        return "{" + string.Join(',', parts) + "}";
    }

    /// <summary>What this routing event decided - "worker", "plan", "review", "step" - or null.</summary>
    public static string? Route(this WorkEvent ev) => Field(ev.PayloadJson, RouteRegex);

    /// <summary>The complexity a routing event names, or null.</summary>
    public static string? RouteComplexity(this WorkEvent ev) => Field(ev.PayloadJson, ComplexityRegex);

    /// <summary>Minimal JSON string escaping — provider ids and model names are not free text, but a
    /// backslash or quote in one must not produce a payload nothing can parse.</summary>
    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// The provider id an event names, or null. Null for a run recorded before this was written
    /// down: unknown is a different fact from "none", and the UI shows it as such.
    /// </summary>
    public static string? ProviderId(this WorkEvent ev)
        => Field(ev.PayloadJson, ProviderRegex);

    /// <summary>The model an event names, or null.</summary>
    public static string? ModelName(this WorkEvent ev)
        => Field(ev.PayloadJson, ModelRegex);

    private static string? Field(string? payload, System.Text.RegularExpressions.Regex regex)
    {
        if (string.IsNullOrEmpty(payload)) return null;
        var match = regex.Match(payload);
        return match.Success ? match.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : null;
    }

    private static readonly System.Text.RegularExpressions.Regex ProviderRegex =
        new("\"provider\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ModelRegex =
        new("\"model\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex PurposeRegex =
        new("\"purpose\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex RouteRegex =
        new("\"route\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ComplexityRegex =
        new("\"complexity\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Builds the payload of a <see cref="EventKind.PlanCreated"/> event: the plan's title and its
    /// step titles, as values.
    ///
    /// <para>These were read out of the summary — "&lt;title&gt; — N steps: a | b" — by everything
    /// that wanted them: the live step cards, the replayed ones, the window header. A step whose
    /// title happens to contain " | " became two steps, a plan title containing " — " lost its tail,
    /// and rewording the sentence would have quietly changed how many cards a run appears to have
    /// had. Serialized properly, so a title is a title whatever is in it.</para>
    /// </summary>
    public static string PlanPayload(string title, IReadOnlyList<string> stepTitles)
        => System.Text.Json.JsonSerializer.Serialize(new PlanPayloadShape(title, stepTitles), PayloadJson);

    /// <summary>
    /// camelCase, because the other payloads in this file are hand-written that way and the step
    /// number is found with a regex over <c>"step"</c>. One spelling, or a serialized payload stops
    /// being readable by the accessors the hand-written ones are read by.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>The plan title this event carries, or null for a run recorded before it did.</summary>
    public static string? PlanTitle(this WorkEvent ev) => Plan(ev)?.Title;

    /// <summary>The step titles this event carries, or null when it carries none.</summary>
    public static IReadOnlyList<string>? PlanSteps(this WorkEvent ev) => Plan(ev)?.Steps;

    private static PlanPayloadShape? Plan(WorkEvent ev)
    {
        if (string.IsNullOrEmpty(ev.PayloadJson))
            return null;

        try
        {
            var shape = System.Text.Json.JsonSerializer.Deserialize<PlanPayloadShape>(ev.PayloadJson, PayloadJson);
            return shape is { Steps: not null } ? shape : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record PlanPayloadShape(string Title, IReadOnlyList<string> Steps);

    /// <summary>
    /// Builds the payload of an <see cref="EventKind.ArtifactProduced"/> event: what was produced and
    /// where. The path used to be recovered by splitting the summary on its first ": ", which is a
    /// guess about a sentence, not a fact about a file.
    /// </summary>
    public static string ArtifactPayload(string kind, string relativePath, int? stepNo = null)
        => System.Text.Json.JsonSerializer.Serialize(
            new ArtifactPayloadShape(kind, relativePath, stepNo), PayloadJson);

    /// <summary>The path this artifact event names, or null when it names none.</summary>
    public static string? ArtifactPath(this WorkEvent ev) => Artifact(ev)?.Path;

    /// <summary>The artifact kind this event names, or null.</summary>
    public static string? ArtifactKindName(this WorkEvent ev) => Artifact(ev)?.Kind;

    private static ArtifactPayloadShape? Artifact(WorkEvent ev)
    {
        if (string.IsNullOrEmpty(ev.PayloadJson))
            return null;

        try
        {
            var shape = System.Text.Json.JsonSerializer.Deserialize<ArtifactPayloadShape>(ev.PayloadJson, PayloadJson);
            return shape is { Path.Length: > 0 } ? shape : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The step rides along, because an artifact belongs to the step that produced it and
    /// that is how every consumer attributes one.</summary>
    private sealed record ArtifactPayloadShape(string Kind, string Path, int? Step);

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
    /// <summary>
    /// What was asked for, as a VALUE rather than as the prefix of a sentence.
    ///
    /// <para>The summary is "Intent: &lt;text&gt;", and taking the text back out of it would be the
    /// habit these payloads exist to end - besides breaking on the first request that begins with
    /// something looking like a prefix. Retrying a run means asking for the same thing again, so the
    /// same thing has to be recoverable exactly.</para>
    /// </summary>
    public static string RequestPayload(string rawText)
        => System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string?> { ["request"] = rawText }, PayloadJson);

    /// <summary>The request an IntentReceived payload carries, or null when it carries none.</summary>
    public static string? RequestTextIn(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("request", out var value)
                   && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One success criterion's result, as VALUES. The panel that will show a run's checks reads
    /// these; nothing has to take the sentence apart.
    /// </summary>
    public static string CriterionPayload(
        string name, string outcome, bool required, int? exitCode)
        => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["criterion"] = name,
            ["outcome"] = outcome,
            ["required"] = required,
            ["exitCode"] = exitCode
        }, PayloadJson);

    /// <summary>The criterion outcome this event carries, or null when it carries none.</summary>
    public static string? CriterionOutcomeName(this WorkEvent ev)
        => StringField(ev, "outcome");

    /// <summary>The criterion's name, when the event is a criterion result.</summary>
    public static string? CriterionName(this WorkEvent ev)
        => StringField(ev, "criterion");

    private static string? StringField(WorkEvent ev, string field)
    {
        if (string.IsNullOrEmpty(ev.PayloadJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ev.PayloadJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(field, out var value)
                   && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

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
