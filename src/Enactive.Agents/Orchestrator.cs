namespace Enactive.Agents;

using System.Globalization;
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
using Enactive.Core.Execution;
using Enactive.Core.Intents;
using Enactive.Core.Orchestration;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Core.Workers;

/// <summary>
/// MVP #2 orchestrator: Intent -> Understand/Plan -> (QuickAction | Task) -> streaming tool loop per
/// step -> ToolResult -> Artifact -> Event. Permission gating and decisions layer on in MVP #3.
/// </summary>
public sealed class Orchestrator : IOrchestrator
{
    /// <summary>
    /// How many turns in a row may go by without the step doing anything it has not already done,
    /// before it is called stuck.
    ///
    /// <para>This replaced a flat cap of 12 turns, which was the wrong quantity to count. A step
    /// that scaffolds an Avalonia project reads seven files and writes seven more, one per turn, and
    /// was cut off on its twelfth having done nothing wrong — while a model rereading the same file
    /// forever was equally welcome to twelve. Work is not the thing to limit; a project with a
    /// hundred files needs a hundred turns and no setting should have to say so. REPETITION is the
    /// thing to limit, and it does not grow with the project.</para>
    /// </summary>
    private const int StallLimit = 3;

    /// <summary>
    /// An absolute backstop, not a work limit: nothing legitimate reaches it, and a step that does
    /// has gone wrong in a way <see cref="StallLimit"/> cannot see (an agent inventing new work
    /// forever). Deliberately far above any real task, because the moment this number starts
    /// deciding outcomes it is the flat cap again under a new name.
    /// </summary>
    private const int RunawayCeiling = 250;

    /// <summary>
    /// The tools that CHANGE the workspace. Shared by <see cref="OpenFailures"/> (a later file of
    /// theirs can close an earlier failure) and by <see cref="StepProgress"/> (after one of these
    /// lands, everything read afterwards is being read off a different tree).
    /// </summary>
    private static readonly HashSet<string> MutatingTools =
        new(StringComparer.Ordinal) { "write_file", "edit_file", "move_file", "create_directory" };

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

    /// <summary>
    /// The checks that decide whether this run is finished, independently of what the model says
    /// about it. Empty is the behaviour that existed before them: the only voices were the worker's
    /// own closing sentence and a reviewer's opinion of free text.
    /// </summary>
    private readonly IReadOnlyList<SuccessCriterionDefinition> _successCriteria;
    private readonly SuccessEvaluator _successEvaluator = new();

