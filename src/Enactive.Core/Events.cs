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
    TaskFailed,

    /// <summary>
    /// A long generation is still going: how much of a tool call's arguments, or of the model's
    /// reasoning, has arrived so far. Transient - it updates the step card's activity line, and is
    /// not kept in the run's record. Added at the END of this enum so no stored value moves.
    ///
    /// <para>Measured 2026-09-24, run a2142be6: a single write_file of 6,795 tokens took three
    /// minutes on a local model, and nothing on the step card moved - text streams to the card,
    /// tool-call arguments and reasoning did not. Asked the same afternoon: "the model went off
    /// generating something again". It was writing a 27 KB report.</para>
    /// </summary>
    GenerationProgress,

    /// <summary>
    /// A step's output, accepted by the engine (Phase 2): its values and where they came from, in
    /// the payload. The run's record of it - the steps after it are handed these values.
    /// </summary>
    StepOutputRecorded,

    /// <summary>
    /// The run's definition of done changed after it was fixed (Phase 4): what changed, who changed it
    /// and why, how it compares with what it replaced, and whether it was let through and on whose say.
    /// </summary>
    ContractRevised,

    /// <summary>
    /// The plan grew while it ran (Phase 5.3): a step to be done for each item was given one step per
    /// item, or per batch. The payload is the step's title and the new steps' titles, numbered after
    /// every step the plan already had.
    /// </summary>
    PlanExpanded
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
    Cancelled,

    /// <summary>
    /// Stopped at a question only a person can answer, with nobody there to answer it - and kept,
    /// not refused. The question is written down, the run's last step boundary is kept, and the run
    /// carries on from there once somebody answers. Not an ending: nothing about the work is known
    /// to be wrong or unfinished, only that it cannot go on without somebody.
    /// </summary>
    NeedsUser
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
    Skipped,

    /// <summary>
    /// The work was done; the verdict on it was not obtained. The worker finished, and the reviewer
    /// then failed to return a usable answer - an error, a response the validators refused after
    /// clarification, no verdict at all - or the budget ran out before it could be asked.
    ///
    /// <para>Kept apart from <see cref="Incomplete"/>, which means the WORK did not finish, because
    /// the two were one outcome and that cost real work. Run 4b3b7457, 2026-09-27: a harness written,
    /// twenty tests passing, confirmed by an independent re-run; the reviewer's answer lacked one
    /// field, so the step was Incomplete, the next step was Skipped, and the report said nothing was
    /// verified - with both files on disk. Refusing to call this step verified was right. Concluding
    /// it had not been done, and stopping everything after it, was not.</para>
    ///
    /// <para>So its dependents run: the work they build on exists, and they are reviewed on their
    /// own. The run still cannot be Completed on the strength of it - it counts as Incomplete there,
    /// exactly as before - and nothing here is reverted, because nothing here was rejected.</para>
    ///
    /// <para>Last in the list so no number already written anywhere changes meaning.</para>
    /// </summary>
    DoneUnverified
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

    /// <summary>
    /// The cached share, which is why it is written AFTER "out" in the payload: the pair above is
    /// matched as an adjacent pair, and a field inserted between them would silently stop every
    /// usage event being read at all.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex CachedRegex =
        new("\"cached\"\\s*:\\s*(\\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

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
    /// <param name="cachedPromptTokens">
    /// How many of <paramref name="promptTokens"/> were served from the provider's prompt cache at a
    /// tenth of the price. Null where the provider does not report it, which is a DIFFERENT fact
    /// from zero: zero means the cache was cold or the prompt was too short, null means nobody
    /// knows. A run record that could not tell those apart would report every local model as
    /// getting no benefit from a feature it does not have.
    /// </param>
    public static string UsagePayload(
        int promptTokens, int completionTokens, int? stepNo,
        string? providerId = null, string? model = null, string? purpose = null,
        int? cachedPromptTokens = null, int? cacheCreationPromptTokens = null)
    {
        var parts = new List<string>(8);
        if (cacheCreationPromptTokens is { } created) parts.Add($"\"cacheCreated\":{created}");
        if (stepNo is { } n) parts.Add($"\"step\":{n}");
        parts.Add($"\"in\":{promptTokens}");
        parts.Add($"\"out\":{completionTokens}");
        if (cachedPromptTokens is { } cached) parts.Add($"\"cached\":{cached}");
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

    /// <summary>
    /// Builds the payload of a <see cref="EventKind.ToolInvoked"/> event: WHICH tool, as a value.
    ///
    /// <para>The summary is "<c>name {compacted arguments}</c>", which reads well and cannot be
    /// asked a question. Whether a run reached into the workspace at all
    /// (<see cref="Enactive.Core.Memory.ProjectFacts"/>) is decided by the tools it used, and
    /// deciding it by looking for a tool's name at the front of a sentence would make that wording
    /// load-bearing — the same mistake as parsing "-&gt; model " out of a routing summary, which
    /// this file already carries the scar of.</para>
    /// </summary>
    public static string ToolPayload(string name, int? stepNo = null)
        => "{" + (stepNo is { } n ? $"\"step\":{n}," : "") + "\"tool\":" + Quote(name) + "}";

    /// <summary>
    /// The tool an event names, or null for a record written before this was carried as a value.
    ///
    /// <para>Null means UNKNOWN, never "none" — callers must not read silence as a no.</para>
    /// </summary>
    public static string? ToolName(this WorkEvent ev) => Field(ev.PayloadJson, ToolRegex);

    /// <summary>The same, from a stored event's payload.</summary>
    public static string? ToolNameIn(string? payload) => Field(payload, ToolRegex);

    /// <summary>The complexity a routing event names, or null.</summary>
    public static string? RouteComplexity(this WorkEvent ev) => Field(ev.PayloadJson, ComplexityRegex);

    /// <summary>Minimal JSON string escaping — provider ids and model names are not free text, but a
    /// backslash or quote in one must not produce a payload nothing can parse.</summary>
    /// <summary>
    /// A JSON string literal. Every character JSON requires escaped IS escaped: backslash, quote, and
    /// every control character, a line break among them. Anything else, Cyrillic included, is left as
    /// it is - valid JSON, and readable in a raw log.
    ///
    /// <para>It escaped only the backslash and the quote, so a REASON with a line break in it - "Combined
    /// review response has structural errors:" followed by a list, for one - made the whole payload
    /// invalid. Every reader then failed to parse it and got nothing: the step's outcome read as null,
    /// and the window, falling back to looking for "FAILED:" in the summary, found none in
    /// "INCOMPLETE:" and painted the card green, "Done". Run 4b3b7457 on 2026-09-27 ended its second
    /// step exactly that way.</para>
    /// </summary>
    private static string Quote(string value)
    {
        var quoted = new System.Text.StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': quoted.Append("\\\\"); break;
                case '"': quoted.Append("\\\""); break;
                case '\n': quoted.Append("\\n"); break;
                case '\r': quoted.Append("\\r"); break;
                case '\t': quoted.Append("\\t"); break;
                default:
                    if (c < ' ') quoted.Append("\\u").Append(((int)c).ToString("x4"));
                    else quoted.Append(c);
                    break;
            }
        }
        return quoted.Append('"').ToString();
    }

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
        if (!match.Success) return null;
        // Decoded as the JSON string it is, so this is the exact inverse of Quote - line breaks and
        // \u escapes included. A payload recorded before Quote escaped control characters can hold a
        // raw one, which is not a valid JSON string; that falls back to the decoding those payloads
        // were read with, and loses nothing it used to return.
        var raw = match.Groups[1].Value;
        try { return System.Text.Json.JsonSerializer.Deserialize<string>("\"" + raw + "\""); }
        catch (System.Text.Json.JsonException) { return raw.Replace("\\\"", "\"").Replace("\\\\", "\\"); }
    }

    private static readonly System.Text.RegularExpressions.Regex ProviderRegex =
        new("\"provider\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ModelRegex =
        new("\"model\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex PurposeRegex =
        new("\"purpose\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex RouteRegex =
        new("\"route\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ToolRegex =
        new("\"tool\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

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
    /// Builds the payload of a <see cref="EventKind.StepCompleted"/> event: the step number, how
    /// that step ended, and WHY when it did not simply succeed. The UI used to decide whether a card
    /// goes green by searching the summary for "FAILED:" and "skipped (dependency failed)", so
    /// rewording a message silently turned a red card green. A value cannot be reworded.
    /// </summary>
    /// <param name="reason">
    /// The step's own account of why, under the key <c>reason</c> that
    /// <see cref="OutcomeReasonIn"/> already reads — the same key a terminal event uses, because a
    /// reader asking "why did this end like that" is asking one question.
    ///
    /// <para>It was in the summary and only there. A replayed run therefore showed a failed step as
    /// the bare word "Incomplete", with the reason the orchestrator had computed sitting in a
    /// sentence the card does not parse — so the one card a person opens the run to look at was the
    /// one that would not say what happened.</para>
    /// </param>
    public static string StepPayload(int? stepNo, StepOutcomeKind outcome, string? reason = null)
    {
        var parts = new List<string>(3);
        if (stepNo is { } n) parts.Add($"\"step\":{n}");
        parts.Add($"\"stepOutcome\":\"{outcome}\"");
        if (!string.IsNullOrWhiteSpace(reason)) parts.Add($"\"reason\":{Quote(reason)}");
        return "{" + string.Join(',', parts) + "}";
    }

    /// <summary>
    /// Builds the payload of a <see cref="EventKind.DecisionResolved"/> event: which tool, and
    /// whether the call went through.
    ///
    /// <para>A value, for the reason the whole of this class exists. The event carried only a
    /// sentence — <c>"git: denied"</c>, <c>"git: blocked by policy"</c>, <c>"run_command: not
    /// available to role 'writer'"</c> — three wordings for one fact, and a reader that wanted the
    /// fact had to match on the words. A step card counted them all as remarks because that is what
    /// an unparsed string is.</para>
    /// </summary>
    /// <param name="allowed">
    /// Whether the call proceeded. False covers every way it did not: a person answering Deny, a
    /// policy that refused without asking, a tool the role does not have, and an unattended run
    /// where nobody could have said yes. They differ in WHO decided, which the summary says; they do
    /// not differ in whether the call happened, which is what a count of refusals is about.
    /// </param>
    public static string DecisionPayload(int? stepNo, string tool, bool allowed)
    {
        var parts = new List<string>(3);
        if (stepNo is { } n) parts.Add($"\"step\":{n}");
        parts.Add($"\"tool\":{Quote(tool)}");
        parts.Add($"\"decision\":\"{(allowed ? "allowed" : "refused")}\"");
        return "{" + string.Join(',', parts) + "}";
    }

    /// <summary>
    /// Whether this event reports a call that did NOT go through. Null when the event carries no
    /// decision at all — which is any event that is not a resolved decision, and any resolved
    /// decision recorded before the payload existed.
    /// </summary>
    public static bool? WasRefused(this WorkEvent ev) => WasRefusedIn(ev.PayloadJson);

    /// <summary>The same, from a stored event's payload.</summary>
    public static bool? WasRefusedIn(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("decision", out var value)
                || value.ValueKind != System.Text.Json.JsonValueKind.String)
                return null;

            return value.GetString() switch
            {
                "refused" => true,
                "allowed" => false,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

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
        string name, string outcome, bool required, int? exitCode, string origin = "Declared")
        => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["criterion"] = name,
            ["outcome"] = outcome,
            ["required"] = required,
            ["exitCode"] = exitCode,
            // Who asked for the check. Without it a reader of the values could not tell the engine's
            // own look at the files from a check a person or the planner wrote.
            ["origin"] = origin
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

    /// <summary>A change to the definition of done, as values (Phase 4.1).</summary>
    public static string ContractRevisionPayload(Enactive.Core.Templates.ContractRevision revision)
        => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?> { ["contractRevision"] = revision }, PayloadJson);

    /// <summary>A step output as values: the whole record, so the run store holds what the next step was given.</summary>
    public static string StepOutputPayload(Enactive.Core.Tasks.StepOutput output)
        => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["step"] = output.StepNo,
            ["stepOutput"] = output
        }, PayloadJson);

    public static string OutcomePayload(RunOutcomeKind kind, string? reason = null)
        => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["outcome"] = kind.ToString(),
            ["reason"] = string.IsNullOrWhiteSpace(reason) ? null : reason
        });

    /// <summary>The outcome this event carries, or null when it is not a terminal event.</summary>
    public static RunOutcomeKind? Outcome(this WorkEvent ev) => OutcomeIn(ev.PayloadJson);

    /// <summary>
    /// The same, from a STORED event's payload. A live event and a recorded one carry the same
    /// bytes, and a reader of history is entitled to the same values a reader of a live run gets -
    /// having two implementations of that is how the two drift apart.
    /// </summary>
    public static RunOutcomeKind? OutcomeIn(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
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
    public static string? OutcomeReason(this WorkEvent ev) => OutcomeReasonIn(ev.PayloadJson);

    /// <summary>The same, from a stored event's payload.</summary>
    public static string? OutcomeReasonIn(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
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

    /// <summary>
    /// How much of this event's prompt was served from a cache, or null when it does not say.
    ///
    /// <para>Separate from <see cref="Usage"/> rather than a third member of its tuple. It is a
    /// different kind of fact - a SHARE of the "in" figure, not another total - and every record
    /// written before 2026-09-11 has none, so a caller has to handle its absence either way.</para>
    /// </summary>
    public static int? CachedTokens(this WorkEvent ev)
    {
        if (string.IsNullOrEmpty(ev.PayloadJson))
            return null;
        var m = CachedRegex.Match(ev.PayloadJson);
        return m.Success && int.TryParse(m.Groups[1].Value, out var cached) ? cached : null;
    }

    /// <summary>Input tokens written to cache, already included in Usage().In; null when unreported.</summary>
    public static int? CacheCreationTokens(this WorkEvent ev)
    {
        if (string.IsNullOrEmpty(ev.PayloadJson)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(ev.PayloadJson, "\"cacheCreated\"\\s*:\\s*(\\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var count) ? count : null;
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
