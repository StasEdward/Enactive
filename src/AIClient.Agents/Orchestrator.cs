namespace AIClient.Agents;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIClient.Core.Artifacts;
using AIClient.Core.Chat;
using AIClient.Core.Context;
using AIClient.Core.Diagnostics;
using AIClient.Core.Events;
using AIClient.Core.Intents;
using AIClient.Core.Orchestration;
using AIClient.Core.Permissions;
using AIClient.Core.Providers;
using AIClient.Core.Tasks;
using AIClient.Core.Tools;
using AIClient.Core.Workers;

/// <summary>
/// MVP #2 orchestrator: Intent -> Understand/Plan -> (QuickAction | Task) -> streaming tool loop per
/// step -> ToolResult -> Artifact -> Event. Permission gating and decisions layer on in MVP #3.
/// </summary>
public sealed class Orchestrator : IOrchestrator
{
    private const int MaxIterations = 12;

    private readonly IChatProviderFactory _providers;
    private readonly IWorkerProvider _workers;
    private readonly IToolRegistry _tools;
    private readonly IArtifactStore _artifacts;
    private readonly WorkspaceInfo _workspace;
    private readonly Planner _planner;
    private readonly IPermissionEngine _permissions;
    private readonly IDecisionHandler _decisions;
    private readonly PermissionPolicy _policy;
    private readonly IServiceProvider _services;
    private readonly IModelRouter _router;
    private readonly int _reviewAttempts;
    private readonly int? _numCtx;
    private readonly Reviewer _reviewer = new();

    public Orchestrator(
        IChatProviderFactory providers,
        IModelResolver modelResolver,
        IWorkerProvider workers,
        IToolRegistry tools,
        IArtifactStore artifacts,
        WorkspaceInfo workspace,
        Planner planner,
        IPermissionEngine permissions,
        IDecisionHandler decisions,
        PermissionPolicy policy,
        IServiceProvider services,
        IModelRouter? router = null,
        int reviewAttempts = 1,
        int? numCtx = null)
    {
        _providers = providers;
        _workers = workers;
        _tools = tools;
        _artifacts = artifacts;
        _workspace = workspace;
        _planner = planner;
        _permissions = permissions;
        _decisions = decisions;
        _policy = policy;
        _services = services;
        _router = router ?? new ModelRouter(modelResolver);
        _reviewAttempts = reviewAttempts;
        _numCtx = numCtx;
    }

    public async IAsyncEnumerable<WorkEvent> SubmitIntentAsync(
        Intent intent, [EnumeratorCancellation] CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        var taskId = intent.Id;

        // Open the ambient correlation scope for the whole run: every nested provider/tool/planner
        // call is then tagged with this run in the global log, without threading ids through them.
        using var _logScope = LogScope.Begin(runId, taskId);

        WorkEvent Ev(EventKind kind, string summary)
            => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, kind, summary, null);

        yield return Ev(EventKind.IntentReceived, $"Intent: {intent.RawText}");
        yield return Ev(EventKind.ContextAssembled,
            $"Workspace '{_workspace.Name}' at {_workspace.RootPath}"
            + (intent.Context.GitBranch is { } branch ? $" (git: {branch})" : "")
            + (intent.Context.Environment is { } envInfo ? $" · {envInfo.OneLine()}" : ""));

        var worker = _workers.Get(intent.WorkerId);
        var model = _router.Resolve(ModelPurpose.Execute, worker) ?? worker.ModelPolicy.Preferred;
        yield return Ev(EventKind.Routed, $"Worker '{worker.Role}' -> model {model.ProviderId}/{model.Model}");

        var provider = _providers.Create(model.ProviderId);

        // Plan phase: the bound Plan model, else the executing model.
        var planRef = _router.Resolve(ModelPurpose.Plan, worker) ?? model;
        var planProvider = _providers.Create(planRef.ProviderId);
        var planModel = planRef.Model;
        if (planRef.ProviderId != model.ProviderId || planRef.Model != model.Model)
            yield return Ev(EventKind.Routed, $"Planner -> {planRef.ProviderId}/{planRef.Model}");

        // Review phase: on iff a Review model is bound.
        var reviewRef = _router.Resolve(ModelPurpose.Review, worker);
        var reviewOn = reviewRef is not null;
        var reviewProvider = reviewOn ? _providers.Create(reviewRef!.ProviderId) : null;
        var reviewModel = reviewRef?.Model ?? "";
        if (reviewOn)
            yield return Ev(EventKind.Routed, $"Reviewer -> {reviewRef!.ProviderId}/{reviewRef.Model}");