    /// <summary>
    /// What this run may spend. Null everywhere it is not set, which is the behaviour that existed
    /// before limits were enforced at all - they shipped as data in M0 and were read by nothing.
    /// </summary>
    private readonly ExecutionLimits _limits;

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
        bool revertRejectedSteps = true,
        IReadOnlyList<SuccessCriterionDefinition>? successCriteria = null,
        ExecutionLimits? limits = null)
    {
        _successCriteria = successCriteria ?? Array.Empty<SuccessCriterionDefinition>();
        _limits = limits ?? ExecutionLimits.None;
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

        // A routing decision carries its choice as VALUES, not only as a sentence. The panel that
        // answers "which model actually ran this" reads the payload, so rewording a summary cannot
        // change what it shows - the same reason step outcomes stopped being parsed out of prose.
        WorkEvent Route(string purpose, ModelRef reference, string summary,
                        int? stepNo = null, StepComplexity? complexity = null)
            => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, EventKind.Routed, summary,
                   WorkEventPayload.RoutePayload(purpose, reference.ProviderId, reference.Model,
                                                 stepNo, complexity?.ToString().ToLowerInvariant()));

        // Tokens spent OUTSIDE the tool loop. The loop emits its own usage; planning and review call
        // the provider directly, so their cost was spent on every run and counted on none - which
        // made the run total execute-only while the reviewer, on the most expensive model bound, read
        // whole documents for free as far as the UI was concerned.
        // What this run may spend, and what is gone. Every phase counts against it - planning,
        // execution and review - because the budget is what the RUN costs, and a reviewer on a large
        // cloud model can be the larger half of that.
        var budget = new RunBudget(_limits, DateTimeOffset.UtcNow);

        WorkEvent UsageOutsideLoop(
            string purpose, ModelRef reference, int prompt, int completion, int? stepNo = null)
        {
            budget.TokensUsed(prompt, completion);
            return new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, EventKind.UsageReported,
                       $"tokens: {prompt} in, {completion} out ({reference.ProviderId}/{reference.Model}, {purpose})",
                       WorkEventPayload.UsagePayload(prompt, completion, stepNo,
                                                     reference.ProviderId, reference.Model, purpose));
        }

        yield return new WorkEvent(
            Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, EventKind.IntentReceived,
            $"Intent: {intent.RawText}",
            // The request as a value, so a retry can ask for the same thing rather than reconstruct
            // it from the wording of a log line.
            WorkEventPayload.RequestPayload(intent.RawText));
        yield return Ev(EventKind.ContextAssembled,
            $"Workspace '{_workspace.Name}' at {_workspace.RootPath}"
            + (intent.Context.GitBranch is { } branch ? $" (git: {branch})" : "")
            + (intent.Context.Environment is { } envInfo ? $" · {envInfo.OneLine()}" : ""));

        var worker = _workers.Get(intent.WorkerId);
        var model = _router.Resolve(ModelPurpose.Execute, worker) ?? worker.ModelPolicy.Preferred;
        yield return Route("worker", model, $"Worker '{worker.Role}' -> model {model.ProviderId}/{model.Model}");

        var provider = _providers.Create(model.ProviderId);

        // Plan phase: the bound Plan model, else the executing model.
        var planRef = _router.Resolve(ModelPurpose.Plan, worker) ?? model;
        var planProvider = _providers.Create(planRef.ProviderId);
        var planModel = planRef.Model;
        if (planRef.ProviderId != model.ProviderId || planRef.Model != model.Model)
            yield return Route("plan", planRef, $"Planner -> {planRef.ProviderId}/{planRef.Model}");

        // Review phase: on iff a Review model is bound.
        var reviewRef = _router.Resolve(ModelPurpose.Review, worker);
        var reviewOn = reviewRef is not null;
        var reviewProvider = reviewOn ? _providers.Create(reviewRef!.ProviderId) : null;
        var reviewModel = reviewRef?.Model ?? "";
        if (reviewOn)
            yield return Route("review", reviewRef!, $"Reviewer -> {reviewRef!.ProviderId}/{reviewRef.Model}");

        // ── Understand / Plan (reasoner when multi-agent) ─────────────────────
        var plan = await InScopeAsync(runId, taskId, null,
            () => _planner.PlanAsync(intent.RawText, intent.Context, planProvider, planModel, ct));

        if (plan.PromptTokens + plan.CompletionTokens > 0)
            yield return UsageOutsideLoop(
                WorkEventPayload.WorkPurpose.Plan, planRef, plan.PromptTokens, plan.CompletionTokens);

        // A plan nobody could read is not a decision to do one thing. The two were the same value
        // and the same title until now, so a genuine multi-step request that arrived back as prose
        // became one unplanned action under a heading cut from the request - and the run showed
        // nothing at all. The work still happens; what changes is that the run says on what basis.
        if (plan.Readout == PlanReadout.Unreadable)
            yield return Ev(EventKind.ErrorObserved,
                "The planner's answer could not be read, twice. Running this as a single action — "
                + "that is a fallback, not a decision that the request has one step.");

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

        // A criterion's result as VALUES as well as a sentence - the same reason every other event
        // carries a payload: rewording a summary must not change what a reader of the run sees.
        WorkEvent Criterion(CriterionResult r)
            => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, EventKind.CriterionEvaluated,
                   r.Describe(),
                   WorkEventPayload.CriterionPayload(r.Name, r.Outcome.ToString(), r.Required, r.ExitCode));

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

                    // This run's own view of the store. Everything it writes belongs to it, and it
                    // is the only thing that can undo those writes - see IArtifactScope.
                    var store = _artifacts.BeginStep();

                    // What this run actually DID, written down as it happens. The reviewer's
                    // evidence used to be read back out of the conversation, which is the model's
                    // working memory and gets shortened when the window fills.
                    var journal = new ExecutionJournal();
                    var conversationStart = messages.Count;

                    // The same one-shot fallback the DAG path has: an unreachable model is not the
                    // model doing bad work, so it costs no review attempt.
                    var activeRef = model;
                    var activeProvider = provider;
                    var triedFallback = false;

                    // See the same line on the DAG path: the evidence window moves only when a
                    // retry discards the attempt before it.
                    var evidenceStart = journal.Mark();

                    for (var attempt = 1; attempt <= maxQuickAttempts; attempt++)
                    {
                        // A quick action runs no plan steps, so a STEP limit never bites here - but
                        // a token or time limit can, and a retry is the natural place to notice: it
                        // is the only point in this path where more spending is about to be chosen
                        // rather than already under way.
                        if (budget.Exhausted is { } spent)
                        {
                            quickResult.Set(StepOutcomeKind.Incomplete, spent);
                            quick.Writer.TryWrite(Ev(EventKind.ErrorObserved, spent));
                            break;
                        }

                        try
                        {
                            await foreach (var ev in RunToolLoopAsync(
                                taskId, runId, activeProvider, activeRef.Model, worker, messages, artifacts,
                                intent.Context, store, journal, null, quickResult, budget, ct, activeRef.ProviderId))
                                quick.Writer.TryWrite(ev);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException
                                                   && !triedFallback
                                                   && _modelResolver.NextOnFailure(worker.ModelPolicy, activeRef) is not null)
                        {
                            var fallback = _modelResolver.NextOnFailure(worker.ModelPolicy, activeRef)!;
                            triedFallback = true;

                            // Routed as the worker's model, because from here on it IS the model
                            // doing the work: a panel that still named the unreachable one would be
                            // reporting a binding rather than what ran.
                            quick.Writer.TryWrite(Route("worker", fallback,
                                $"{activeRef.ProviderId}/{activeRef.Model} failed ({ex.Message}) — "
                                + $"retrying on the fallback {fallback.ProviderId}/{fallback.Model}"));

                            activeRef = fallback;
                            activeProvider = _providers.Create(fallback.ProviderId);

                            attempt--;   // the retry is the SAME attempt
                            continue;
                        }

                        if (!reviewOn || !quickResult.Succeeded)
                            break;

                        quick.Writer.TryWrite(Ev(EventKind.ReviewRequested, "reviewing…"));
                        var (review, mode) = await ReviewAsync(
                            plan.Title, messages, journal, evidenceStart, artifacts, store,
                            reviewProvider!, reviewModel, ct);

                        if (review.PromptTokens + review.CompletionTokens > 0)
                            quick.Writer.TryWrite(UsageOutsideLoop(
                                WorkEventPayload.WorkPurpose.Review, reviewRef!,
                                review.PromptTokens, review.CompletionTokens));

                        if (review.Pass)
                        {
                            quick.Writer.TryWrite(Ev(EventKind.ReviewPassed,
                                $"PASS ({mode} review){(string.IsNullOrEmpty(review.Notes) ? "" : ": " + review.Notes)}"));
                            break;
                        }

                        quick.Writer.TryWrite(Ev(EventKind.ReviewFailed, $"FAIL ({mode} review): {review.Notes}"));

                        if (attempt < maxQuickAttempts)
                        {
                            // See the DAG path: the evidence window follows the transcript window.
                            if (RetryAfterReview(messages, conversationStart, mode, review.Notes, "the work"))
                                evidenceStart = journal.Mark();
                            continue;
                        }

                        // Out of attempts and still rejected: the work is NOT done, and saying so is
                        // the entire point of having a reviewer.
                        quickResult.Set(StepOutcomeKind.ReviewRejected, "review not passed: " + review.Notes);

                        if (_revertRejectedSteps)
                        {
                            var report = await RevertAsync(store, artifacts, ct);
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

            var quickOutcome = RunOutcomeOf(new[] { quickResult.Kind });
            var quickReason = quickResult.Reason;

            if (quickOutcome == RunOutcomeKind.Completed)
            {
                var report = await InScopeAsync(runId, taskId, null,
                    () => CheckSuccessAsync(taskId, runId, intent.Context, ct));

                foreach (var checkResult in report.Results)
                    yield return Criterion(checkResult);

                var adjusted = report.Apply(quickOutcome);
                if (adjusted != quickOutcome)
                {
                    quickOutcome = adjusted;
                    quickReason = report.Explain();
                }
            }

            yield return Terminal(quickOutcome, quickReason);
            yield break;
        }

        // ── Task with a DAG plan ──────────────────────────────────────────
        var builtPlan = plan.Plan ?? LinearPlan.FromTitles(new[] { plan.Title });
        var total = builtPlan.Steps.Count;
        var stepTitles = builtPlan.Steps.Select(x => x.Title).ToArray();
        yield return new WorkEvent(
            Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, EventKind.PlanCreated,
            $"{plan.Title} — {total} steps: {string.Join(" | ", stepTitles)}",
            // The titles as VALUES. Read out of the sentence, a step title containing " | " became
            // two cards and a plan title containing " — " lost its tail.
            WorkEventPayload.PlanPayload(plan.Title, stepTitles));

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
            // Emitted for EVERY step, not only when it differs from the worker's model. "Which model
            // ran this step" is the question the panel exists to answer, and answering it only
            // sometimes is exactly how a run could show a local worker binding while all of its steps
            // in fact went to the cloud, because the planner had rated them complex.
            events.Writer.TryWrite(Route("step", stepRef,
                $"[{stepNumber}] {step.Complexity} step -> {stepRef.ProviderId}/{stepRef.Model}",
                stepNumber, step.Complexity));

            var maxAttempts = reviewOn ? _reviewRetries + 1 : 1;
            var stepResult = new ToolLoopResult();
            var outcome = StepOutcomeKind.Succeeded;
            string? outcomeReason = null;

            // This step's own view of the store, and where the conversation stood before it began.
            // Both are needed when a review rejects: the files this step wrote go back - and only
            // the ones it wrote, whatever a concurrent step is doing - and the rejected draft comes
            // out of the transcript instead of being carried into the retry.
            var store = _artifacts.BeginStep();

            // This step's own record of what it did - see the note on the quick-action path.
            var journal = new ExecutionJournal();
            var conversationStart = convo.Count;

            // One switch to the fallback model per step — see the catch below.
            var triedFallback = false;

            // Where the reviewer's evidence starts. It moves only when a retry DISCARDS the attempt
            // before it — see the note at RetryAfterReview below.
            var evidenceStart = journal.Mark();

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await foreach (var ev in RunToolLoopAsync(
                        taskId, runId, stepProvider, stepModel, worker, convo, artifacts,
                        intent.Context, store, journal, stepNumber, stepResult, budget, ct, stepRef.ProviderId))
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
                        // Same reason as the quick-action path: after the switch the fallback is
                        // what runs this step, so that is what the step's routing row must name.
                        events.Writer.TryWrite(Route("step", fallback,
                            $"[{stepNumber}] {stepRef.ProviderId}/{stepRef.Model} failed ({ex.Message}) — "
                            + $"retrying on the fallback {fallback.ProviderId}/{fallback.Model}",
                            stepNumber, step.Complexity));

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

                if (!reviewOn || outcome != StepOutcomeKind.Succeeded)
                    break;

                Emit(EventKind.ReviewRequested, $"[{stepNumber}] reviewing with reasoner…");

                var (review, mode) = await ReviewAsync(
                    step.Title, convo, journal, evidenceStart, artifacts, store, reviewProvider!, reviewModel, ct);

                if (review.PromptTokens + review.CompletionTokens > 0)
                    events.Writer.TryWrite(UsageOutsideLoop(
                        WorkEventPayload.WorkPurpose.Review, reviewRef!,
                        review.PromptTokens, review.CompletionTokens, stepNumber));

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
                    // The evidence window has to be the same window the ANSWER is drawn from.
                    // A discarded attempt is gone from the model's memory too, so the retry starts
                    // clean and the evidence starts with it. A KEPT transcript is the opposite: the
                    // model can still cite what it did on the first attempt — correctly — and
                    // evidence beginning after those calls makes an honest answer look invented.
                    // That is what happened on 2026-09-07 19:36; see FIX_PLAN §9f.
                    if (RetryAfterReview(convo, conversationStart, mode, review.Notes, "this step"))
                        evidenceStart = journal.Mark();
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
                var report = await RevertAsync(store, artifacts, ct);
                foreach (var line in DescribeRevert(report))
                    Emit(EventKind.ArtifactReverted, $"[{stepNumber}] {line}");
            }

            // The card's colour comes from this payload, not from the wording of the summary.
            void EmitStepDone(string summary, int? no, StepOutcomeKind kind)
                => events.Writer.TryWrite(new WorkEvent(
                    Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow,
                    EventKind.StepCompleted, summary, WorkEventPayload.StepPayload(no, kind)));

            // Succeeded is the ONLY outcome that unblocks what comes after it - and MarkDone is what
            // does the unblocking, so it goes LAST. It used to go first, which opened two races on
            // everything a dependent is entitled to see the moment it starts:
            //
            //   - the DIGEST. A dependent seeds its conversation from it, and could be dispatched
            //     and read it before this step's line was in - losing the conclusion of the very
            //     step it was waiting for, silently and only sometimes.
            //   - the COMPLETION EVENT. The join's StepStarted could reach the channel ahead of this
            //     step's StepCompleted, so a reader - the UI's step cards, a log, a test - saw a
            //     step begin before the thing it depends on had finished.
            //
            // Nothing here needs to happen before the unblocking. Everything here needs to be
            // visible to whoever the unblocking releases.
            if (outcome == StepOutcomeKind.Succeeded)
            {
                lock (digest)
                    digest.Add($"{step.Title}: {Gist(LastAssistant(convo))}");
                EmitStepDone($"[{stepNumber}/{total}] {step.Title} — done", stepNumber, outcome);
                scheduler.MarkDone(step.Id);
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

        // Set when a limit stops the run, so the terminal event can say which one rather than
        // reporting a pile of skipped steps with no explanation for them.
        string? limitReason = null;

        // Dispatcher: keep up to maxParallel steps in flight, topping up as each one finishes.
        var pump = Task.Run(async () =>
        {
            using var _pumpScope = LogScope.Begin(runId, taskId);
            var inFlight = new List<Task>();
            try
            {
                while (true)
                {
                    // BEFORE dispatching, never during: a limit stops the next step, it does not kill
                    // the one running. Cancelling work in flight throws away what that step had
                    // already done and leaves the workspace in a state nobody chose.
                    if (limitReason is null && budget.Exhausted is { } spent)
                    {
                        limitReason = spent;
                        events.Writer.TryWrite(Ev(EventKind.ErrorObserved, spent));

                        // Pending steps become Skipped rather than staying Pending: the run's outcome
                        // is built from its steps', so a step with no recorded outcome would quietly
                        // not count at all.
                        foreach (var abandoned in scheduler.AbandonPending())
                        {
                            var abNo = stepNumbers.TryGetValue(abandoned.Id, out var an) ? an : 0;
                            lock (stepOutcomes)
                                stepOutcomes[abandoned.Id] = StepOutcomeKind.Skipped;
                            events.Writer.TryWrite(new WorkEvent(
                                Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow,
                                EventKind.StepCompleted,
                                $"[{(abNo > 0 ? abNo : 0)}/{total}] {abandoned.Title} — skipped ({spent})",
                                WorkEventPayload.StepPayload(abNo > 0 ? abNo : null, StepOutcomeKind.Skipped)));
                        }
                    }

                    foreach (var ready in scheduler.NextReadyBatch(maxParallel - inFlight.Count))
                    {
                        budget.StepStarted();
                        inFlight.Add(RunStepAsync(ready));
                    }

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

        var runReason = ExplainOutcome(outcomes, cycle, limitReason);

        // The last word, and the only one in the run that is not somebody's opinion. Checked only
        // when everything else says the work is done: a run that already failed had its outcome
        // decided by something that actually went wrong, and a build result on top of that would
        // bury it - besides costing a build to learn nothing.
        if (runOutcome == RunOutcomeKind.Completed)
        {
            var report = await InScopeAsync(runId, taskId, null,
                () => CheckSuccessAsync(taskId, runId, intent.Context, ct));

            foreach (var checkResult in report.Results)
                yield return Criterion(checkResult);

            var adjusted = report.Apply(runOutcome);
            if (adjusted != runOutcome)
            {
                runOutcome = adjusted;
                runReason = report.Explain();
            }
        }

        yield return Terminal(runOutcome, runReason);
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
    private static string? ExplainOutcome(
        IReadOnlyCollection<StepOutcomeKind> steps, bool cycle, string? limit = null)
    {
        var parts = new List<string>();

        // First, because it EXPLAINS the skipped steps that follow it: without it a run that hit its
        // ceiling reports "4 step(s) skipped" and nothing about why.
        if (!string.IsNullOrWhiteSpace(limit))
            parts.Add(limit!);

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
    ///
    /// A lookup that found nothing is held to a different standard. <see cref="ToolResult.IsAnswer"/>
    /// marks the failures that ANSWERED — read_file on a path that does not exist, list_dir on a
    /// folder that is not there — and guessing at a name and being told no is how anything explores
    /// a tree it has not seen. Those are forgiven, with one condition: <b>unless they are all the
    /// step has to show for itself.</b> A step whose every action was a lookup that found nothing
    /// produced nothing, and "Done" over that is the exact shape this class was built to catch. One
    /// call that WORKED is what separates a step exploring from a step with nothing.
    /// </summary>
    private sealed class OpenFailures
    {
        private readonly Dictionary<string, string> _byCall = new(StringComparer.Ordinal);

        /// <summary>Lookups that found nothing — see the note above about when these count.</summary>
        private readonly Dictionary<string, string> _foundNothing = new(StringComparer.Ordinal);

        /// <summary>The file each open failure was trying to change, where it named one.</summary>
        private readonly Dictionary<string, string> _fileOf = new(StringComparer.Ordinal);

        /// <summary>
        /// Open failures that named NO file, by the tool that produced them — a call refused for a
        /// missing required argument, which is a sentence that did not parse rather than an action
        /// that did not happen. See <see cref="Succeeded"/>.
        /// </summary>
        private readonly Dictionary<string, string> _namedNothing = new(StringComparer.Ordinal);

        private bool _anythingWorked;

        public int Count => _byCall.Count + (_anythingWorked ? 0 : _foundNothing.Count);

        /// <summary>True when the step's whole record is lookups that found nothing.</summary>
        public bool NothingButMisses
            => !_anythingWorked && _byCall.Count == 0 && _foundNothing.Count > 0;

        public void Failed(ToolCall call, string? error)
        {
            var key = Key(call);
            _byCall[key] = Line(call, error);

            if (FileNamedBy(call) is { } file)
                _fileOf[key] = file;
            else if (ProducesFiles.Contains(call.Name))
                _namedNothing[key] = call.Name;
        }

        /// <summary>
        /// The tools whose work IS a file, so a later file of theirs can show the work happened.
        /// A command is not one of them: nothing it produces says the earlier one succeeded.
        /// </summary>
        private static HashSet<string> ProducesFiles => MutatingTools;

        /// <summary>A lookup whose target is not there. An answer — unless the step has nothing else.</summary>
        public void FoundNothing(ToolCall call, string? error)
            => _foundNothing[Key(call)] = Line(call, error);

        /// <summary>
        /// A call that worked, and the files it produced.
        ///
        /// <para>Those files close any failure that was trying to change one of them. Reported
        /// 2026-09-07 21:01: an <c>edit_file</c> whose <c>old_string</c> did not match, and a
        /// <c>write_file</c> sent without its <c>path</c> — both on Program.cs, both followed
        /// immediately by a <c>write_file</c> of that same file that WORKED. The tests were written.
        /// The step was marked Incomplete for two calls the model had already made good, plus a
        /// third, and the step after it was skipped.</para>
        ///
        /// <para>The old rule — recovered only when the same call, same arguments, succeeds — took a
        /// tool call for the goal. It is not: the model was never asked to call edit_file with that
        /// exact old_string, it chose to, and when the choice did not work it rewrote the file
        /// instead. The FILE is the thing that was asked for, and the artifact store says which files
        /// a call actually produced, so this is evidence rather than inference.</para>
        /// </summary>
        /// <param name="produced">
        /// <para>A second case, from the same log: <c>write_file {"content":"…"}</c> with the path
        /// left out, refused with <i>'path' is required</i> before it touched anything, and sent
        /// again correctly twenty-two seconds later. That call names no file, so nothing above can
        /// close it — and it is not work that did not happen, it is a sentence that did not parse.
        /// The same tool succeeding afterwards is the model having said it properly.</para>
        ///
        /// <para>The hole that leaves: a model could send a malformed write, never correct it, and
        /// have an unrelated successful write close it. Small, visible in the evidence either way,
        /// and much smaller than the alternative — every mistyped argument poisoning its step for
        /// good, which is what the log showed.</para>
        /// </param>
        public void Succeeded(ToolCall call, IReadOnlyList<ArtifactRef> produced)
        {
            _anythingWorked = true;
            Close(Key(call));

            foreach (var reference in produced)
                foreach (var open in _fileOf.Where(p => SameFile(p.Value, reference.RelativePath))
                                            .Select(p => p.Key).ToArray())
                    Close(open);

            foreach (var open in _namedNothing.Where(p => p.Value == call.Name)
                                              .Select(p => p.Key).ToArray())
                Close(open);
        }

        private void Close(string key)
        {
            _byCall.Remove(key);
            _foundNothing.Remove(key);
            _fileOf.Remove(key);
            _namedNothing.Remove(key);
        }

        /// <summary>The file a call was trying to change, from its own arguments.</summary>
        private static string? FileNamedBy(ToolCall call)
        {
            try
            {
                using var doc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return null;

                // "path" for write/edit/create; "to" is where a move puts the file, which is the
                // one that has to exist afterwards.
                foreach (var name in new[] { "path", "to" })
                    if (doc.RootElement.TryGetProperty(name, out var value)
                        && value.ValueKind == JsonValueKind.String
                        && value.GetString() is { Length: > 0 } text)
                        return text;

                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Workspace-relative paths, compared as this platform compares them.</summary>
        private static bool SameFile(string a, string b)
            => string.Equals(a.Replace('\\', '/').Trim('/'), b.Replace('\\', '/').Trim('/'),
                             StringComparison.OrdinalIgnoreCase);

        public string Describe()
            => string.Join("; ", _anythingWorked ? _byCall.Values : _byCall.Values.Concat(_foundNothing.Values));

        private static string Line(ToolCall call, string? error)
            => $"{call.Name} {Compact(call.ArgumentsJson)} — {error ?? "failed"}";

        private static string Key(ToolCall call) => CallIdentity.Of(call);
    }

    /// <summary>
    /// What makes a tool call THAT call — shared by <see cref="OpenFailures"/> (has the failed one
    /// been made to work?) and <see cref="StepProgress"/> (is this anything new?), so "the same call
    /// again" means one thing in this file.
    ///
    /// <para>The name plus the arguments as an ACTION. <c>expectedExitCodes</c> is not part of it:
    /// that argument says how to READ a result, not what to do, so a command and the same command
    /// declaring that 1 is an answer are one call. Without that, the only route this engine offers
    /// out of such a failure — run it again and declare — would produce a different identity, leave
    /// the first failure open, and kill the step anyway. Advice that cannot be followed is worse
    /// than none.</para>
    ///
    /// <para>Every call is canonicalised, not only the ones carrying that argument: a raw string and
    /// a rebuilt one would never meet. Property order stops mattering as a free consequence, which
    /// it never should have.</para>
    /// </summary>
    internal static class CallIdentity
    {
        internal static string Of(ToolCall call)
            => call.Name + "\0" + AsAction(call.ArgumentsJson);

        private static string AsAction(string? argumentsJson)
        {
            var text = (argumentsJson ?? string.Empty).Trim();
            if (text.Length == 0)
                return text;

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return text;

                var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in doc.RootElement.EnumerateObject())
                    if (!string.Equals(property.Name, ToolArguments.ExpectedExitCodes, StringComparison.Ordinal))
                        kept[property.Name] = property.Value.GetRawText();

                return string.Join("\0", kept.Select(p => p.Key + "=" + p.Value));
            }
            catch (JsonException)
            {
                // Unparseable arguments are their own identity — two identical broken calls are the
                // same broken call, which is what the stall detector needs to see.
                return text;
            }
        }
    }

    /// <summary>
    /// Whether a step is still getting somewhere.
    ///
    /// <para>"Somewhere" is deliberately cheap to define: a tool call, by name and arguments, that
    /// this step has not made before. Success is not required — a command that fails teaches the
    /// model something and an unresolved failure is already caught at the end of the loop — so what
    /// is left is exactly repetition, which is what a stuck model actually does.</para>
    ///
    /// <para>With one correction, which is the whole point of this class existing after a run that
    /// proved it wrong. A REPAIR LOOP is made of repeated calls by construction: read the file,
    /// edit it, build, read it again, edit again, build again. Counted naively, the second read and
    /// the second build are "calls it had already made" — and a step that had just broken the build
    /// and was in the middle of fixing it was stopped for doing the fixing. But those calls are not
    /// the same calls: the file they read and the tree they build no longer exist as they were. So
    /// a successful <see cref="MutatingTools">write</see> advances a GENERATION, and everything
    /// that only observes — reads, commands — is identified together with the generation it
    /// observed. After a real change, looking again is new.</para>
    ///
    /// <para>The writes themselves carry no generation, which is what keeps this bounded: escaping
    /// a stall costs a write nobody has made before. Two edits alternating forever are still two
    /// calls already made, and still stall. And <see cref="RunawayCeiling"/> remains behind all of
    /// it for the case this cannot see.</para>
    /// </summary>
    private sealed class StepProgress
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly List<string> _repeats = new();

        /// <summary>How many times the workspace has changed under this step.</summary>
        private int _generation;

        /// <summary>Turns in a row that did nothing new.</summary>
        public int Stalled { get; private set; }

        /// <summary>
        /// A write landed. Everything observed from here on is observing something else — said once,
        /// here, rather than by each caller deciding what counts as a change.
        /// </summary>
        public void WorkspaceChanged() => _generation++;

        /// <summary>Records one turn's calls and says whether any of them was new.</summary>
        public bool Advanced(IReadOnlyList<ToolCall> calls)
        {
            var advanced = false;
            var repeatedHere = new List<string>();

            foreach (var call in calls)
                if (_seen.Add(Identity(call)))
                    advanced = true;
                else
                    repeatedHere.Add($"{call.Name} {Compact(call.ArgumentsJson)}");

            if (advanced)
            {
                Stalled = 0;
                _repeats.Clear();
            }
            else
            {
                Stalled++;
                foreach (var repeat in repeatedHere)
                    if (!_repeats.Contains(repeat))
                        _repeats.Add(repeat);
            }

            return advanced;
        }

        /// <summary>
        /// What makes this call this call, HERE. A write is itself; anything else is itself plus the
        /// state of the workspace it is about to look at.
        /// </summary>
        private string Identity(ToolCall call)
            => MutatingTools.Contains(call.Name)
                ? CallIdentity.Of(call)
                : CallIdentity.Of(call) + "\0#" + _generation.ToString(CultureInfo.InvariantCulture);

        /// <summary>What it kept asking for, for the message that stops it.</summary>
        public string Describe()
            => _repeats.Count == 0 ? "no new tool calls" : string.Join("; ", _repeats);
    }

    /// <summary>
    /// Asks the reviewer about the work just done. Shared by the QuickAction path and by a DAG step,
    /// so a configured reviewer applies to both — it used to run for plan steps only, while the
    /// planner was told to prefer QuickAction, which left most ordinary requests unreviewed.
    ///
    /// Fails CLOSED: a reviewer that cannot answer has not approved anything.
    /// </summary>
    private async Task<(ReviewResult Result, ReviewMode Mode)> ReviewAsync(
        string title, List<ChatMessage> convo, ExecutionJournal journal, int evidenceStart,
        List<ArtifactRef> artifacts, IArtifactScope store,
        IChatProvider reviewProvider, string reviewModel, CancellationToken ct)
    {
        try
        {
            string[] changed;
            lock (artifacts)
                changed = artifacts.Select(a => a.RelativePath).ToArray();

            // From the journal, not from the transcript. The transcript is the model's working
            // memory: once it has to be shortened to fit the window, the tool results become a stub,
            // and the reviewer was handed less evidence with nothing saying so.
            var evidence = journal.Describe(evidenceStart);

            // Which question can even be asked about this step? A step that RAN something is judged
            // on whether it ran and succeeded. A step that only WROTE something has no exit code to
            // check, so execution review passes anything — which is how a guide full of invented
            // package names and made-up command syntax finished green. There, the content itself is
            // the only thing there is to review.
            //
            // What it wrote is read back from the STORE, not scraped out of the conversation. The
            // conversation is a poor source for it twice over: only write_file was ever recognised
            // there, so an edit or a move was reviewed as if nothing had happened, and once the
            // transcript has to be shortened to fit the window the arguments are gone. Reading the
            // file also means the reviewer judges what is actually on disk rather than what the
            // model said it would put there.
            var written = await ReadWrittenAsync(store, ct);
            var mode = _reviewContent && !journal.UsedAny(CommandTools, evidenceStart) && written.Count > 0
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
    /// Runs the criteria this run was given, if any.
    ///
    /// <para>They are the answer to the question the engine could not answer before: everything it
    /// had to decide "is this done" went through a language model — the worker's closing sentence,
    /// and a reviewer judging free text, which on 2026-09-07 failed a correct run over a defect it
    /// had invented complete with a line number. An exit code does not confabulate.</para>
    /// </summary>
    private async Task<SuccessReport> CheckSuccessAsync(
        Guid taskId, Guid runId, WorkContext context, CancellationToken ct)
    {
        if (_successCriteria.Count == 0)
            return SuccessReport.NothingToCheck;

        var toolContext = new ToolContext(
            TaskId: taskId,
            RunId: runId,
            WorkspaceId: _workspace.Id,
            Context: context,
            PermissionPolicy: _policy,
            WorkspaceRoot: _workspace.RootPath,
            Artifacts: _artifacts,
            Services: _services);

        return await _successEvaluator.EvaluateAsync(
            _successCriteria, _tools, _permissions, _policy, _decisions, toolContext, taskId, ct);
    }

    /// <summary>Per file, and in total — the same budget the reviewer prompt applies.</summary>
    private const int MaxReviewFileChars = 8000;

    /// <summary>
    /// What this step actually changed, as it stands now: the store's own record of the paths, and
    /// the current content of each. A path the step removed is reported as such rather than
    /// silently skipped — "this file is gone" is exactly the sort of thing a reviewer should see.
    /// </summary>
    private async Task<IReadOnlyList<WrittenFile>> ReadWrittenAsync(
        IArtifactScope store, CancellationToken ct)
    {
        var written = new List<WrittenFile>();

        foreach (var path in store.TouchedPaths)
        {
            string? content;
            try
            {
                // A staged run holds the proposal in memory; a direct one has it on disk.
                content = await store.TryReadPendingAsync(path, ct);
                if (content is null)
                {
                    var full = WorkspaceGuard.ResolveInside(_workspace.RootPath, path);
                    content = File.Exists(full) ? await File.ReadAllTextAsync(full, ct) : null;
                }
            }
            catch (Exception)
            {
                // Unreadable is not the same as unwritten, and neither is worth failing the review
                // over: say what is known and let the reviewer judge with it.
                content = null;
            }

            // The real size travels with the excerpt. Cutting here and saying nothing is what let a
            // 414-line page be reviewed as its first 161 lines as though that were the whole thing.
            var shown = content is null
                ? "(this file was removed, or could not be read back)"
                : content.Length > MaxReviewFileChars ? content[..MaxReviewFileChars] : content;

            written.Add(new WrittenFile(path, shown, content?.Length ?? shown.Length));
        }

        return written;
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
        // The step's own view of the artifact store. Tools write through THIS, never through the
        // store itself, so every file they touch is attributed to the step that asked for it and
        // can be undone without reaching into a concurrent step's work.
        IArtifactScope store,
        // What this step actually did, recorded as it happens rather than read back out of the
        // conversation afterwards.
        ExecutionJournal journal,
        int? stepNo, ToolLoopResult loopResult,
        // The run's budget. The loop reports its own tokens, so this is where execution spending is
        // counted; it is never CHECKED in here - see RunBudget on why limits bite between steps.
        // Named runBudget because this method already has a `budget` of its own: the room left in
        // the model's context window, which is a different thing entirely.
        RunBudget runBudget,
        [EnumeratorCancellation] CancellationToken ct,
        // Only for the usage record. The loop is handed a ready provider and a model NAME, which is
        // all it needs to talk; the id is what makes the tokens attributable afterwards.
        string? providerId = null)
    {
        // An async iterator cannot return a value, so the caller passes in the slot the loop fills.
        // Without it "how did this end" existed only as English inside an event, and every consumer
        // guessed. Pessimistic until proven otherwise: falling out of the loop means the iteration
        // cap was reached, which is not success.
        loopResult.Set(StepOutcomeKind.Incomplete,
            $"ran for {RunawayCeiling} turns without a final answer");

        WorkEvent Ev(EventKind kind, string summary)
            => new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, kind, summary,
                   stepNo is { } n ? $"{{\"step\":{n}}}" : null);

        WorkEvent Usage(int prompt, int completion)
        {
            runBudget.TokensUsed(prompt, completion);
            return new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, EventKind.UsageReported,
                       $"tokens: {prompt} in, {completion} out"
                       + (providerId is { Length: > 0 } id ? $" ({id}/{model}, execute)" : ""),
                       WorkEventPayload.UsagePayload(prompt, completion, stepNo, providerId, model,
                                                     WorkEventPayload.WorkPurpose.Execute));
        }

        // A reply that describes a call instead of making one earns exactly ONE re-ask per step; without
        // the cap a model that keeps explaining itself would burn every iteration on the same nudge.
        var repairRequested = false;
        var openFailures = new OpenFailures();

        // How this model's prompt tokens relate to transcript characters, measured as the step runs.
        var scale = new TokenScale();

        // What the last turn's prompt actually cost, so a cut-off can be explained with the real
        // number instead of a guess about which budget ran out.
        int? lastPromptTokens = null;

        // What the last turn GENERATED. A turn that spent tokens and delivered neither text nor a
        // tool call is the signature of output that never reached us, and it is the difference
        // between "the model had nothing to say" and "the model's answer was lost".
        int? lastCompletionTokens = null;

        // Whether anything at all has been executed in this step, which decides what an empty
        // closing turn MEANS: after real work it is a missing sentence, before any it is silence.
        var actionsTaken = 0;

        var toolDefs = _tools.Definitions.Where(d => Allows(worker, d.Name)).ToArray();

        // The tool schemas are sent with every request and are not part of the message list, so they
        // have to be counted separately or the estimate is short by a constant few thousand
        // characters - exactly the margin that decides whether the last turn fits.
        var toolsOverhead = toolDefs.Sum(d => d.Name.Length + d.Description.Length + d.JsonSchema.Length + 16);

        // Repetition, counted. Not turns - see StallLimit.
        var progress = new StepProgress();

        for (var iteration = 1; iteration <= RunawayCeiling; iteration++)
        {
            var request = new ChatRequest(model, messages, toolDefs, Temperature: 0.2, NumCtx: _numCtx, Think: _think);

            // Does this provider apply a hard window to prompt AND generation together? Only Ollama
            // answers; a cloud provider returns null and none of what follows applies to it, because
            // its max_tokens caps the answer and says nothing about how long the transcript may be.
            if (provider.ContextWindow(request) is { } window && window > 0)
            {
                // Room kept back for the model's own answer. Without it the transcript is allowed to
                // fill the window completely and the reply is cut off mid-token - which is what
                // happened: 8174 prompt tokens of 8192, and 18 left to answer with.
                // Never more than half the window: on a small one a fixed floor of 256 would leave
                // nothing to talk with, and the guard would refuse every request instead of any.
                var reserve = Math.Min(Math.Clamp(window / 8, 256, 2048), window / 2);
                var budget = window - reserve;
                var sizeNow = Transcript.Size(messages) + toolsOverhead;

                if (scale.TokensFor(sizeNow) > budget)
                {
                    var elided = Transcript.Elide(messages, scale.CharsFor(budget) - toolsOverhead);
                    sizeNow = Transcript.Size(messages) + toolsOverhead;

                    if (elided > 0)
                        yield return Ev(EventKind.ContextTrimmed,
                            $"Context window nearly full — dropped the contents of {elided} earlier tool "
                            + $"message(s) to make room (about {scale.TokensFor(sizeNow)} of {window} tokens now).");

                    // Trimming had nothing left to give and the transcript still does not fit. Stop
                    // here rather than send it: the provider would answer with a fragment, and a
                    // fragment of a tool call is indistinguishable from a model that lost its way.
                    if (scale.TokensFor(sizeNow) > budget)
                    {
                        var reason =
                            $"the context window is full: this turn needs about {scale.TokensFor(sizeNow)} "
                            + $"of the {window} tokens this model was given (num_ctx), and there is nothing "
                            + "left to trim";
                        loopResult.Set(StepOutcomeKind.Incomplete, reason);
                        yield return Ev(EventKind.ErrorObserved,
                            char.ToUpperInvariant(reason[0]) + reason[1..]
                            + ". Raise num_ctx in Settings, or use a model with a larger window.");
                        yield break;
                    }
                }
            }

            // Measured against what this request actually is, so the next estimate uses the model's
            // real ratio rather than the pessimistic default.
            var sizeAtRequest = Transcript.Size(messages) + toolsOverhead;

            var contentBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
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

                    // Kept apart from the content on purpose: it is a draft, not an answer, and it
                    // never enters the transcript. It exists so a turn that produced ONLY reasoning
                    // can be diagnosed instead of arriving as an inexplicable silence.
                    case ReasoningDelta reasoning:
                        reasoningBuilder.Append(reasoning.Text);
                        break;

                    case FinishDelta finish:
                        finishReason = finish.Reason;
                        break;

                    // What the turn cost. It was dropped here, which is why nothing downstream -
                    // the status tile, the run record - could ever say. Providers report totals per
                    // turn, not increments, so each turn is one event and the run adds them up.
                    case UsageDelta usage:
                        lastCompletionTokens = usage.CompletionTokens;
                        // Also the one honest measurement of how this model tokenizes: the same
                        // transcript, in characters and in the provider's own count.
                        if (usage.PromptTokens is { } prompted)
                        {
                            lastPromptTokens = prompted;
                            scale.Observe(sizeAtRequest, prompted);
                        }
                        yield return Usage(usage.PromptTokens ?? 0, usage.CompletionTokens ?? 0);
                        break;
                }
            }

            // The turn was cut off at the token limit. Record only the partial text (dropping any
            // half-finished tool call, which would dangle without a tool_result and break the next
            // provider call) and stop this step — otherwise the model re-issues the same truncated call
            // every iteration until the ceiling, burning the run (seen with a reasoning model whose
            // thinking exhausted max_tokens before the tool arguments were emitted).
            if (finishReason is "max_tokens" or "length")
            {
                if (contentBuilder.Length > 0)
                    messages.Add(new ChatMessage(ChatRole.Assistant, contentBuilder.ToString(), null));
                // Which budget ran out? For Ollama the two are the same number - num_ctx covers
                // prompt AND generation - so a prompt that nearly fills the window produces exactly
                // this, and telling the user to raise max_tokens sends them to a setting that does
                // not exist for their provider. Say which one it was, from what was measured.
                var hardWindow = provider.ContextWindow(request);
                var squeezed = hardWindow is { } w && lastPromptTokens is { } used && used > w * 4 / 5;

                loopResult.Set(StepOutcomeKind.Incomplete,
                    squeezed
                        ? $"the context window filled up: {lastPromptTokens} of {hardWindow} tokens went to the "
                          + $"prompt, leaving no room to answer (finish={finishReason})"
                        : $"the model's output was cut off at the token limit (finish={finishReason})");

                yield return Ev(EventKind.ErrorObserved,
                    squeezed
                        ? $"The context window filled up: the prompt used {lastPromptTokens} of the {hardWindow} "
                          + $"tokens this model was given (num_ctx), leaving no room to answer "
                          + $"(finish={finishReason}); stopping this step. Raise num_ctx in Settings, or use "
                          + "a model with a larger window."
                        : $"Model output was cut off at the token limit (finish={finishReason}); stopping "
                          + "this step. Raise the provider's max output tokens, or use a model that doesn't "
                          + "spend the whole budget on reasoning.");
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

                // NOTHING came back. Not an answer, not a call - and this used to fall through to
                // "genuine final answer" below and mark the step SUCCEEDED. The reviewer then failed
                // it for the only thing it could see ("no tools were run and no files were
                // changed"), the retry produced the same silence, and the run died with a message
                // about the reviewer while the cause - the model's output never arrived - appeared
                // nowhere. An absence is not an answer, which is the same rule as everywhere else
                // here; this was the last place still breaking it.
                if (replyText is null && actionsTaken == 0)
                {
                    var thought = reasoningBuilder.Length;
                    var spent = lastCompletionTokens is { } t and > 0 ? $" while reporting {t} output token(s)" : "";

                    // Only when the prompt is actually near the window. Suggesting num_ctx to somebody
                    // whose prompt used 1786 of 131072 tokens sends them to tune a setting that has
                    // nothing to do with it, which is how a diagnosis becomes a list of everything it
                    // might be.
                    var declaredWindow = provider.ContextWindow(request);
                    var tight = declaredWindow is { } w && lastPromptTokens is { } used && used > w * 4 / 5
                        ? $" The prompt also used {used} of this model's {w} tokens, so raising num_ctx may help."
                        : "";

                    var why = thought > 0
                        ? $"The model spent the whole turn reasoning ({thought:N0} characters of it) and "
                          + "produced no answer and no tool call. Turn Thinking off in Settings, or use a "
                          + "model that answers as well as reasons." + tight
                        // What was OBSERVED first, then the causes - and reasoning is named as ruled
                        // out rather than led with, because the provider reported none and saying
                        // "a reasoning model does this" over evidence to the contrary is the habit
                        // the rest of this engine exists to break.
                        : $"The model returned nothing{spent} — no text, no tool call, and no reasoning "
                          + "either — so its output never reached the engine. The usual cause is a model "
                          + "that cannot emit tool calls in the format the provider expects: a small "
                          + "local model often answers with something the provider then drops. Tick "
                          + "\"Capture raw wire (Trace)\" in the log window and run it again to see "
                          + "exactly what came back, or use a model known to call tools." + tight;

                    yield return Ev(EventKind.ErrorObserved, why);
                    loopResult.Set(StepOutcomeKind.Failed, why);
                    yield break;
                }

                // A final answer only settles the step if the actions behind it actually worked. The
                // model saying "Done" over a failed read is the exact shape the follow-up review
                // caught reporting green.
                if (openFailures.Count > 0)
                {
                    var unresolved = openFailures.Describe();

                    // Two different things end a step here, and saying which one is the difference
                    // between a person fixing a broken command and a person checking a path.
                    yield return Ev(EventKind.ErrorObserved, openFailures.NothingButMisses
                        ? $"Finished with nothing done: all {openFailures.Count} lookup(s) this step "
                          + "made found nothing, and nothing else was tried: " + unresolved
                        : $"Finished without resolving {openFailures.Count} tool call(s) that did not "
                          + "go through: " + unresolved);

                    loopResult.Set(StepOutcomeKind.Incomplete, openFailures.NothingButMisses
                        ? "nothing found and nothing done: " + unresolved
                        : "unresolved tool call: " + unresolved);
                    yield break;
                }

                loopResult.Set(StepOutcomeKind.Succeeded, null);
                yield break; // genuine final answer - no tool calls
            }

            // Did this turn do anything the step had not already done? A stuck model does not stop
            // calling tools - it calls the SAME one, with the same arguments, until something else
            // stops it. That is the shape worth detecting, and unlike a turn count it does not grow
            // with the size of the job: seven new files are seven turns of progress, while one file
            // read three times is three turns of nothing however big the project is.
            var advanced = progress.Advanced(toolCalls);
            if (!advanced && progress.Stalled >= StallLimit)
            {
                var repeated = progress.Describe();
                loopResult.Set(StepOutcomeKind.Incomplete,
                    $"stopped after {StallLimit} turns that only repeated earlier tool calls: {repeated}");
                yield return Ev(EventKind.ErrorObserved,
                    $"The model spent {StallLimit} turns repeating tool calls it had already made "
                    + $"({repeated}) without doing anything new; stopping this step. It is stuck rather "
                    + "than slow — the turn count is not the limit here.");
                yield break;
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
                    // Counted as an open failure, exactly like a tool that ran and failed. A refusal
                    // used to reach only the transcript, so a model that was denied the one action
                    // the request needed could still finish with "done" and the run reported
                    // Completed — a permission system whose whole effect was a sentence nobody
                    // checked. Not permitted is not performed.
                    openFailures.Failed(call, $"not available to the {worker.Role} role");
                    journal.Record(stepNo, call.Name, Compact(call.ArgumentsJson), ActionOutcome.Refused,
                                   $"not available to the {worker.Role} role");
                    yield return Ev(EventKind.DecisionResolved, $"{call.Name}: not available to role '{worker.Role}'");
                    messages.Add(ChatMessage.Tool(call.Id, $"ERROR: tool '{call.Name}' is not available to the {worker.Role} role."));
                    continue;
                }

                // ── Permission gate: allow / ask / deny ──────────────────────
                var gate = _permissions.Evaluate(
                    EffectivePolicyFor(worker), call.Name, _tools.RequiredLevelOf(call.Name));
                if (gate == PermissionDecision.Allow && _tools.RequiresApprovalOf(call.Name))
                    gate = PermissionDecision.Ask;
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
                            Subject: _tools.RequiresApprovalOf(call.Name) ? null : call.Name,
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
                        // Same reason as the role gate above: a denial that only appears in the
                        // transcript lets the step finish green over an action that never happened.
                        var why = gate == PermissionDecision.Ask
                            ? "the user did not permit this action"
                            : "blocked by the permission policy";
                        openFailures.Failed(call, why);
                        journal.Record(stepNo, call.Name, Compact(call.ArgumentsJson),
                                       ActionOutcome.Refused, why);

                        // The model is told WHICH refusal this was. Both used to arrive as "the user
                        // did not permit this action" - the distinction was computed, recorded in the
                        // journal, and then thrown away on the one path where it changes behaviour.
                        // A model told a person refused it stops and apologises, which is right; a
                        // model told a tool is off for this run should stop asking for that tool and
                        // do the job another way, and it could not tell the two apart.
                        messages.Add(ChatMessage.Tool(call.Id, gate == PermissionDecision.Ask
                            ? "ERROR: the user did not permit this action."
                            : $"ERROR: the tool '{call.Name}' is not permitted for this run and will "
                              + "not become permitted. Do not call it again. If another permitted tool "
                              + "can do the same job, use that instead; if none can, say what you "
                              + "could not do and why."));
                        continue;
                    }
                }

                actionsTaken++;
                yield return Ev(EventKind.ToolInvoked, $"{call.Name} {Compact(call.ArgumentsJson)}");

                var toolContext = new ToolContext(
                    TaskId: taskId,
                    RunId: runId,
                    WorkspaceId: _workspace.Id,
                    Context: context,
                    PermissionPolicy: EffectivePolicyFor(worker),
                    WorkspaceRoot: _workspace.RootPath,
                    Artifacts: store,
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
                {
                    openFailures.Succeeded(call, result.Artifacts);

                    // The tree just moved. A read or a build that comes after this is not the one
                    // that came before it, whatever its arguments say.
                    if (MutatingTools.Contains(call.Name))
                        progress.WorkspaceChanged();
                }
                else if (result.IsAnswer)
                    openFailures.FoundNothing(call, result.Error);
                else
                    openFailures.Failed(call, result.Error);

                // What a failure SAYS: the error, and the output under it when there is one. Built
                // once, here, because the model and the reviewer each get a copy and on 2026-09-07
                // 23:42 they got different ones. The model was told "Command exited with code
                // -532462766" and, beneath it, the stderr: "Unhandled exception.
                // System.ArgumentException: HTML cannot be null or empty" - the very crash the step
                // was there to cause. The journal recorded the first line only. So the reviewer,
                // handed evidence with no exception in it, read the agent's true account of the
                // crash and called it fabricated. The failure-carries-its-output fix below had
                // been made for the transcript alone; the evidence is the record that gets judged.
                var failure = string.IsNullOrWhiteSpace(result.Output)
                    ? result.Error
                    : $"{result.Error}\n{result.Output}";

                // The evidence, written down at the moment it exists. Nothing that shortens the
                // prompt afterwards can take it away.
                journal.Record(
                    stepNo, call.Name, Compact(call.ArgumentsJson),
                    result.Success ? ActionOutcome.Succeeded
                        : result.IsAnswer ? ActionOutcome.Answered
                        : ActionOutcome.Failed,
                    result.Success ? result.Output : failure);

                yield return result.Success
                    ? Ev(EventKind.ToolResult, $"{call.Name} -> ok: {result.Output}")
                    : Ev(EventKind.ToolResult, $"{call.Name} -> failed: {result.Error}");

                foreach (var reference in result.Artifacts)
                {
                    lock (artifacts)
                        artifacts.Add(reference);
                    yield return new WorkEvent(
                        Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, EventKind.ArtifactProduced,
                        $"{reference.Kind}: {reference.RelativePath}",
                        WorkEventPayload.ArtifactPayload(
                            reference.Kind.ToString(), reference.RelativePath, stepNo));
                }

                // A FAILURE carries its output too. This dropped it: the model was told
                // "ERROR: git exited with code 1." and nothing else, while the output holding
                // "git: 'diff HEAD' is not a git command" sat in the journal, in the log and on
                // screen - visible to everyone except the only party that could act on it.
                //
                // On 2026-09-07 that cost a run 18 git calls in ten different formulations, none of
                // which could work, and ended with a review of a diff the model never saw. The same
                // class as every other defect this month: the record somebody works from is
                // shortened, and nothing says so.
                var reply = result.Success
                    ? (result.Output ?? "OK")
                    : $"ERROR: {failure}";

                // A repeat is executed and answered like any call - but the model is TOLD it is
                // one. The stall detector knew from the first repeat; the model learned nothing
                // until the third, when the step was already over. On 2026-09-07 23:34 a model
                // fixed one build error, hit the next, and spent its remaining three turns reading
                // the same file and running the same build with nothing changed between them,
                // each result identical to the last and nothing saying so. Whether a nudge would
                // have moved it is not knowable; that it was owed one is.
                if (!advanced)
                    reply += progress.Stalled >= StallLimit - 1
                        ? "\n\n[This is a call you have already made in this step, and it is being "
                          + "counted as no progress. One more turn that only repeats earlier calls "
                          + "and this step will be stopped as stuck. Change something, or say what "
                          + "you are stuck on and stop.]"
                        : "\n\n[This is a call you have already made in this step, and it is being "
                          + "counted as no progress. If you know what to change, change it now; if "
                          + "you are finished, say so.]";

                messages.Add(ChatMessage.Tool(call.Id, reply));
            }
        }

        // The backstop, reached only by a step that kept finding genuinely new things to do for
        // longer than any real task does. Worth saying plainly rather than as "did not converge".
        yield return Ev(EventKind.ErrorObserved,
            $"This step ran {RunawayCeiling} turns and never finished. It was still doing new things "
            + "each turn, so it is not stuck in a loop — but nothing this long is going to plan. "
            + "Stopping it.");
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
        || worker.ToolAllowlist.Contains(tool, StringComparer.OrdinalIgnoreCase)
        || (tool.StartsWith("mcp__", StringComparison.Ordinal)
            && worker.ToolAllowlist.Any(pattern => pattern.StartsWith("mcp__", StringComparison.Ordinal)
                && pattern.EndsWith('*') && tool.StartsWith(pattern[..^1], StringComparison.Ordinal)));


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
    /// <returns>
    /// Whether the rejected attempt was DISCARDED from the transcript. The caller needs this to keep
    /// the evidence window and the transcript window the same length — see the note at the call site.
    /// </returns>
    private static bool RetryAfterReview(
        List<ChatMessage> convo, int conversationStart, ReviewMode mode, string notes, string what)
    {
        var discarded = mode == ReviewMode.Content;

        if (discarded && convo.Count > conversationStart)
            convo.RemoveRange(conversationStart, convo.Count - conversationStart);

        convo.Add(ChatMessage.User(
            $"A reviewer rejected the previous attempt with this feedback: {notes}\n"
            + (discarded
                ? $"That attempt has been discarded. Redo {what} from scratch, correcting every point above."
                : $"Please fix the issues and redo {what}.")));

        return discarded;
    }

    /// <summary>
    /// Puts back what the rejected work produced, and takes it out of the run's artifact list so the
    /// summary does not go on claiming files that are no longer there.
    /// </summary>
    private static async Task<RevertReport> RevertAsync(
        IArtifactScope store, List<ArtifactRef> artifacts, CancellationToken ct)
    {
        // What this step touched, from the store's own record - not from the conversation, which
        // knew only about write_file and lost even that when the transcript had to be shortened.
        var written = store.TouchedPaths;
        if (written.Count == 0)
            return RevertReport.Empty;

        RevertReport report;
        try
        {
            report = await store.RevertAsync(written, ct);
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

        // The reason per path, not one sentence over all of them. "It changed after the step wrote
        // it" was the only reason there used to be; a path can also be kept because another step
        // wrote it afterwards, or because nothing this step did to it is on record - and telling
        // someone the wrong reason for work left in place is worse than telling them none.
        if (report.Kept.Count > 0)
            yield return "Left as it is: "
                       + string.Join(", ", report.Kept.Select(
                           path => report.WhyKept(path) is { } why ? $"{path} ({why})" : path));
    }

    /// <summary>
    /// Tools that make something happen outside the workspace's files. Which question the reviewer
    /// is asked turns on this: a step that ran one has a real exit code to be judged on, a step that
    /// only wrote text does not.
    /// </summary>
    private static readonly HashSet<string> CommandTools =
        new(StringComparer.OrdinalIgnoreCase) { "run_command", "run_powershell", "git", "docker" };



    /// <summary>Accumulates a streamed tool call across deltas.</summary>
    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
