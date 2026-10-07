using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Core.Diagnostics;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Orchestration;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using Enactive.Providers;
using Enactive.Tools;
using Enactive.Tools.Mcp;
using Enactive.Workspace;

namespace Enactive.Settings;

/// <summary>
/// What a run needs from the application that is not about any one run: the providers, the tools,
/// the models, and the settings as they stood when the run was started.
///
/// <para>Taken as a snapshot on the UI thread and then treated as frozen. A run reads it minutes
/// later, on a thread pool thread, by which time the slider has moved and the settings dialog has
/// been through two round trips - so reading these live would mean a run whose permissions changed
/// underneath it and a history that could not say what it had been allowed to do.</para>
///
/// <para><see cref="Policy"/>, <see cref="RunSettings"/> and <see cref="WorkerId"/> are the three
/// that belong to a WORKSPACE rather than to the application, and the caller is what decides which
/// workspace's. For a run started here that is the slider on screen, because the slider on screen
/// is about the folder on screen. For a run started from a phone it is emphatically not: that task
/// names a workspace of its own, and the level saved against THAT folder is the one its owner
/// chose for it.</para>
/// </summary>
/// <param name="WorkerId">The worker this host would pick when neither the run nor its template names one.</param>
/// <param name="Session">The approvals given "for this session" in this process; null for a host without a session.</param>
/// <param name="Approvals">The approvals given "for this workspace"; null for the store every host shares.</param>
public sealed record RunEnvironment(
    IChatProviderFactory Providers,
    ModelResolver Models,
    IWorkerProvider Workers,
    IToolRegistry BuiltInTools,
    IReadOnlyList<McpServerConfig> McpServers,
    Planner Planner,
    IPermissionEngine Permissions,
    IModelRouter Router,
    LogHub Log,
    AppSettings Settings,
    PermissionPolicy Policy,
    RunSettings RunSettings,
    string? WorkerId,
    SessionApprovals? Session = null,
    ApprovalStore? Approvals = null)
{
    public RunEngineOptions EngineOptions { get; } = RunEngineOptions.Capture(Settings);
}

/// <summary>What one host asks to be run.</summary>
/// <param name="Source">Where the run was started - which decides who can be asked, and so what the run may do.</param>
/// <param name="TaskId">
/// The task this run belongs to, when it is carrying one on: a task that stopped at a question and is
/// started again once it is answered must be the same task, or the recorded answer cannot find its
/// question. Null for a new task.
/// </param>
/// <param name="Resume">An interrupted run to carry on from its last step boundary.</param>
/// <param name="Spec">The saved task this run is, with its permissions, checks, limits and role.</param>
/// <param name="WorkerId">A worker named for this invocation; it wins over the template's and the host's.</param>
/// <param name="Stage">Hold the run's changes for somebody to apply, instead of writing them.</param>
/// <param name="Remembered">
/// False when the host's decision handler is an explicit answer for this invocation (the console's
/// <c>--approve</c>): that answer was given on purpose, and a standing approval must not overrule it.
/// </param>
public sealed record RunRequest(
    WorkspaceInfo Workspace,
    string Prompt,
    IntentSource Source,
    Guid? TaskId = null,
    RunCheckpoint? Resume = null,
    ResolvedTaskSpec? Spec = null,
    string? WorkerId = null,
    bool Stage = false,
    bool Remembered = true);

/// <summary>
/// One composed run, ready to start: the engine, the intent, where its changes go, and the things
/// opened along the way that have to be closed again when it ends.
/// </summary>
/// <param name="Engine">The orchestrator with the run recorder and the log tap already around it.</param>
/// <param name="Artifacts">Where the run's changes go - staged for somebody to apply, or straight to disk.</param>
/// <param name="Resources">
/// The MCP tool servers this run connected to. They are child processes, and a caller that forgets
/// them leaves one set per run running until the application is closed.
/// </param>
public sealed record ComposedRun(
    IOrchestrator Engine,
    Intent Intent,
    IArtifactStore Artifacts,
    RunCheckpoint? Resume,
    IAsyncDisposable Resources) : IAsyncDisposable
{
    /// <summary>
    /// The run's events: the request submitted, or the interrupted run carried on - with a context
    /// built now, because what is on this machine is a fact about now and not about the run that stopped.
    /// </summary>
    public IAsyncEnumerable<WorkEvent> Events(CancellationToken ct)
        => Resume is null
            ? Engine.SubmitIntentAsync(Intent, ct)
            : Engine.ResumeRunAsync(Resume, Intent.Context, ct);

    public ValueTask DisposeAsync() => Resources.DisposeAsync();
}

