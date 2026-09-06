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
    private readonly IModelResolver _modelResolver;
    private readonly int _reviewRetries;
    private readonly int _maxParallelSteps;
    /// <summary>One approval card at a time, however many steps are running.</summary>
    private readonly SemaphoreSlim _decisionGate = new(1, 1);
    private readonly int? _numCtx;
    private readonly bool? _think;
    private readonly bool _allowImplicitToolCalls;
    private readonly bool _reviewContent;
    private readonly bool _revertRejectedSteps;
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
        int reviewRetries = 1,
        int? numCtx = null,
        bool disableThinking = false,
        int maxParallelSteps = 1,
        bool allowImplicitToolCalls = false,
        bool reviewContent = true,
        bool revertRejectedSteps = true)
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
        _modelResolver = modelResolver;
        // How many times a rejected step may be redone. Clamped rather than trusted: this multiplies
        // the cost of a run by the reviewer's price, and a stray large number would be paid for in
        // full before anyone noticed.
        _reviewRetries = Math.Clamp(reviewRetries, 0, 5);
        // 1 = the original behaviour: one step at a time on one shared conversation.
        _maxParallelSteps = Math.Max(1, maxParallelSteps);
        _numCtx = numCtx;
        // Disable the local model's <think> phase by sending think:false; null leaves it to the model.
        _think = disableThinking ? false : null;
        // Off by default: executing JSON found in a reply is a way to talk the agent into acting.
        _allowImplicitToolCalls = allowImplicitToolCalls;
        // On by default: for a step that only writes text, execution review has nothing to check, so
        // without this a configured reviewer passes anything such a step produces.
        _reviewContent = reviewContent;
        // On by default: a gate that stops the report but leaves the rejected work on disk is the
        // state a person is most likely to pick up and use.
        _revertRejectedSteps = revertRejectedSteps;
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

        // The one place a run ends. TaskCompleted is emitted for Completed and NOTHING else — the
        // whole point of the outcome type is that a failure cannot arrive dressed as a success — and
        // the kind travels in the payload so the UI, the history and the Inbox read a value instead
        // of parsing the wording.
        WorkEvent Terminal(RunOutcomeKind kind, string? reason)
            => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow,
                   kind == RunOutcomeKind.Completed ? EventKind.TaskCompleted : EventKind.TaskFailed,
                   kind == RunOutcomeKind.Completed
                       ? SummarizeArtifacts(artifacts)
                       : $"{kind}{(string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason)}",
                   WorkEventPayload.OutcomePayload(kind, reason));

        if (plan.Disposition == IntentDisposition.QuickAction)
        {
            yield return Ev(EventKind.Routed, $"Quick action: {plan.Title}");

            // Drained through a channel for the same reason as the DAG path below: the work runs in a
            // task that owns the log scope, while this method only yields what the channel hands it.
            var quick = Channel.CreateUnbounded<WorkEvent>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var quickResult = new ToolLoopResult();
            var quickPump = Task.Run(async () =>
            {
                using var _quickScope = LogScope.Begin(runId, taskId);
                try
                {
                    // A configured reviewer now applies here too. It used to run for plan steps only,
                    // while the planner was explicitly told to prefer QuickAction — so the reviewer
                    // setting did nothing for most ordinary requests, file writes and commands included.
                    var maxQuickAttempts = reviewOn ? _reviewRetries + 1 : 1;
                    var storeCheckpoint = _artifacts.Checkpoint();
                    var conversationStart = messages.Count;

                    // Accumulated across attempts, because a content retry drops the rejected one
                    // from the transcript — see the same note on the DAG path.
                    var writtenHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // The same one-shot fallback the DAG path has: an unreachable model is not the
                    // model doing bad work, so it costs no review attempt.
                    var activeRef = model;
                    var activeProvider = provider;
                    var triedFallback = false;

                    for (var attempt = 1; attempt <= maxQuickAttempts; attempt++)
                    {
                        var evidenceStart = messages.Count;

                        try
                        {
                            await foreach (var ev in RunToolLoopAsync(
                                taskId, runId, activeProvider, activeRef.Model, worker, messages, artifacts,
                                intent.Context, null, quickResult, ct))
                                quick.Writer.TryWrite(ev);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException
                                                   && !triedFallback
                                                   && _modelResolver.NextOnFailure(worker.ModelPolicy, activeRef) is not null)
                        {
                            var fallback = _modelResolver.NextOnFailure(worker.ModelPolicy, activeRef)!;
                            triedFallback = true;

                            quick.Writer.TryWrite(Ev(EventKind.Routed,
                                $"{activeRef.ProviderId}/{activeRef.Model} failed ({ex.Message}) — "
                                + $"retrying on the fallback {fallback.ProviderId}/{fallback.Model}"));

                            activeRef = fallback;
                            activeProvider = _providers.Create(fallback.ProviderId);

                            attempt--;   // the retry is the SAME attempt
                            continue;
                        }

                        foreach (var file in BuildWrittenFiles(messages, evidenceStart))
                            writtenHere.Add(file.RelativePath);

                        if (!reviewOn || !quickResult.Succeeded)
                            break;

                        quick.Writer.TryWrite(Ev(EventKind.ReviewRequested, "reviewing…"));
                        var (review, mode) = await ReviewAsync(
                            plan.Title, messages, evidenceStart, artifacts, reviewProvider!, reviewModel, ct);

                        if (review.Pass)
                        {
                            quick.Writer.TryWrite(Ev(EventKind.ReviewPassed,
                                $"PASS ({mode} review){(string.IsNullOrEmpty(review.Notes) ? "" : ": " + review.Notes)}"));
                            break;
                        }

                        quick.Writer.TryWrite(Ev(EventKind.ReviewFailed, $"FAIL ({mode} review): {review.Notes}"));

                        if (attempt < maxQuickAttempts)
                        {
                            RetryAfterReview(messages, conversationStart, mode, review.Notes, "the work");
                            continue;
                        }

                        // Out of attempts and still rejected: the work is NOT done, and saying so is
                        // the entire point of having a reviewer.
                        quickResult.Set(StepOutcomeKind.ReviewRejected, "review not passed: " + review.Notes);

                        if (_revertRejectedSteps)
                        {
                            var report = await RevertAsync(storeCheckpoint, writtenHere, artifacts, ct);
                            foreach (var line in DescribeRevert(report))
                                quick.Writer.TryWrite(Ev(EventKind.ArtifactReverted, line));
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    quickResult.Set(StepOutcomeKind.Failed, ex.Message);
                    quick.Writer.TryWrite(Ev(EventKind.ErrorObserved, ex.Message));
                }
                finally
                {
                    quick.Writer.TryComplete();
                }
            }, ct);

            await foreach (var ev in quick.Reader.ReadAllAsync(ct))
                yield return ev;

            await quickPump;

            yield return Terminal(RunOutcomeOf(new[] { quickResult.Kind }), quickResult.Reason);
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

        // How each step ended. The run's own outcome is the aggregate of these, computed once at the
        // end — not assumed to be success because the loop finished.
        var stepOutcomes = new Dictionary<Guid, StepOutcomeKind>();

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

            var maxAttempts = reviewOn ? _reviewRetries + 1 : 1;
            var stepResult = new ToolLoopResult();
            var outcome = StepOutcomeKind.Succeeded;
            string? outcomeReason = null;

            // Where the workspace and the conversation stood before this step touched either. Both
            // are needed when a review rejects: the files go back, and the rejected draft comes out
            // of the transcript instead of being carried into the retry.
            var storeCheckpoint = _artifacts.Checkpoint();
            var conversationStart = convo.Count;

            // Every path this step wrote, across ALL its attempts. Accumulated rather than read back
            // off the transcript at the end, because a content retry removes the rejected attempt
            // from the transcript — a file written only by the first attempt would otherwise be
            // invisible to the revert and left behind.
            var writtenThisStep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // One switch to the fallback model per step — see the catch below.
            var triedFallback = false;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var evidenceStart = convo.Count;

                try
                {
                    await foreach (var ev in RunToolLoopAsync(
                        taskId, runId, stepProvider, stepModel, worker, convo, artifacts,
                        intent.Context, stepNumber, stepResult, ct))
                        events.Writer.TryWrite(ev);

                    outcome = stepResult.Kind;
                    outcomeReason = stepResult.Reason;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // The worker's fallback model exists for exactly this: the endpoint is down, the
                    // key is wrong, the local server is not running. It was settable in the worker
                    // editor and never consulted — IModelResolver.NextOnFailure had no callers at
                    // all. One switch per step, and it does not spend a review attempt: failing to
                    // reach a model is not the model producing bad work.
                    if (!triedFallback
                        && _modelResolver.NextOnFailure(worker.ModelPolicy, stepRef) is { } fallback)
                    {
                        triedFallback = true;
                        Emit(EventKind.Routed,
                            $"[{stepNumber}] {stepRef.ProviderId}/{stepRef.Model} failed ({ex.Message}) — "
                            + $"retrying on the fallback {fallback.ProviderId}/{fallback.Model}");

                        stepRef = fallback;
                        stepProvider = _providers.Create(fallback.ProviderId);
                        stepModel = fallback.Model;

                        // Undo this iteration's increment so the retry is the SAME attempt.
                        attempt--;
                        continue;
                    }

                    // A throw fails only THIS step (and its dependents), never the whole run.
                    outcome = StepOutcomeKind.Failed;
                    outcomeReason = ex.Message;
                    break;
                }
                finally
                {
                    // Recorded while this attempt is still in the transcript — see writtenThisStep.
                    foreach (var file in BuildWrittenFiles(convo, evidenceStart))
                        writtenThisStep.Add(file.RelativePath);
                }

                if (!reviewOn || outcome != StepOutcomeKind.Succeeded)
                    break;

                Emit(EventKind.ReviewRequested, $"[{stepNumber}] reviewing with reasoner…");

                var (review, mode) = await ReviewAsync(
                    step.Title, convo, evidenceStart, artifacts, reviewProvider!, reviewModel, ct);

                if (review.Pass)
                {
                    Emit(EventKind.ReviewPassed,
                        $"[{stepNumber}] PASS ({mode} review){(string.IsNullOrEmpty(review.Notes) ? "" : ": " + review.Notes)}");
                    outcome = StepOutcomeKind.Succeeded;
                    break;
                }

                Emit(EventKind.ReviewFailed, $"[{stepNumber}] FAIL ({mode} review): {review.Notes}");

                if (attempt < maxAttempts)
                {
                    RetryAfterReview(convo, conversationStart, mode, review.Notes, "this step");
                    continue;
                }

                // Attempts exhausted and still rejected. This used to call MarkDone anyway, so a step
                // the reviewer had explicitly refused unblocked its dependents and the run still ended
                // Completed — which removes the only thing a review gate is for.
                outcome = StepOutcomeKind.ReviewRejected;
                outcomeReason = "review not passed: " + review.Notes;
            }

            lock (stepOutcomes)
                stepOutcomes[step.Id] = outcome;

            // A rejected step puts its work back. Otherwise the gate stops only the REPORT: the run
            // says Failed while the rejected document — invented commands and all — stays in the
            // workspace, which is the state a person is most likely to pick up and use.
            if (outcome == StepOutcomeKind.ReviewRejected && _revertRejectedSteps)
            {
                var report = await RevertAsync(storeCheckpoint, writtenThisStep, artifacts, ct);
                foreach (var line in DescribeRevert(report))
                    Emit(EventKind.ArtifactReverted, $"[{stepNumber}] {line}");
            }

            // The card's colour comes from this payload, not from the wording of the summary.
            void EmitStepDone(string summary, int? no, StepOutcomeKind kind)
                => events.Writer.TryWrite(new WorkEvent(
                    Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow,
                    EventKind.StepCompleted, summary, WorkEventPayload.StepPayload(no, kind)));

            // Succeeded is the ONLY outcome that unblocks what comes after it.
            if (outcome == StepOutcomeKind.Succeeded)
            {
                scheduler.MarkDone(step.Id);
                lock (digest)
                    digest.Add($"{step.Title}: {Gist(LastAssistant(convo))}");
                EmitStepDone($"[{stepNumber}/{total}] {step.Title} — done", stepNumber, outcome);
                return;
            }

            var label = outcome switch
            {
                StepOutcomeKind.ReviewRejected => "REVIEW REJECTED",
                StepOutcomeKind.Incomplete => "INCOMPLETE",
                _ => "FAILED"
            };

            var skippedSteps = scheduler.MarkFailed(step.Id);
            EmitStepDone(
                $"[{stepNumber}/{total}] {step.Title} — {label}{(string.IsNullOrWhiteSpace(outcomeReason) ? "" : ": " + outcomeReason)}",
                stepNumber, outcome);

            foreach (var sk in skippedSteps)
            {
                // Stamp the skipped step's own number so the UI marks ITS card, not whichever
                // card happened to be current.
                var skNo = stepNumbers.TryGetValue(sk.Id, out var n) ? n : 0;
                lock (stepOutcomes)
                    stepOutcomes[sk.Id] = StepOutcomeKind.Skipped;
                EmitStepDone(
                    $"[{skNo}/{total}] {sk.Title} — skipped (a dependency did not succeed)",
                    skNo > 0 ? skNo : (int?)null, StepOutcomeKind.Skipped);
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


        var cycle = scheduler.HasPending;
        if (cycle)
            yield return Ev(EventKind.ErrorObserved,
                "Plan has unresolvable dependencies (a cycle) — remaining steps could not run.");

        StepOutcomeKind[] outcomes;
        lock (stepOutcomes)
            outcomes = stepOutcomes.Values.ToArray();

        var runOutcome = RunOutcomeOf(outcomes);
        if (cycle && runOutcome == RunOutcomeKind.Completed)
            runOutcome = RunOutcomeKind.Incomplete;

        yield return Terminal(runOutcome, ExplainOutcome(outcomes, cycle));
    }

    /// <summary>
    /// The run's outcome from its steps'. Anything that went wrong outranks anything that went
    /// right: a plan is not finished because most of it finished. Completed requires that every step
    /// succeeded — which is exactly the guarantee the engine did not have.
    /// </summary>
    private static RunOutcomeKind RunOutcomeOf(IReadOnlyCollection<StepOutcomeKind> steps)
    {
        if (steps.Count == 0)
            return RunOutcomeKind.Incomplete;

        if (steps.Any(s => s is StepOutcomeKind.Failed or StepOutcomeKind.ReviewRejected))
            return RunOutcomeKind.Failed;

        if (steps.Any(s => s is StepOutcomeKind.Incomplete or StepOutcomeKind.Skipped))
            return RunOutcomeKind.Incomplete;

        return RunOutcomeKind.Completed;
    }

    /// <summary>A short, honest summary of why a run did not simply complete.</summary>
    private static string? ExplainOutcome(IReadOnlyCollection<StepOutcomeKind> steps, bool cycle)
    {
        var parts = new List<string>();

        var failed = steps.Count(s => s == StepOutcomeKind.Failed);
        var rejected = steps.Count(s => s == StepOutcomeKind.ReviewRejected);
        var incomplete = steps.Count(s => s == StepOutcomeKind.Incomplete);
        var skipped = steps.Count(s => s == StepOutcomeKind.Skipped);

        if (failed > 0) parts.Add($"{failed} step(s) failed");
        if (rejected > 0) parts.Add($"{rejected} step(s) rejected by the reviewer");
        if (incomplete > 0) parts.Add($"{incomplete} step(s) did not finish");
        if (skipped > 0) parts.Add($"{skipped} step(s) skipped");
        if (cycle) parts.Add("the plan had unresolvable dependencies");

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    /// How a tool loop ended, filled in by <see cref="RunToolLoopAsync"/>. A class, not a return
    /// value, because an async iterator has nowhere to put one.
    /// </summary>
    private sealed class ToolLoopResult
    {
        public StepOutcomeKind Kind { get; private set; } = StepOutcomeKind.Incomplete;
        public string? Reason { get; private set; }

        public bool Succeeded => Kind == StepOutcomeKind.Succeeded;

        public void Set(StepOutcomeKind kind, string? reason)
        {
            Kind = kind;
            Reason = reason;
        }
    }

    /// <summary>
    /// The tool calls that failed and were never made to work.
    ///
    /// A failed <see cref="ToolResult"/> used to reach only the transcript: the model saw "ERROR:
    /// file not found", replied "Done", and the step was recorded as Succeeded because a reply
    /// without tool calls was taken as a finished job. A run that never read the file it was asked
    /// to read reported green.
    ///
    /// An operation is considered recovered when the SAME call — same tool, same arguments — later
    /// succeeds. That is the only recovery this can actually verify; a different call succeeding
    /// says nothing about the one that failed. The cost is that an agent which reaches the goal by
    /// another route still leaves the step Incomplete, which is the honest reading: what it was
    /// asked to do did not happen, whatever else did.
    /// </summary>
    private sealed class OpenFailures
    {
        private readonly Dictionary<string, string> _byCall = new(StringComparer.Ordinal);

        public int Count => _byCall.Count;

        public void Failed(ToolCall call, string? error)
            => _byCall[Key(call)] = $"{call.Name} {Compact(call.ArgumentsJson)} — {error ?? "failed"}";

        public void Succeeded(ToolCall call) => _byCall.Remove(Key(call));

        public string Describe()
            => string.Join("; ", _byCall.Values);

        private static string Key(ToolCall call)
            => call.Name + " " + (call.ArgumentsJson ?? string.Empty).Trim();
    }

    /// <summary>
    /// Asks the reviewer about the work just done. Shared by the QuickAction path and by a DAG step,
    /// so a configured reviewer applies to both — it used to run for plan steps only, while the
    /// planner was told to prefer QuickAction, which left most ordinary requests unreviewed.
    ///
    /// Fails CLOSED: a reviewer that cannot answer has not approved anything.
    /// </summary>
    private async Task<(ReviewResult Result, ReviewMode Mode)> ReviewAsync(
        string title, List<ChatMessage> convo, int evidenceStart, List<ArtifactRef> artifacts,
        IChatProvider reviewProvider, string reviewModel, CancellationToken ct)
    {
        try
        {
            string[] changed;
            lock (artifacts)
                changed = artifacts.Select(a => a.RelativePath).ToArray();

            var evidence = BuildEvidence(convo, evidenceStart);

            // Which question can even be asked about this step? A step that RAN something is judged
            // on whether it ran and succeeded. A step that only WROTE something has no exit code to
            // check, so execution review passes anything — which is how a guide full of invented
            // package names and made-up command syntax finished green. There, the content itself is
            // the only thing there is to review.
            var written = BuildWrittenFiles(convo, evidenceStart);
            var mode = _reviewContent && !RanACommand(convo, evidenceStart) && written.Count > 0
                ? ReviewMode.Content
                : ReviewMode.Execution;

            var result = await _reviewer.ReviewAsync(
                title, LastAssistant(convo), evidence, changed, reviewProvider, reviewModel, ct,
                mode, written);

            return (result, mode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // This used to score an unreachable reviewer as PASS, so a stopped Ollama or a bad key
            // silently turned every step green: the gate looked configured and enforced nothing.
            return (new ReviewResult(false, "review error: " + ex.Message), ReviewMode.Execution);
        }
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
        int? stepNo, ToolLoopResult loopResult,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // An async iterator cannot return a value, so the caller passes in the slot the loop fills.
        // Without it "how did this end" existed only as English inside an event, and every consumer
        // guessed. Pessimistic until proven otherwise: falling out of the loop means the iteration
        // cap was reached, which is not success.
        loopResult.Set(StepOutcomeKind.Incomplete,
            $"reached the {MaxIterations}-iteration limit without a final answer");

        WorkEvent Ev(EventKind kind, string summary)
            => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, kind, summary,
                   stepNo is { } n ? $"{{\"step\":{n}}}" : null);

        WorkEvent Usage(int prompt, int completion)
            => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, EventKind.UsageReported,
                   $"tokens: {prompt} in, {completion} out",
                   WorkEventPayload.UsagePayload(prompt, completion, stepNo));

        // A reply that describes a call instead of making one earns exactly ONE re-ask per step; without
        // the cap a model that keeps explaining itself would burn every iteration on the same nudge.
        var repairRequested = false;
        var openFailures = new OpenFailures();

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
                loopResult.Set(StepOutcomeKind.Incomplete,
                    $"the model's output was cut off at the token limit (finish={finishReason})");
                yield return Ev(EventKind.ErrorObserved,
                    $"Model output was cut off at the token limit (finish={finishReason}); stopping this step. "
                    + "Raise max_tokens, or use a model that doesn't spend the whole budget on reasoning.");
                yield break;
            }

            var toolCalls = BuildToolCalls(toolBuilders);
            var replyText = contentBuilder.Length > 0 ? contentBuilder.ToString() : null;
            var recovered = false;

            // Prose is NOT an action. A parser cannot tell an intended call from a quoted example, an
            // explanation or a snippet the user pasted, so by default a JSON-looking reply executes
            // nothing: the model is asked to re-emit a real tool call instead. The old behaviour stays
            // available for a weak local model that cannot emit structured calls at all, but it is
            // opt-in (AllowImplicitToolCalls) precisely because it is a way to talk the agent into acting.
            var described = toolCalls is null && replyText is not null ? TryRecoverImplicitToolCall(replyText) : null;
            if (described is not null && _allowImplicitToolCalls)
            {
                toolCalls = new List<ToolCall> { described };
                recovered = true;
            }

            messages.Add(new ChatMessage(ChatRole.Assistant, replyText, toolCalls));

            if (toolCalls is null)
            {
                if (described is not null && !repairRequested)
                {
                    repairRequested = true;
                    messages.Add(ChatMessage.User(
                        $"Your reply described a '{described.Name}' call in plain text instead of invoking it. "
                        + "Nothing was executed. If you meant to act, send it again as a real tool call. "
                        + "If that JSON was only an example or an explanation, reply with your final answer."));
                    yield return Ev(EventKind.ErrorObserved,
                        $"The model described a '{described.Name}' call in plain text instead of invoking it — "
                        + "nothing was executed; asked it to re-send the call properly.");
                    continue;
                }

                // A final answer only settles the step if the actions behind it actually worked. The
                // model saying "Done" over a failed read is the exact shape the follow-up review
                // caught reporting green.
                if (openFailures.Count > 0)
                {
                    var unresolved = openFailures.Describe();
                    yield return Ev(EventKind.ErrorObserved,
                        $"Finished without resolving {openFailures.Count} failed tool call(s): {unresolved}");
                    loopResult.Set(StepOutcomeKind.Incomplete, "unresolved tool failure: " + unresolved);
                    yield break;
                }

                loopResult.Set(StepOutcomeKind.Succeeded, null);
                yield break; // genuine final answer - no tool calls
            }

            if (recovered)
                yield return Ev(EventKind.ErrorObserved,
                    $"The model described a '{toolCalls[0].Name}' call in plain text instead of "
                    + "actually invoking it - executed anyway because AllowImplicitToolCalls is on. "
                    + "Verify the result below.");

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
                var gate = _permissions.Evaluate(
                    EffectivePolicyFor(worker), call.Name, _tools.RequiredLevelOf(call.Name));
                if (gate != PermissionDecision.Allow)
                {
                    var approved = false;
                    if (gate == PermissionDecision.Ask)
                    {
                        yield return Ev(EventKind.DecisionRequested,
                            $"Approve tool '{call.Name}'? {Compact(call.ArgumentsJson)}");

                        // Detail is the one-line summary; FullDetail is what will actually run. The
                        // card must show the second before it can be approved — a 400-character
                        // PowerShell script used to be approved on its first 120 characters.
                        var decisionRequest = new DecisionRequest(
                            taskId,
                            $"Run tool '{call.Name}'?",
                            $"Arguments: {Compact(call.ArgumentsJson)}",
                            new[] { new DecisionOption("allow", "Allow"), new DecisionOption("deny", "Deny") },
                            RecommendedOptionId: "allow",
                            Subject: call.Name,
                            FullDetail: DescribeCall(call));

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
                    PermissionPolicy: EffectivePolicyFor(worker),
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

                if (result.Success)
                    openFailures.Succeeded(call);
                else
                    openFailures.Failed(call, result.Error);

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

        // The old "outermost {...} span" fallback is gone on purpose: it turned any prose containing a
        // brace into a candidate action, which is how a reply reading "Example, do not execute:" wrote a
        // file. Only a fenced ```json block is even considered, and even that is a signal to ASK for a
        // real tool call — never, by itself, permission to run one (see the caller).

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

    /// <summary>
    /// A one-line form for an EVENT LINE. Never for a decision card: shortening what a person is
    /// asked to approve, while running the whole thing, is how a long script gets approved by its
    /// first sentence. See <see cref="DescribeCall"/>.
    /// </summary>
    private static string Compact(string json)
    {
        var flattened = json.Replace('\n', ' ').Replace('\r', ' ');
        return flattened.Length <= 120 ? flattened : flattened[..120] + "…";
    }

    /// <summary>
    /// The complete action a decision authorises, laid out for a person: each argument in full, on
    /// its own, with newlines intact — a shell script has to be readable as a script. Falls back to
    /// the raw JSON when it does not parse, because showing something odd beats showing nothing.
    /// </summary>
    private static string DescribeCall(ToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return call.ArgumentsJson;

            var sb = new StringBuilder();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();

                sb.Append(property.Name).AppendLine(":");
                sb.AppendLine(value);
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return call.ArgumentsJson;
        }
    }

    /// <summary>
    /// The policy this worker actually runs under: the workspace's autonomy, narrowed by the role's
    /// own default level.
    ///
    /// A role NARROWS, never widens. Worker.DefaultLevel was settable in the worker editor, saved,
    /// and then read by nothing that mattered — a Reviewer defined as Observe still ran at whatever
    /// the workspace slider said, so the field described a restriction that did not exist. Narrowing
    /// does not forbid outright: a tool above the effective level asks for a one-off approval, which
    /// is what the permission engine already does for anything over the granted autonomy.
    /// </summary>
    private PermissionPolicy EffectivePolicyFor(Worker worker)
    {
        var level = (PermissionLevel)Math.Min((int)_policy.Level, (int)worker.DefaultLevel);
        return level == _policy.Level ? _policy : _policy with { Level = level };
    }

    /// <summary>
    /// Whether a worker's role is allowed to call the given tool. An EMPTY list means NO tools:
    /// unchecking every box in the worker editor must narrow the role, not turn it into full access.
    /// Full access is stated explicitly with "*". Settings written before SchemaVersion 2 are migrated
    /// on load (see AppSettings.Migrate), so an old empty list does not silently lose its tools.
    /// </summary>
    private static bool Allows(Worker worker, string tool)
        => worker.ToolAllowlist.Contains("*")
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

    /// <summary>
    /// Prepares the conversation for another attempt after a review rejected the work.
    ///
    /// For a step that only WROTE something, the rejected draft is removed from the transcript
    /// first. Keeping it costs tokens twice over (the tool call carries the whole file, and so does
    /// the next prompt) and anchors the model on the version it was just told is wrong — with
    /// num_ctx at 8192 a second retry was measured at 6.7k tokens, close enough to the ceiling that
    /// Ollama would have started silently dropping the system prompt, honesty rules included. The
    /// model rewrites the whole file on every attempt anyway, so nothing is lost.
    ///
    /// For a step that RAN something, the transcript stays: the command output IS the evidence, and
    /// discarding it would mean re-running commands that have already had their effect.
    /// </summary>
    private static void RetryAfterReview(
        List<ChatMessage> convo, int conversationStart, ReviewMode mode, string notes, string what)
    {
        if (mode == ReviewMode.Content && convo.Count > conversationStart)
            convo.RemoveRange(conversationStart, convo.Count - conversationStart);

        convo.Add(ChatMessage.User(
            $"A reviewer rejected the previous attempt with this feedback: {notes}\n"
            + (mode == ReviewMode.Content
                ? $"That attempt has been discarded. Redo {what} from scratch, correcting every point above."
                : $"Please fix the issues and redo {what}.")));
    }

    /// <summary>
    /// Puts back what the rejected work produced, and takes it out of the run's artifact list so the
    /// summary does not go on claiming files that are no longer there.
    /// </summary>
    private async Task<RevertReport> RevertAsync(
        int checkpoint, IReadOnlyCollection<string> written,
        List<ArtifactRef> artifacts, CancellationToken ct)
    {
        if (written.Count == 0)
            return RevertReport.Empty;

        RevertReport report;
        try
        {
            report = await _artifacts.RevertToAsync(checkpoint, written, ct);
        }
        catch (Exception ex)
        {
            // Failing to undo is worth saying out loud; it is not worth failing the run twice over.
            return new RevertReport(Array.Empty<string>(), new[] { $"(revert failed: {ex.Message})" });
        }

        if (report.Reverted.Count > 0)
        {
            lock (artifacts)
                artifacts.RemoveAll(a =>
                    report.Reverted.Contains(a.RelativePath, StringComparer.OrdinalIgnoreCase));
        }

        return report;
    }

    /// <summary>One line per thing worth telling the user about a revert; nothing when nothing happened.</summary>
    private static IEnumerable<string> DescribeRevert(RevertReport report)
    {
        if (report.Reverted.Count > 0)
            yield return "Rejected work put back: " + string.Join(", ", report.Reverted);

        if (report.Kept.Count > 0)
            yield return "Left as it is because it changed after the step wrote it: "
                       + string.Join(", ", report.Kept);
    }

    /// <summary>Tools that make something happen outside the workspace's files.</summary>
    private static readonly HashSet<string> CommandTools =
        new(StringComparer.OrdinalIgnoreCase) { "run_command", "run_powershell", "git", "docker" };

    /// <summary>
    /// Whether this step actually executed anything. Decides which question the reviewer is asked:
    /// a step with a command has a real exit code to be judged on, a step without one does not.
    /// </summary>
    private static bool RanACommand(List<ChatMessage> messages, int start)
    {
        for (var i = Math.Max(0, start); i < messages.Count; i++)
            if (messages[i].Role == ChatRole.Assistant && messages[i].ToolCalls is { Count: > 0 } calls)
                foreach (var call in calls)
                    if (CommandTools.Contains(call.Name))
                        return true;

        return false;
    }

    /// <summary>
    /// The content this step wrote, taken from the write_file calls themselves rather than from disk:
    /// staging means the file may not be on disk at all, and this is what the step is claiming to have
    /// produced either way. The LAST write to a path wins — an earlier draft it replaced is not what
    /// the user ends up with.
    /// </summary>
    private static IReadOnlyList<WrittenFile> BuildWrittenFiles(List<ChatMessage> messages, int start)
    {
        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        for (var i = Math.Max(0, start); i < messages.Count; i++)
        {
            if (messages[i].Role != ChatRole.Assistant || messages[i].ToolCalls is not { Count: > 0 } calls)
                continue;

            foreach (var call in calls)
            {
                if (!string.Equals(call.Name, "write_file", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(call.ArgumentsJson);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object
                        || !doc.RootElement.TryGetProperty("path", out var p)
                        || p.ValueKind != JsonValueKind.String
                        || !doc.RootElement.TryGetProperty("content", out var c)
                        || c.ValueKind != JsonValueKind.String)
                        continue;

                    var path = p.GetString();
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    if (!byPath.ContainsKey(path))
                        order.Add(path);
                    byPath[path] = c.GetString() ?? "";
                }
                catch (JsonException)
                {
                    // Arguments the tool itself would reject are not evidence of content.
                }
            }
        }

        return order.Select(path => new WrittenFile(path, byPath[path])).ToArray();
    }

    /// <summary>Accumulates a streamed tool call across deltas.</summary>
    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
