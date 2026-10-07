namespace Enactive.Agents;

using Enactive.Core.Artifacts;
using Enactive.Core.Builds;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Core.Workers;

/// <summary>
/// Everything one run is given, other than the engine's switches (<see cref="EngineOptions"/>): the parts it
/// works with, and the terms it runs under. The orchestrator is built from this and the options, and nothing
/// else.
///
/// <para><b>Why a record and not the constructor's own parameters.</b> Until 2026-10-08 the orchestrator took
/// twenty-three of them, and the one composition that builds it for a host (RunComposer) unpacked a record
/// like this one back into them field by field - a second list to keep in step with the first, and six
/// nullable parameters in a row that could change places without anything noticing. Named here, each one is
/// said by its name where it is set, and a new collaborator is a property, not a longer signature.</para>
///
/// <para>Owned by the caller: the orchestrator disposes none of it.</para>
/// </summary>
public sealed record RunEngineResources
{
    // ── the parts ───────────────────────────────────────────────────────────

    public required IChatProviderFactory Providers { get; init; }

    public required IModelResolver Models { get; init; }

    public required IWorkerProvider Workers { get; init; }

    public required IToolRegistry Tools { get; init; }

    /// <summary>Where the run's changes go - straight to disk, or staged for somebody to apply.</summary>
    public required IArtifactStore Artifacts { get; init; }

    public required WorkspaceInfo Workspace { get; init; }

    /// <summary>How the workspace's files are snapshotted around a step, so its changes can be told apart and reverted.</summary>
    public required IWorkspaceChangesFactory WorkspaceChanges { get; init; }

    public required Planner Planner { get; init; }

    public required IPermissionEngine Permissions { get; init; }

    /// <summary>Who answers this run's permission questions - the one part each host supplies.</summary>
    public required IDecisionHandler Decisions { get; init; }

    public required PermissionPolicy Policy { get; init; }

    /// <summary>Which model runs which phase. Null: every phase on the worker's own model.</summary>
    public IModelRouter? Router { get; init; }

    /// <summary>The success evaluator and the handover, when a test puts its own in; the engine's own otherwise.</summary>
    public OrchestratorServices? Agents { get; init; }

    /// <summary>
    /// The kinds of project the engine can build and read, each behind IEcosystem. None by default: a
    /// workspace no ecosystem recognises gets no build check, and so does a test that says nothing about builds.
    /// </summary>
    public IReadOnlyList<IEcosystem> Ecosystems { get; init; } = [];

    // ── the terms ───────────────────────────────────────────────────────────

    /// <summary>The checks that decide whether the run did what was asked - a template's; none for a typed request.</summary>
    public IReadOnlyList<SuccessCriterionDefinition> SuccessCriteria { get; init; } = [];

    /// <summary>How far the run may go - a template's; none for a typed request.</summary>
    public ExecutionLimits Limits { get; init; } = ExecutionLimits.None;

    /// <summary>
    /// Where an interrupted run is left so it can be picked up. Null means this run does not checkpoint, which
    /// is the behaviour that existed before resume did - the tests that do not care about it set nothing and
    /// nothing is written.
    /// </summary>
    public IRunCheckpointStore? Checkpoints { get; init; }

    /// <summary>
    /// What this run was allowed to do, as the host set it up. Carried in a checkpoint so a resumed run
    /// continues under the same permissions rather than under whatever the window says later - and so a STAGED
    /// run can be recognised as one that must not be checkpointed at all.
    /// </summary>
    public RunSettings? Settings { get; init; }

    // ── this machine's places ───────────────────────────────────────────────

    /// <summary>
    /// Folders this workspace may write to outside itself, from earlier runs. The machine's own store unless a
    /// test points it somewhere temporary: a run that never writes outside the workspace never touches it, and
    /// making every caller name it would put a policy decision in front of every test.
    /// </summary>
    public WritableRoots WritableRoots { get; init; } = WritableRoots.Default;

    /// <summary>Where a wave's files are kept for a resume - outside every workspace. A test points it somewhere temporary.</summary>
    public string WaveStore { get; init; } = WaveCapture.DefaultStore();
}