/// <summary>
/// Assembles every run, wherever it was started: the window, a background run, a phone, the console.
///
/// <para><b>Why one.</b> A run used to be assembled by hand in three places - the window, the
/// unattended path and the console - and the copies drifted apart where nobody looked: the console
/// connected no MCP servers, recorded its runs without their settings and never honoured a remembered
/// approval; a template run in the background quietly dropped its template; a background or console
/// resume ran under today's slider instead of the permissions it was started with. Each was a run that
/// worked and quietly was not the run somebody asked for. See Docs/adr/0001-one-run-composer.md.</para>
///
/// <para><b>What a host keeps.</b> Who answers its questions (the decision handler), where its events
/// go (the window, the Inbox, a phone, stdout) and how it is cancelled. Those really differ; everything
/// else is the same run.</para>
/// </summary>
public static class RunComposer
{
    /// <summary>
    /// Why this request cannot be run as asked, or null when it can. Asked by hosts before they start
    /// anything, so the refusal is shown where the request was made; <see cref="ComposeAsync"/> refuses
    /// the same way.
    /// </summary>
    public static string? Refusal(RunRequest request)
        // Staging that survives a run nobody is watching needs a store that persists its proposals,
        // and there is not one. Running anyway would write to the files directly while the history
        // said the changes were staged - and nobody is at the machine to notice the difference.
        => request.Stage && !Attended(request)
            ? "Stage changes is on, and a run nobody is watching cannot stage: it would write to the files "
              + "directly while the history claimed the changes were staged. Turn Stage changes off for this "
              + "workspace, or run the task in the foreground on the desktop."
            : null;

