namespace AIClient.Agents;

using System.Runtime.CompilerServices;
using System.Text;
using AIClient.Core.Artifacts;
using AIClient.Core.Chat;
using AIClient.Core.Context;
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
    private const int MaxIterations = 6;

    private readonly IChatProviderFactory _providers;
    private readonly IModelResolver _modelResolver;
    private readonly IWorkerProvider _workers;
    private readonly IToolRegistry _tools;
    private readonly IArtifactStore _artifacts;
    private readonly WorkspaceInfo _workspace;
    private readonly Planner _planner;
    private readonly IPermissionEngine _permissions;
    private readonly IDecisionHandler _decisions;
    private readonly PermissionPolicy _policy;
    private readonly IServiceProvider _services;
    private readonly IChatProvider? _reasoner;
    private readonly string? _reasonerModel;
    private readonly int _reviewAttempts;
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
        IChatProvider? reasoner = null,
        string? reasonerModel = null,
        int reviewAttempts = 1)
    {
        _providers = providers;
        _modelResolver = modelResolver;
        _workers = workers;
        _tools = tools;
        _artifacts = artifacts;
        _workspace = workspace;
        _planner = planner;
        _permissions = permissions;
        _decisions = decisions;
        _policy = policy;
        _services = services;
        _reasoner = reasoner;
        _reasonerModel = reasonerModel;
        _reviewAttempts = reviewAttempts;
    }

    public async IAsyncEnumerable<WorkEvent> SubmitIntentAsync(
        Intent intent, [EnumeratorCancellation] CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        var taskId = intent.Id;

        WorkEvent Ev(EventKind kind, string summary)
            => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, kind, summary, null);

        yield return Ev(EventKind.IntentReceived, $"Intent: {intent.RawText}");
        yield return Ev(EventKind.ContextAssembled,
            $"Workspace '{_workspace.Name}' at {_workspace.RootPath}"
            + (intent.Context.GitBranch is { } branch ? $" (git: {branch})" : ""));

        var worker = _workers.Default;
        var model = _modelResolver.Resolve(worker.ModelPolicy);
        yield return Ev(EventKind.Routed, $"Worker '{worker.Role}' -> model {model.ProviderId}/{model.Model}");
        if (_reasoner is not null)
            yield return Ev(EventKind.Routed, $"Reasoner (planning + review): {_reasonerModel}");

        var provider = _providers.Create(model.ProviderId);
        var planProvider = _reasoner ?? provider;
        var planModel = _reasoner is not null ? (_reasonerModel ?? model.Model) : model.Model;

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

            await foreach (var ev in RunToolLoopAsync(taskId, runId, provider, model.Model, messages, artifacts, intent.Context, ct))
                yield return ev;

            yield return Ev(EventKind.TaskCompleted, SummarizeArtifacts(artifacts));
            yield break;
        }

        // ── Task with a linear plan ──────────────────────────────────────────
        var builtPlan = LinearPlan.FromTitles(plan.Steps);
        yield return Ev(EventKind.PlanCreated,
            $"{plan.Title} — {builtPlan.Steps.Count} steps: {string.Join(" | ", plan.Steps)}");

        var stepNumber = 0;
        foreach (var step in builtPlan.Steps)
        {
            stepNumber++;
            yield return Ev(EventKind.StepStarted, $"[{stepNumber}/{builtPlan.Steps.Count}] {step.Title}");

            messages.Add(ChatMessage.User(
                $"Proceed with step {stepNumber} of the plan: {step.Title}\n"
                + "Do only this step. Use tools as needed. When finished, briefly confirm what you did."));

            var maxAttempts = _reasoner is not null ? _reviewAttempts + 1 : 1;
            var passed = true;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                await foreach (var ev in RunToolLoopAsync(taskId, runId, provider, model.Model, messages, artifacts, intent.Context, ct))
                    yield return ev;

                if (_reasoner is null)
                    break;

                yield return Ev(EventKind.ReviewRequested, $"[{stepNumber}] reviewing with reasoner…");

                ReviewResult review;
                try
                {
                    var changed = artifacts.Select(a => a.RelativePath).ToArray();
                    review = await _reviewer.ReviewAsync(step.Title, LastAssistant(messages), changed, _reasoner, _reasonerModel ?? "", ct);
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

            yield return Ev(EventKind.StepCompleted,
                $"[{stepNumber}/{builtPlan.Steps.Count}] {step.Title} — {(passed ? "done" : "done (review not passed)")}");
        }

        yield return Ev(EventKind.TaskCompleted, SummarizeArtifacts(artifacts));
    }

    /// <summary>
    /// Runs the streaming tool loop over a shared message list until the assistant produces a final
    /// answer (no tool calls). Emits token + tool + artifact events and appends produced artifacts.
    /// </summary>
    private async IAsyncEnumerable<WorkEvent> RunToolLoopAsync(
        Guid taskId, Guid runId, IChatProvider provider, string model,
        List<ChatMessage> messages, List<ArtifactRef> artifacts, WorkContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        WorkEvent Ev(EventKind kind, string summary)
            => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, kind, summary, null);

        for (var iteration = 1; iteration <= MaxIterations; iteration++)
        {
            var request = new ChatRequest(model, messages, _tools.Definitions, Temperature: 0.2);

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
            messages.Add(new ChatMessage(
                ChatRole.Assistant,
                contentBuilder.Length > 0 ? contentBuilder.ToString() : null,
                toolCalls));

            if (toolCalls is null)
                yield break; // final answer for this segment

            foreach (var call in toolCalls)
            {
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
                            RecommendedOptionId: "allow");

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

    private static List<ToolCall>? BuildToolCalls(Dictionary<int, ToolCallBuilder> builders)
    {
        if (builders.Count == 0)
            return null;

        return builders
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var b = kv.Value;
                var arguments = b.Arguments.Length > 0 ? b.Arguments.ToString() : "{}";
                return new ToolCall(b.Id ?? Guid.NewGuid().ToString("N"), b.Name ?? "", arguments);
            })
            .ToList();
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

    /// <summary>Accumulates a streamed tool call across deltas.</summary>
    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