        // ── Understand / Plan (reasoner when multi-agent) ─────────────────────
        var plan = await _planner.PlanAsync(intent.RawText, intent.Context, planProvider, planModel, ct);

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(worker.Instructions),
            ChatMessage.User(BuildUserPrompt(intent))
        };
        var artifacts = new List<ArtifactRef>();

        if (plan.Disposition == IntentDisposition.QuickAction)
        {
            yield return Ev(EventKind.Routed, $"Quick action: {plan.Title}");

            await foreach (var ev in RunToolLoopAsync(taskId, runId, provider, model.Model, worker, messages, artifacts, intent.Context, ct))
                yield return ev;

            yield return Ev(EventKind.TaskCompleted, SummarizeArtifacts(artifacts));
            yield break;
        }

        // ── Task with a DAG plan ──────────────────────────────────────────
        var builtPlan = plan.Plan ?? LinearPlan.FromTitles(new[] { plan.Title });
        var total = builtPlan.Steps.Count;
        yield return Ev(EventKind.PlanCreated,
            $"{plan.Title} — {total} steps: {string.Join(" | ", builtPlan.Steps.Select(x => x.Title))}");

        var scheduler = new DagScheduler(builtPlan);
        var stepNumber = 0;

        // Execute by readiness: a step runs only once all its dependencies are Done (a real DAG),
        // not in a fixed linear order.
        while (scheduler.NextReady() is { } step)
        {
            stepNumber++;
            var depNote = step.DependsOn.Count > 0 ? $" (after {step.DependsOn.Count} dep)" : "";
            yield return Ev(EventKind.StepStarted, $"[{stepNumber}/{total}] {step.Title}{depNote}");

            messages.Add(ChatMessage.User(
                $"Proceed with this step of the plan: {step.Title}\n"
                + "Do only this step. Use tools as needed. When finished, briefly confirm what you did."));

            var maxAttempts = reviewOn ? _reviewAttempts + 1 : 1;
            var passed = true;
            var failedHard = false;
            string? failError = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var evidenceStart = messages.Count;

                // Drain the tool loop manually so a thrown exception fails only THIS step (and its
                // dependents) instead of the whole run — the yield stays outside the try/catch.
                var stepEnum = RunToolLoopAsync(taskId, runId, provider, model.Model, worker, messages, artifacts, intent.Context, ct)
                    .GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        WorkEvent current = null!;
                        try
                        {
                            if (!await stepEnum.MoveNextAsync())
                                break;
                            current = stepEnum.Current;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            failedHard = true;
                            failError = ex.Message;
                            break;
                        }
                        yield return current;
                    }
                }
                finally
                {
                    await stepEnum.DisposeAsync();
                }

                if (failedHard)
                    break;

                if (!reviewOn)
                    break;

                yield return Ev(EventKind.ReviewRequested, $"[{stepNumber}] reviewing with reasoner…");

                ReviewResult review;
                try
                {
                    var changed = artifacts.Select(a => a.RelativePath).ToArray();
                    var evidence = BuildEvidence(messages, evidenceStart);
                    review = await _reviewer.ReviewAsync(step.Title, LastAssistant(messages), evidence, changed, reviewProvider!, reviewModel, ct);
                }
                catch (Exception ex)
                {
                    review = new ReviewResult(true, "review skipped: " + ex.Message);
                }

                if (review.Pass)
                {
                    yield return Ev(EventKind.ReviewPassed,
                        $"[{stepNumber}] PASS{(string.IsNullOrEmpty(review.Notes) ? "" : ": " + review.Notes)}");
                    passed = true;
                    break;
                }

                passed = false;
                yield return Ev(EventKind.ReviewFailed, $"[{stepNumber}] FAIL: {review.Notes}");

                if (attempt < maxAttempts)
                    messages.Add(ChatMessage.User(
                        $"A reviewer rejected the previous attempt with this feedback: {review.Notes}\n"
                        + "Please fix the issues and redo this step."));
            }

            if (failedHard)
            {
                var skippedSteps = scheduler.MarkFailed(step.Id);
                yield return Ev(EventKind.StepCompleted, $"[{stepNumber}/{total}] {step.Title} — FAILED: {failError}");
                foreach (var sk in skippedSteps)
                    yield return Ev(EventKind.StepCompleted, $"[-/{total}] {sk.Title} — skipped (dependency failed)");
            }
            else
            {
                scheduler.MarkDone(step.Id);
                yield return Ev(EventKind.StepCompleted,
                    $"[{stepNumber}/{total}] {step.Title} — {(passed ? "done" : "done (review not passed)")}");
            }
        }

        if (scheduler.HasPending)
            yield return Ev(EventKind.ErrorObserved,
                "Plan has unresolvable dependencies (a cycle) — remaining steps could not run.");

        yield return Ev(EventKind.TaskCompleted, SummarizeArtifacts(artifacts));
    }

    /// <summary>
    /// Runs the streaming tool loop over a shared message list until the assistant produces a final
    /// answer (no tool calls). Emits token + tool + artifact events and appends produced artifacts.
    /// </summary>
    private async IAsyncEnumerable<WorkEvent> RunToolLoopAsync(
        Guid taskId, Guid runId, IChatProvider provider, string model, Worker worker,
        List<ChatMessage> messages, List<ArtifactRef> artifacts, WorkContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        WorkEvent Ev(EventKind kind, string summary)
            => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, kind, summary, null);

        for (var iteration = 1; iteration <= MaxIterations; iteration++)
        {
            var toolDefs = _tools.Definitions.Where(d => Allows(worker, d.Name)).ToArray();
            var request = new ChatRequest(model, messages, toolDefs, Temperature: 0.2, NumCtx: _numCtx);

            var contentBuilder = new StringBuilder();
            var toolBuilders = new Dictionary<int, ToolCallBuilder>();

            await foreach (var delta in provider.StreamChatAsync(request, ct))
            {
                switch (delta)
                {
                    case TextDelta text:
                        contentBuilder.Append(text.Text);
                        yield return Ev(EventKind.AssistantDelta, text.Text);
                        break;

                    case ToolCallDelta call:
                        var builder = toolBuilders.TryGetValue(call.Index, out var existing)
                            ? existing
                            : toolBuilders[call.Index] = new ToolCallBuilder();
                        if (call.Id is not null) builder.Id = call.Id;
                        if (call.Name is not null) builder.Name = call.Name;
                        if (call.ArgumentsJson is not null) builder.Arguments.Append(call.ArgumentsJson);
                        break;

                    case FinishDelta:
                    case UsageDelta:
                        break;
                }
            }

            var toolCalls = BuildToolCalls(toolBuilders);
            var recovered = false;
            if (toolCalls is null && contentBuilder.Length > 0)
            {
                var implicitCall = TryRecoverImplicitToolCall(contentBuilder.ToString());
                if (implicitCall is not null)
                {
                    toolCalls = new List<ToolCall> { implicitCall };
                    recovered = true;
                }
            }

            messages.Add(new ChatMessage(
                ChatRole.Assistant,
                contentBuilder.Length > 0 ? contentBuilder.ToString() : null,
                toolCalls));

            if (toolCalls is null)
                yield break; // genuine final answer - no tool calls, nothing recoverable either

            if (recovered)
                yield return Ev(EventKind.ErrorObserved,
                    $"The model described a '{toolCalls[0].Name}' call in plain text instead of "
                    + "actually invoking it - recovered automatically. Verify the result below.");

            foreach (var call in toolCalls)
            {
                // ── Role gate: is this tool available to the worker's role? ──
                if (!Allows(worker, call.Name))
                {
                    yield return Ev(EventKind.DecisionResolved, $"{call.Name}: not available to role '{worker.Role}'");
                    messages.Add(ChatMessage.Tool(call.Id, $"ERROR: tool '{call.Name}' is not available to the {worker.Role} role."));
                    continue;
                }

                // ── Permission gate: allow / ask / deny ──────────────────────
                var gate = _permissions.Evaluate(_policy, call.Name, _tools.RequiredLevelOf(call.Name));
                if (gate != PermissionDecision.Allow)
                {
                    var approved = false;
                    if (gate == PermissionDecision.Ask)
                    {
                        yield return Ev(EventKind.DecisionRequested,
                            $"Approve tool '{call.Name}'? {Compact(call.ArgumentsJson)}");

                        var decisionRequest = new DecisionRequest(
                            taskId,
                            $"Run tool '{call.Name}'?",
                            $"Arguments: {Compact(call.ArgumentsJson)}",
                            new[] { new DecisionOption("allow", "Allow"), new DecisionOption("deny", "Deny") },
                            RecommendedOptionId: "allow",
                            Subject: call.Name);

                        var outcome = await _decisions.RequestAsync(decisionRequest, ct);
                        approved = string.Equals(outcome.OptionId, "allow", StringComparison.OrdinalIgnoreCase);
                        yield return Ev(EventKind.DecisionResolved, $"{call.Name}: {(approved ? "allowed" : "denied")}");
                    }
                    else
                    {
                        yield return Ev(EventKind.DecisionResolved, $"{call.Name}: blocked by policy");
                    }

                    if (!approved)
                    {
                        messages.Add(ChatMessage.Tool(call.Id, "ERROR: the user did not permit this action."));
                        continue;
                    }
                }

                yield return Ev(EventKind.ToolInvoked, $"{call.Name} {Compact(call.ArgumentsJson)}");

                var toolContext = new ToolContext(
                    TaskId: taskId,
                    RunId: runId,
                    WorkspaceId: _workspace.Id,
                    Context: context,
                    PermissionPolicy: PermissionPolicy.PermissiveDefault,
                    WorkspaceRoot: _workspace.RootPath,
                    Artifacts: _artifacts,
                    Services: _services);

                ToolResult result;
                try
                {
                    result = await _tools.InvokeAsync(call, toolContext, ct);
                }
                catch (Exception ex)
                {
                    result = ToolResults.Fail($"{call.Name} threw: {ex.Message}");
                }

                yield return result.Success
                    ? Ev(EventKind.ToolResult, $"{call.Name} -> ok: {result.Output}")
                    : Ev(EventKind.ToolResult, $"{call.Name} -> failed: {result.Error}");

                foreach (var reference in result.Artifacts)
                {
                    artifacts.Add(reference);
                    yield return Ev(EventKind.ArtifactProduced, $"{reference.Kind}: {reference.RelativePath}");
                }

                messages.Add(ChatMessage.Tool(
                    call.Id,
                    result.Success ? (result.Output ?? "OK") : $"ERROR: {result.Error}"));
            }
        }

        yield return Ev(EventKind.ErrorObserved, $"Segment did not converge after {MaxIterations} iterations.");
    }

    /// <summary>
    /// Best-effort recovery for the "narrated instead of called" failure mode: some local
    /// models, especially quantized ones, sometimes print what a tool call WOULD look like
    /// (a fenced ```json block, or a bare {...} block) instead of emitting a real structured
    /// tool call. If that JSON's keys satisfy exactly one registered tool's required
    /// parameters, treat it as if that tool had actually been called. Deliberately
    /// conservative: any ambiguity (no tool matches, or more than one matches equally well)
    /// returns null rather than guessing.
    /// </summary>
    private ToolCall? TryRecoverImplicitToolCall(string text)
    {
        var candidates = new List<string>();
        foreach (Match m in JsonFenceRegex.Matches(text))
            candidates.Add(m.Groups[1].Value);

        // Fallback: the model may not have fenced it at all - try the outermost {...} span too.
        var braceStart = text.IndexOf('{');
        var braceEnd = text.LastIndexOf('}');
        if (braceStart >= 0 && braceEnd > braceStart)
            candidates.Add(text[braceStart..(braceEnd + 1)]);

        foreach (var candidate in candidates)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(candidate); }
            catch { continue; }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;

                var docKeys = doc.RootElement.EnumerateObject()
                    .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

                ToolDefinition? best = null;
                var bestScore = 0;
                var ambiguous = false;

                foreach (var tool in _tools.Definitions)
                {
                    if (!TryReadSchemaKeys(tool.JsonSchema, out var required, out var properties))
                        continue;
                    if (required.Count == 0 || !required.All(docKeys.Contains))
                        continue; // must at least cover everything this tool requires

                    var score = docKeys.Count(properties.Contains);
                    if (score > bestScore) { best = tool; bestScore = score; ambiguous = false; }
                    else if (score == bestScore && best is not null) { ambiguous = true; }
                }

                if (best is not null && !ambiguous)
                    return new ToolCall(Guid.NewGuid().ToString("N"), best.Name, doc.RootElement.GetRawText());
            }
        }

        return null;
    }

    private static readonly Regex JsonFenceRegex =
        new("```(?:json)?\\s*(\\{[\\s\\S]*?\\})\\s*```", RegexOptions.Compiled);

    private static bool TryReadSchemaKeys(string jsonSchema, out HashSet<string> required, out HashSet<string> properties)
    {
        required = new HashSet<string>(StringComparer.Ordinal);
        properties = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(jsonSchema);
            var root = doc.RootElement;
            if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                foreach (var p in props.EnumerateObject())
                    properties.Add(p.Name);
            if (root.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                foreach (var r in req.EnumerateArray())
                    if (r.ValueKind == JsonValueKind.String) required.Add(r.GetString()!);
            return true;
        }
        catch { return false; }
    }

    private static List<ToolCall>? BuildToolCalls(Dictionary<int, ToolCallBuilder> builders)
    {
        if (builders.Count == 0)
            return null;

        return builders
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var b = kv.Value;
                var arguments = NormalizeToolArgs(b.Arguments.Length > 0 ? b.Arguments.ToString() : "{}");
                return new ToolCall(b.Id ?? Guid.NewGuid().ToString("N"), b.Name ?? "", arguments);
            })
            .ToList();
    }

    /// <summary>
    /// Small models sometimes concatenate two JSON objects into one tool call's arguments
    /// (e.g. {"a":1}{"b":2}), which is not valid JSON. Keep only the FIRST value so the call still
    /// runs instead of failing the whole step; trailing junk is dropped.
    /// </summary>
    private static string NormalizeToolArgs(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "{}";
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(raw));
            if (JsonDocument.TryParseValue(ref reader, out var doc))
            {
                using (doc)
                    return doc.RootElement.GetRawText();
            }
        }
        catch
        {
            // fall through — let the tool report the parse error itself
        }
        return raw;
    }

    private static string SummarizeArtifacts(List<ArtifactRef> artifacts)
        => artifacts.Count == 0
            ? "(completed, no files changed)"
            : $"(completed; {artifacts.Count} artifact(s): {string.Join(", ", artifacts.Select(a => a.RelativePath))})";

    private static string LastAssistant(List<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == ChatRole.Assistant && !string.IsNullOrEmpty(messages[i].Content))
                return messages[i].Content!;
        return "(no output)";
    }

    private string BuildUserPrompt(Intent intent)
    {
        var context = intent.Context;
        var sb = new StringBuilder();
        sb.AppendLine("## Context (provided by the application)");
        sb.AppendLine($"Workspace root: {_workspace.RootPath}");
        sb.AppendLine($"Workspace name: {_workspace.Name}");
        if (context.GitBranch is { } branch)
            sb.AppendLine($"Git branch: {branch}");
        if (context.Environment is { } env)
        {
            sb.AppendLine("Environment:");
            foreach (var line in env.Summary().Split('\n'))
                sb.AppendLine("  " + line);
        }
        sb.AppendLine("File paths you pass to tools are RELATIVE to the workspace root.");
        sb.AppendLine();
        sb.AppendLine("## Request (the user's intent)");
        sb.AppendLine(intent.RawText);
        return sb.ToString();
    }

    private static string Compact(string json)
    {
        var flattened = json.Replace('\n', ' ').Replace('\r', ' ');
        return flattened.Length <= 120 ? flattened : flattened[..120] + "…";
    }

    /// <summary>Whether a worker's role is allowed to call the given tool (empty or "*" = all).</summary>
    private static bool Allows(Worker worker, string tool)
        => worker.ToolAllowlist.Count == 0
        || worker.ToolAllowlist.Contains("*")
        || worker.ToolAllowlist.Contains(tool, StringComparer.OrdinalIgnoreCase);

    /// <summary>The real commands and tool outputs added during a step — the reviewer's ground truth.</summary>
    private static string BuildEvidence(List<ChatMessage> messages, int start)
    {
        var sb = new StringBuilder();
        for (var i = Math.Max(0, start); i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 } calls)
                foreach (var call in calls)
                    sb.Append("-> ").Append(call.Name).Append(' ').AppendLine(Compact(call.ArgumentsJson));
            else if (m.Role == ChatRole.Tool && !string.IsNullOrEmpty(m.Content))
                sb.Append("<- ").AppendLine(m.Content);
        }
        var text = sb.ToString().Trim();
        if (text.Length == 0) return "(no tools were run in this step)";
        return text.Length > 3000 ? text[..3000] + "\n… (truncated)" : text;
    }

    /// <summary>Accumulates a streamed tool call across deltas.</summary>
    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
