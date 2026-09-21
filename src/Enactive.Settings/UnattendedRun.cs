using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Core.Diagnostics;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Orchestration;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
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
    string? WorkerId);

/// <summary>
/// One composed run, ready to submit: the engine to run it with, the intent to run, and the things
/// opened along the way that have to be closed again when it ends.
/// </summary>
/// <param name="Resources">
/// The MCP tool servers this run connected to. They are child processes, and a caller that forgets
/// them leaves one set per run running until the application is closed.
/// </param>
public sealed record ComposedRun(
    IOrchestrator Engine,
    Intent Intent,
    IAsyncDisposable Resources);

/// <summary>
/// Assembles a run nobody is sitting in front of.
///
/// <para>This exists because there are now two of them - the background runs started from the
/// composer and the runs started from a phone - and the interesting thing about an unattended run
/// is not the assembly but the decision handler, which is the one part each caller supplies. The
/// rest was assembled twice, and the second copy was already drifting: it is easy to build a run
/// that works and quietly does not record itself, which shows up as a run that ran, changed files,
/// and then does not appear in the history at all.</para>
///
/// <para>The interactive path is deliberately not routed through here. It streams into the window
/// as it goes and owns its own cancellation, and pulling it in would mean parameterising this on
/// the differences rather than sharing what is actually the same.</para>
/// </summary>
public static class UnattendedRun
{
    /// <summary>
    /// Connects the tools and builds the engine and the intent for one run.
    ///
    /// <para>The returned engine already records and logs: <see cref="ComposedRun.Engine"/> is the
    /// orchestrator with the run recorder and the log tap wrapped around it, so a caller cannot
    /// forget them. That is why the recording is here and not left to each caller - a remote run
    /// missing from the desktop's run list is not a cosmetic difference, it is the only account of
    /// what was done to the files.</para>
    /// </summary>
    /// <param name="decisions">
    /// Who answers permission requests for this run. It is the whole difference between the
    /// callers: the background path files the question in the inbox and denies, the remote path
    /// races the desktop against the phone.
    /// </param>
    public static async Task<ComposedRun> ComposeAsync(
        RunEnvironment environment,
        WorkspaceInfo workspace,
        string prompt,
        IntentSource source,
        IDecisionHandler decisions,
        CancellationToken ct)
    {
        var memory = MemoryStoreFactory.Create(workspace);
        var tools = await McpRunTools.ConnectAsync(
            environment.BuiltInTools, environment.McpServers, workspace.RootPath, ct);

        try
        {
            var settings = environment.Settings;

            // A run started from the web never runs a shell, and this is where that is true. The
            // decision handler refuses one too, but a handler only sees what the policy decided to
            // ASK about - and at the Autonomous tier the policy asks about nothing, so a remote
            // task in a workspace saved at that tier ran PowerShell with the rule looking enforced.
            var policy = source == IntentSource.Remote
                ? RemotePolicy.ForRemoteRun(environment.Policy)
                : environment.Policy;

            var orchestrator = new Orchestrator(
                environment.Providers,
                environment.Models,
                environment.Workers,
                new LoggingToolRegistry(tools, environment.Log),
                new DiskArtifactStore(workspace),
                workspace,
                environment.Planner,
                environment.Permissions,
                decisions,
                policy,
                new NoServices(),
                router: environment.Router,
                reviewRetries: settings.ReviewRetries,
                successRetries: settings.SuccessRetries,
                proposeChecks: settings.ProposeChecks,
                numCtx: settings.NumCtx,
                disableThinking: settings.DisableThinking,
                maxParallelSteps: settings.MaxParallelSteps,
                evidenceBudget: settings.EvidenceBudget,
                allowImplicitToolCalls: settings.AllowImplicitToolCalls,
                reviewContent: settings.ReviewContent,
                checkSoundness: settings.CheckSoundness,
                revertRejectedSteps: settings.RevertRejectedSteps,
                // An unattended run is the one that most needs this: it lives in a Task owned by
                // this process, so closing the app kills it wherever it happens to be, and without
                // a checkpoint there is nothing for Resume to offer afterwards.
                checkpoints: new JsonCheckpointStore(workspace),
                settings: environment.RunSettings);

            var context = await new ContextProvider(workspace, new EnvironmentProbe(), memory)
                .BuildAsync(new IntentFocus(workspace.Id), ct);

            return new ComposedRun(
                new Recorded(
                    orchestrator,
                    new RunRecorder(
                        RunStoreFactory.Create(workspace), memory, workspace.Id, environment.RunSettings),
                    environment.Log),
                new Intent(
                    Guid.NewGuid(), prompt, source, context, DateTimeOffset.UtcNow, environment.WorkerId),
                tools);
        }
        catch
        {
            // The connections were made before the thing that threw. Nobody else has been handed
            // them yet, so this is the only place that can close them.
            await tools.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The orchestrator with the run's record and the log tap already around it.
    ///
    /// <para>A decorator rather than a step each caller performs, because the two callers that
    /// existed when this was written had drifted apart on exactly this: one recorded, and the one
    /// that did not looked correct.</para>
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