    /// <summary>Connects the tools and builds the engine and the intent for one run.</summary>
    /// <param name="decisions">Who answers this host's permission requests - the one part each host supplies.</param>
    /// <exception cref="InvalidOperationException">The request is refused - see <see cref="Refusal"/>.</exception>
    public static async Task<ComposedRun> ComposeAsync(
        RunEnvironment environment, RunRequest request, IDecisionHandler decisions, CancellationToken ct)
    {
        if (Refusal(request) is { } refused)
            throw new InvalidOperationException(refused);

        var workspace = request.Workspace;
        var spec = request.Spec;
        var memory = MemoryStoreFactory.Create(workspace);
        var tools = await McpRunTools.ConnectAsync(
            environment.BuiltInTools, environment.McpServers, workspace.RootPath, ct);

        // Said once per run, whether or not anything calls them: starting a server is a cost the run has
        // already paid, and a scheduled run is where it costs most and shows least.
        environment.Log.Info(LogSource.Tool, tools.Summary());

        try
        {
            // A resumed run never stages, whatever was asked. Its earlier steps wrote straight to disk -
            // that is the only kind of run that is ever checkpointed - so staging the rest would put half
            // of one piece of work behind a review gate and leave the other half applied.
            var staged = request.Stage && request.Resume is null;
            IArtifactStore artifacts = staged ? new StagingArtifactStore(workspace.RootPath) : new DiskArtifactStore(workspace);

            // What governs the run, and what its history says governed it. A resumed run continues under
            // what it was started with: the slider will have moved by now - it is a control, not a record
            // - and a run that finishes its remaining steps under permissions nobody granted it is not the
            // run somebody asked to resume.
            var runSettings = request.Resume?.Settings ?? environment.RunSettings with { Staged = staged };
            var policy = request.Resume?.Settings is { } was
                ? EngineComposition.PolicyFor(environment.Settings, was.Autonomy)
                // A template's permissions are already the INTERSECTION of its own ceiling and the
                // workspace's tier (TemplateResolution.Narrow), so this is never more than the slider allows.
                : spec?.Permissions ?? environment.Policy;
            // A run started from the web never runs a shell, and this is where that is true. The decision
            // handler refuses one too, but a handler only sees what the policy decided to ASK about - and at
            // the Autonomous tier the policy asks about nothing.
            if (request.Source == IntentSource.Remote)
                policy = RemotePolicy.ForRemoteRun(policy);

            var engine = Engine(
                new RunEngineResources(environment.Providers, environment.Models, environment.Workers,
                    new LoggingToolRegistry(tools, environment.Log), artifacts, workspace, environment.Planner,
                    environment.Permissions, Answering(environment, request, decisions), policy,
                    new NoServices(), environment.Router),
                environment.EngineOptions, new JsonCheckpointStore(workspace), runSettings,
                spec?.SuccessCriteria, spec?.Limits);

            var context = await new ContextProvider(workspace, new EnvironmentProbe(), memory)
                .BuildAsync(new IntentFocus(workspace.Id), ct);

            // A worker named for this invocation, then the one the template needs, then the host's own pick.
            var workerId = request.WorkerId ?? spec?.WorkerId ?? environment.WorkerId;

            return new ComposedRun(
                new Recorded(
                    engine,
                    // The specification is recorded WITH the run, so reading it back later shows the
                    // template as it was rather than as it has since been edited.
                    new RunRecorder(RunStoreFactory.Create(workspace), memory, workspace.Id, runSettings, spec?.Snapshot()),
                    environment.Log),
                new Intent(request.Resume?.TaskId ?? request.TaskId ?? Guid.NewGuid(), request.Prompt, request.Source,
                    context, DateTimeOffset.UtcNow, workerId),
                artifacts,
                request.Resume,
                tools);
        }
        catch
        {
            // The connections were made before the thing that threw. Nobody else has been handed them
            // yet, so this is the only place that can close them.
            await tools.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The single mapping from run resources and engine switches to an orchestrator - reached directly
    /// only by tests that need to put their own parts into it.
    /// </summary>
    internal static Orchestrator Engine(RunEngineResources resources, RunEngineOptions options,
        IRunCheckpointStore? checkpoints = null, RunSettings? settings = null,
        IReadOnlyList<SuccessCriterionDefinition>? successCriteria = null, ExecutionLimits? limits = null)
        => new(new WorkspaceChangesFactory(), resources.Providers, resources.Models, resources.Workers,
            resources.Tools, resources.Artifacts, resources.Workspace, resources.Planner,
            resources.Permissions, resources.Decisions, resources.Policy, resources.Services,
            router: resources.Router, reviewRetries: options.ReviewRetries, successRetries: options.SuccessRetries,
            maxLoadedToolsPerStep: options.MaxLoadedToolsPerStep,
            proposeChecks: options.ProposeChecks, numCtx: options.NumCtx,
            generationBudgets: options.GenerationBudgets, repairConsultation: options.RepairConsultation,
            disableThinking: options.DisableThinking, maxParallelSteps: options.MaxParallelSteps,
            evidenceBudget: options.EvidenceBudget, allowImplicitToolCalls: options.AllowImplicitToolCalls,
            revertRejectedSteps: options.RevertRejectedSteps, checkpoints: checkpoints, settings: settings,
            successCriteria: successCriteria, limits: limits, agents: resources.Agents,
            // The kinds of project the engine can build for its own "no new build errors" check.
            // A new kind is a new IEcosystem here; nothing in the orchestrator changes.
            ecosystems: [new DotnetEcosystem()],
            stepOutputs: options.StepOutputs, typedCriteria: options.TypedCriteria,
            dynamicSteps: options.DynamicSteps, fanOut: options.FanOut, validateWaves: options.ValidateWaves,
            reportBlocked: options.ReportBlocked, semanticCriteria: options.SemanticCriteria);

    /// <summary>Only the window's own command bar has somebody at the screen to apply staged changes.</summary>
    private static bool Attended(RunRequest request) => request.Source == IntentSource.CommandBar;

    /// <summary>
    /// The host's decisions, behind the answers a person already gave for good - except for a phone,
    /// which must be asked afresh every time, and an explicit answer given for this invocation.
    /// </summary>
    private static IDecisionHandler Answering(RunEnvironment environment, RunRequest request, IDecisionHandler decisions)
        => request.Source == IntentSource.Remote || !request.Remembered
            ? decisions
            : new RememberedApprovals(decisions, request.Workspace.RootPath,
                environment.Approvals ?? ApprovalStore.Default, environment.Session);

    /// <summary>
    /// The orchestrator with the run's record and the log tap already around it.
    ///
    /// <para>A decorator rather than a step each caller performs, because the two callers that existed
    /// when it was written had drifted apart on exactly this: one recorded, and the one that did not
    /// looked correct - a run that ran, changed files, and then did not appear in the history at all.</para>
    /// </summary>
    private sealed class Recorded(IOrchestrator inner, RunRecorder recorder, LogHub log) : IOrchestrator
    {
        public IAsyncEnumerable<WorkEvent> SubmitIntentAsync(Intent intent, CancellationToken ct)
            => recorder.RecordAsync(inner.SubmitIntentAsync(intent, ct).TeeToLog(log, ct), ct);

        public IAsyncEnumerable<WorkEvent> ResumeRunAsync(
            RunCheckpoint checkpoint, WorkContext context, CancellationToken ct)
            => recorder.RecordAsync(inner.ResumeRunAsync(checkpoint, context, ct).TeeToLog(log, ct), ct);
    }

    /// <summary>The engine asks for a service provider and this application has none to give.</summary>
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
