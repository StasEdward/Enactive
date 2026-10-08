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

/// <summary>What one host asks to be run.</summary>
/// <param name="Source">Where the run was started - which decides who can be asked, and so what the run may do.</param>
/// <param name="TaskId">
/// The task this run belongs to, when it is carrying one on: a task that stopped at a question and is
/// started again once it is answered must be the same task, or the recorded answer cannot find its
/// question. Null for a new task.
/// </param>
/// <param name="Resume">An interrupted run to carry on from its last step boundary.</param>
/// <param name="Spec">The saved task this run is, with its permissions, checks, limits and role.</param>
/// <param name="WorkerId">A worker named for this invocation; it wins over the template's and the workspace's.</param>
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
    bool Remembered = true)
{
    /// <summary>
    /// The defaults of the workspace this run is in: its autonomy level and its worker. Required, so no host can
    /// forget to say them: a level is what decides what the run may do without asking. Whose they are is the host's
    /// to know: the slider on screen is about the folder on screen, and a task from a phone names a folder of its own
    /// (see <see cref="FromPhone"/>). The level is ignored when resuming - a resumed run keeps the level it was started
    /// under; the worker is used when neither this invocation nor the template names one.
    /// </summary>
    public required WorkspaceDefaults Defaults { get; init; }

    /// <summary>
    /// A task from a phone, governed by what is saved against THE WORKSPACE IT NAMES - its level, its worker,
    /// its staging - and not by the desktop's slider, which is about whatever folder happens to be open
    /// there. Reading the slider for both once ran a remote task at whatever permission an unrelated project
    /// was sitting at.
    /// </summary>
    public static RunRequest FromPhone(WorkspaceInfo workspace, WorkspaceEntry saved, string prompt)
        => new(workspace, prompt, IntentSource.Remote, Stage: saved.StageChanges)
        {
            Defaults = saved.Defaults
        };
}

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
    /// Why an interrupted run cannot be resumed as asked, or null when it can: a level or a worker named for the resume
    /// that is not the one the run was started with. A resumed run continues under what it was started with (see
    /// <see cref="ComposeAsync"/>), so asking for another is refused rather than ignored - quietly running under
    /// something else than was asked is wrong either way. Asking for the same is no conflict, and neither is a level
    /// given for a run that recorded none: that one is used. A refusal of any level or role with a resume broke the
    /// scheduler line written to resume after every reboot, which names the level it always ran under.
    /// </summary>
    /// <param name="autonomy">The level asked for, when one was.</param>
    /// <param name="workerId">The worker asked for, by id, when one was.</param>
    /// <param name="defaultWorkerId">Who a run that named no worker was on.</param>
    public static string? ResumeRefusal(RunCheckpoint resume, int? autonomy, string? workerId, string defaultWorkerId)
    {
        var levelDiffers = autonomy is { } asked && resume.Settings is { } started && started.Autonomy != asked;
        var workerDiffers = workerId is not null
                            && !string.Equals(workerId, resume.WorkerId ?? defaultWorkerId, StringComparison.OrdinalIgnoreCase);
        return levelDiffers || workerDiffers
            ? "A resumed run keeps the "
              + (levelDiffers && workerDiffers ? "autonomy and role" : levelDiffers ? "autonomy" : "role")
              + " it was started with"
              + (levelDiffers ? $" ({AutonomyTiers.Names[AutonomyTiers.Clamp(resume.Settings!.Autonomy)]})" : "")
              + "; ask for the same, leave it out, or start the task again instead of resuming it."
            : null;
    }

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
    /// <param name="engine">The host's current engine, built by <see cref="EngineComposition.Build"/> - nothing a host assembles.</param>
    /// <param name="decisions">Who answers this host's permission requests - the one part each host supplies.</param>
    /// <exception cref="InvalidOperationException">The request is refused - see <see cref="Refusal"/>.</exception>
    public static async Task<ComposedRun> ComposeAsync(
        ComposedEngine engine, RunRequest request, IDecisionHandler decisions, CancellationToken ct)
    {
        if (Refusal(request) is { } refused)
            throw new InvalidOperationException(refused);

        var workspace = request.Workspace;
        var spec = request.Spec;
        var memory = MemoryStoreFactory.Create(workspace);
        var tools = await McpRunTools.ConnectAsync(
            engine.BuiltInTools, engine.McpServers, workspace.RootPath, ct);

        // Said once per run, whether or not anything calls them: starting a server is a cost the run has
        // already paid, and a scheduled run is where it costs most and shows least.
        engine.Log.Info(LogSource.Tool, tools.Summary());

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
            //
            // A level outside the tiers is the nearest one - and so it is recorded: a -1 from a damaged registry
            // entry is not a request to run everything without asking, nor a level to write into the run's history.
            var autonomy = AutonomyTiers.Clamp(request.Defaults.Autonomy);
            // A worker named for this invocation, then the one the template needs, then the workspace's.
            // Each read as a name that may be an id or a role (ComposedEngine.WorkerIdFor); one that names
            // nobody leaves the engine's default, as an unknown id always did.
            var workerId = engine.WorkerIdFor(request.WorkerId ?? spec?.WorkerId ?? request.Defaults.WorkerId);
            // The history says which worker the run was ON - the template's, when it named one - and by its
            // role, which is what a person reads. It said the host's pick, which a template overrides, and in
            // the console's runs an id where the window's said a role.
            var runSettings = request.Resume?.Settings
                ?? new RunSettings(autonomy, AutonomyTiers.Describe(autonomy), engine.Workers.Get(workerId).Role, staged);
            var policy = request.Resume?.Settings is { } was
                ? EngineComposition.PolicyFor(engine.Settings, was.Autonomy)
                // A template's permissions are already the INTERSECTION of its own ceiling and the
                // workspace's tier (TemplateResolution.Narrow), so this is never more than the slider allows.
                : spec?.Permissions ?? EngineComposition.PolicyFor(engine.Settings, autonomy);
            // A run started from the web never runs a shell, and this is where that is true. The decision
            // handler refuses one too, but a handler only sees what the policy decided to ASK about - and at
            // the Autonomous tier the policy asks about nothing.
            if (request.Source == IntentSource.Remote)
                policy = RemotePolicy.ForRemoteRun(policy);

            var orchestrator = new Orchestrator(new RunEngineResources
            {
                Providers = engine.Providers,
                Models = engine.Models,
                Workers = engine.Workers,
                Router = engine.Router,
                Tools = new LoggingToolRegistry(tools, engine.Log),
                Artifacts = artifacts,
                Workspace = workspace,
                WorkspaceChanges = new WorkspaceChangesFactory(),
                Planner = engine.Planner,
                Permissions = engine.Permissions,
                Decisions = Answering(engine, request, decisions),
                Policy = policy,
                // The kinds of project the engine can build for its own "no new build errors" check.
                // A new kind is a new IEcosystem here; nothing in the orchestrator changes.
                Ecosystems = [new DotnetEcosystem()],
                SuccessCriteria = spec?.SuccessCriteria ?? [],
                Limits = spec?.Limits ?? ExecutionLimits.None,
                Checkpoints = new JsonCheckpointStore(workspace),
                Settings = runSettings
            }, engine.EngineOptions);

            var context = await new ContextProvider(workspace, new EnvironmentProbe(), memory)
                .BuildAsync(new IntentFocus(workspace.Id), ct);

            return new ComposedRun(
                new Recorded(
                    orchestrator,
                    // The specification is recorded WITH the run, so reading it back later shows the
                    // template as it was rather than as it has since been edited.
                    new RunRecorder(RunStoreFactory.Create(workspace), memory, workspace.Id, runSettings, spec?.Snapshot()),
                    engine.Log),
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

    /// <summary>Only the window's own command bar has somebody at the screen to apply staged changes.</summary>
    private static bool Attended(RunRequest request) => request.Source == IntentSource.CommandBar;

    /// <summary>
    /// The host's decisions, behind the answers a person already gave for good - except for a phone,
    /// which must be asked afresh every time, and an explicit answer given for this invocation.
    /// </summary>
    private static IDecisionHandler Answering(ComposedEngine engine, RunRequest request, IDecisionHandler decisions)
        => request.Source == IntentSource.Remote || !request.Remembered
            ? decisions
            : new RememberedApprovals(decisions, request.Workspace.RootPath,
                engine.Approvals ?? ApprovalStore.Default, engine.Session,
                // Only the window's command bar and the console's have somebody at the screen.
                watched: Attended(request));

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
}
