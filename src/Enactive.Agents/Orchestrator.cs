namespace Enactive.Agents;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Diagnostics;
using Enactive.Core.Events;
using Enactive.Core.Intents;
using Enactive.Core.Orchestration;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Tasks;
using Enactive.Core.Tools;
using Enactive.Core.Workers;

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
    private readonly int _maxParallelSteps;
    /// <summary>One approval card at a time, however many steps are running.</summary>
    private readonly SemaphoreSlim _decisionGate = new(1, 1);
    private readonly int? _numCtx;
    private readonly bool? _think;
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
        int? numCtx = null,
        bool disableThinking = false,
        int maxParallelSteps = 1)
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
        // 1 = the original behaviour: one step at a time on one shared conversation.
        _maxParallelSteps = Math.Max(1, maxParallelSteps);
        _numCtx = numCtx;
        // Disable the local model's <think> phase by sending think:false; null leaves it to the model.
        _think = disableThinking ? false : null;
    }

    public async IAsyncEnumerable<WorkEvent> SubmitIntentAsync(
        Intent intent, [EnumeratorCancellation] CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        var taskId = intent.Id;

        // This scope covers only the code up to the first yield: an async iterator resumes on its
        // CONSUMER's execution context, so an AsyncLocal set here is gone from the next segment on.
        // The work itself is therefore scoped where it runs - see InScopeAsync and the two pumps.
        using var _logScope = LogScope.Begin(runId, taskId);

        // The step number rides along in PayloadJson so a UI can attribute an event to the right
        // step card even when several steps are running at once. No schema change needed.
        WorkEvent Ev(EventKind kind, string summary, int? stepNo = null)
            => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, kind, summary,
                   stepNo is { } n ? $"{{\"step\":{n}}}" : null);

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
        var plan = await InScopeAsync(runId, taskId, null,
            () => _planner.PlanAsync(intent.RawText, intent.Context, planProvider, planModel, ct));

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(worker.Instructions),
            ChatMessage.User(BuildUserPrompt(intent))
        };
        var artifacts = new List<ArtifactRef>();

        if (plan.Disposition == IntentDisposition.QuickAction)
        {
            yield return Ev(EventKind.Routed, $"Quick action: {plan.Title}");

            // Drained through a channel for the same reason as the DAG path below: the work runs in a
            // task that owns the log scope, while this method only yields what the channel hands it.
            var quick = Channel.CreateUnbounded<WorkEvent>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var quickPump = Task.Run(async () =>
            {
                using var _quickScope = LogScope.Begin(runId, taskId);
                try
                {
                    await foreach (var ev in RunToolLoopAsync(
                        taskId, runId, provider, model.Model, worker, messages, artifacts, intent.Context, null, ct))
                        quick.Writer.TryWrite(ev);
                }
                finally
                {
                    quick.Writer.TryComplete();
                }
            }, ct);

            await foreach (var ev in quick.Reader.ReadAllAsync(ct))
                yield return ev;

            await quickPump;

            yield return Ev(EventKind.TaskCompleted, SummarizeArtifacts(artifacts));
            yield break;
        }

        // ── Task with a DAG plan ──────────────────────────────────────────
        var builtPlan = plan.Plan ?? LinearPlan.FromTitles(new[] { plan.Title });
        var total = builtPlan.Steps.Count;
        yield return Ev(EventKind.PlanCreated,
            $"{plan.Title} — {total} steps: {string.Join(" | ", builtPlan.Steps.Select(x => x.Title))}");

        var scheduler = new DagScheduler(builtPlan);
        // Step numbers are PLAN positions, not a dispatch counter. The UI resolves an event to its
        // step card by this number, and its cards come from the plan in plan order; as soon as
        // readiness order differs from plan order (any real DAG, and every parallel run) a dispatch
        // counter would point at the wrong card. For a linear plan the two are identical, as before.
        var stepNumbers = new Dictionary<Guid, int>();
        for (var i = 0; i < builtPlan.Steps.Count; i++)
            stepNumbers[builtPlan.Steps[i].Id] = i + 1;
        var maxParallel = _maxParallelSteps;

        // Execute by readiness: a step runs only once all its dependencies are Done (a real DAG),
        // not in a fixed linear order. With MaxParallelSteps > 1 the independent branches of the graph
        // run at the same time; every step task writes into one channel so this method stays a single
        // ordered event stream for the caller.
        var events = Channel.CreateUnbounded<WorkEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        // One line per finished step, so a parallel branch knows what its siblings concluded.
        var digest = new List<string>();

        async Task RunStepAsync(PlanStep step)
        {
            var stepNumber = stepNumbers.TryGetValue(step.Id, out var planNo) ? planNo : 0;
            // Every prompt, response and tool call this step makes is stamped with its number, so a
            // parallel run stays readable in one log file.
            using var _stepScope = LogScope.Begin(runId, taskId, stepNumber);
            void Emit(EventKind kind, string summary) => events.Writer.TryWrite(Ev(kind, summary, stepNumber));

            var depNote = step.DependsOn.Count > 0 ? $" (after {step.DependsOn.Count} dep)" : "";
            Emit(EventKind.StepStarted, $"[{stepNumber}/{total}] {step.Title}{depNote}");

            // Degree 1 keeps the one shared conversation, exactly as before - no behaviour change.
            // Above that a step gets its own fork, because two steps cannot append to one message list;
            // it is seeded with the base prompt plus a digest of what earlier steps concluded, rather
            // than replaying their whole tool transcript.
            List<ChatMessage> convo;
            if (maxParallel == 1)
            {
                convo = messages;
            }
            else
            {
                convo = new List<ChatMessage>
                {
                    ChatMessage.System(worker.Instructions),
                    ChatMessage.User(BuildUserPrompt(intent))
                };
                string[] doneSoFar;
                lock (digest)
                    doneSoFar = digest.ToArray();
                if (doneSoFar.Length > 0)
                    convo.Add(ChatMessage.User(
                        "Earlier steps of this plan are already finished and their results are on disk:\n"
                        + string.Join("\n", doneSoFar.Select(d => "- " + d))));
            }

            convo.Add(ChatMessage.User(
                $"Proceed with this step of the plan: {step.Title}\n"
                + "Do only this step. Use tools as needed. When finished, briefly confirm what you did."));

            // Per-step model auto-routing: pick the Execute model for this step's complexity (light for
            // trivial, heavy for complex, the worker's own for normal). Falls back to the base model.
            var stepRef = _router.ResolveExecute(worker, step.Complexity) ?? model;
            var stepProvider = _providers.Create(stepRef.ProviderId);
            var stepModel = stepRef.Model;
            if (stepRef.ProviderId != model.ProviderId || stepRef.Model != model.Model)
                Emit(EventKind.Routed, $"[{stepNumber}] {step.Complexity} step -> {stepRef.ProviderId}/{stepRef.Model}");

            var maxAttempts = reviewOn ? _reviewAttempts + 1 : 1;
            var passed = true;
            var failedHard = false;
            string? failError = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var evidenceStart = convo.Count;

                try
                {
                    await foreach (var ev in RunToolLoopAsync(
                        taskId, runId, stepProvider, stepModel, worker, convo, artifacts,
                        intent.Context, stepNumber, ct))
                        events.Writer.TryWrite(ev);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A throw fails only THIS step (and its dependents), never the whole run.
                    failedHard = true;
                    failError = ex.Message;
                    break;
                }

                if (!reviewOn)
                    break;

                Emit(EventKind.ReviewRequested, $"[{stepNumber}] reviewing with reasoner…");

                ReviewResult review;
                try
                {
                    string[] changed;
                    lock (artifacts)
                        changed = artifacts.Select(a => a.RelativePath).ToArray();
                    var evidence = BuildEvidence(convo, evidenceStart);
                    review = await _reviewer.ReviewAsync(step.Title, LastAssistant(convo), evidence, changed, reviewProvider!, reviewModel, ct);
                }
                catch (Exception ex)
                {
                    review = new ReviewResult(true, "review skipped: " + ex.Message);
                }

                if (review.Pass)
                {
                    Emit(EventKind.ReviewPassed,
                        $"[{stepNumber}] PASS{(string.IsNullOrEmpty(review.Notes) ? "" : ": " + review.Notes)}");
                    passed = true;
                    break;
                }

                passed = false;
                Emit(EventKind.ReviewFailed, $"[{stepNumber}] FAIL: {review.Notes}");

                if (attempt < maxAttempts)
                    convo.Add(ChatMessage.User(
                        $"A reviewer rejected the previous attempt with this feedback: {review.Notes}\n"
                        + "Please fix the issues and redo this step."));
            }

            if (failedHard)
            {
                var skippedSteps = scheduler.MarkFailed(step.Id);
                Emit(EventKind.StepCompleted, $"[{stepNumber}/{total}] {step.Title} — FAILED: {failError}");
                foreach (var sk in skippedSteps)
                {
                    // Stamp the skipped step's own number so the UI marks ITS card, not whichever
                    // card happened to be current.
                    var skNo = stepNumbers.TryGetValue(sk.Id, out var n) ? n : 0;
                    events.Writer.TryWrite(Ev(EventKind.StepCompleted,
                        $"[{skNo}/{total}] {sk.Title} — skipped (dependency failed)",
                        skNo > 0 ? skNo : (int?)null));
                }
            }
            else
            {
                scheduler.MarkDone(step.Id);
                lock (digest)
                    digest.Add($"{step.Title}: {Gist(LastAssistant(convo))}");
                Emit(EventKind.StepCompleted,
                    $"[{stepNumber}/{total}] {step.Title} — {(passed ? "done" : "done (review not passed)")}");
            }
        }

        // Dispatcher: keep up to maxParallel steps in flight, topping up as each one finishes.
        var pump = Task.Run(async () =>
        {
            using var _pumpScope = LogScope.Begin(runId, taskId);
            var inFlight = new List<Task>();
            try
            {
                while (true)
                {
                    foreach (var ready in scheduler.NextReadyBatch(maxParallel - inFlight.Count))
                        inFlight.Add(RunStepAsync(ready));

                    if (inFlight.Count == 0)
                        break;

                    var finished = await Task.WhenAny(inFlight);
                    inFlight.Remove(finished);
                    await finished;   // surfaces cancellation; step failures are handled inside
                }
            }
            finally
            {
                events.Writer.TryComplete();
            }
        }, ct);

        await foreach (var ev in events.Reader.ReadAllAsync(ct))
            yield return ev;

        await pump;


        if (scheduler.HasPending)
            yield return Ev(EventKind.ErrorObserved,
                "Plan has unresolvable dependencies (a cycle) — remaining steps could not run.");

        yield return Ev(EventKind.TaskCompleted, SummarizeArtifacts(artifacts));
    }

    /// <summary>
    /// Runs one awaited operation under the ambient log scope, so what it logs carries the run (and,
    /// where given, the step). Needed because <see cref="SubmitIntentAsync"/> is an async iterator and
    /// a scope opened in it does not survive a yield - see <see cref="LogScope"/>.
    /// </summary>
    private static async Task<T> InScopeAsync<T>(Guid run, Guid task, int? step, Func<Task<T>> body)
    {
        using var _scope = LogScope.Begin(run, task, step);
        return await body();
    }

    /// <summary>
    /// Runs the streaming tool loop over a shared message list until the assistant produces a final
    /// answer (no tool calls). Emits token + tool + artifact events and appends produced artifacts.
    /// </summary>
    private async IAsyncEnumerable<WorkEvent> RunToolLoopAsync(
        Guid taskId, Guid runId, IChatProvider provider, string model, Worker worker,
        List<ChatMessage> messages, List<ArtifactRef> artifacts, WorkContext context,
        int? stepNo,
        [EnumeratorCancellation] CancellationToken ct)
    {
        WorkEvent Ev(EventKind kind, string summary)
            => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, kind, summary,
                   stepNo is { } n ? $"{{\"step\":{n}}}" : null);

        WorkEvent Usage(int prompt, int completion)
            => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, EventKind.UsageReported,
                   $"tokens: {prompt} in, {completion} out",
                   WorkEventPayload.UsagePayload(prompt, completion, stepNo));

        for (var iteration = 1; iteration <= MaxIterations; iteration++)
        {
            var toolDefs = _tools.Definitions.Where(d => Allows(worker, d.Name)).ToArray();
            var request = new ChatRequest(model, messages, toolDefs, Temperature: 0.2, NumCtx: _numCtx, Think: _think);

            var contentBuilder = new StringBuilder();
            var toolBuilders = new Dictionary<int, ToolCallBuilder>();
            string? finishReason = null;

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

                    case FinishDelta finish:
                        finishReason = finish.Reason;
                        break;

                    // What the turn cost. It was dropped here, which is why nothing downstream -
                    // the status tile, the run record - could ever say. Providers report totals per
                    // turn, not increments, so each turn is one event and the run adds them up.
                    case UsageDelta usage:
                        yield return Usage(usage.PromptTokens ?? 0, usage.CompletionTokens ?? 0);
                        break;
                }
            }

            // The turn was cut off at the token limit. Record only the partial text (dropping any
            // half-finished tool call, which would dangle without a tool_result and break the next
            // provider call) and stop this step — otherwise the model re-issues the same truncated call
            // every iteration until MaxIterations, burning the run (seen with a reasoning model whose
            // thinking exhausted max_tokens before the tool arguments were emitted).
            if (finishReason is "max_tokens" or "length")
            {
                if (contentBuilder.Length > 0)
                    messages.Add(new ChatMessage(ChatRole.Assistant, contentBuilder.ToString(), null));
                yield return Ev(EventKind.ErrorObserved,
                    $"Model output was cut off at the token limit (finish={finishReason}); stopping this step. "
                    + "Raise max_tokens, or use a model that doesn't spend the whole budget on reasoning.");
                yield break;
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

                        // Parallel steps must not race to put two cards on screen at once.
                        DecisionOutcome outcome;
                        await _decisionGate.WaitAsync(ct);
                        try { outcome = await _decisions.RequestAsync(decisionRequest, ct); }
                        finally { _decisionGate.Release(); }
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
                    lock (artifacts)
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

    /// <summary>One flat line out of a step's closing message - what a sibling branch needs to know
    /// about it, without dragging the whole transcript along.</summary>
    private static string Gist(string? text, int max = 220)
    {
        var flat = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (flat.Length == 0)
            return "(no output)";
        return flat.Length <= max ? flat : flat[..max] + "…";
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
