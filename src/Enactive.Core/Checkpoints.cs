namespace Enactive.Core.History;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Tasks;

/// <summary>
/// One step as a checkpoint remembers it: what it is, what it depends on, and how far it got.
/// </summary>
/// <param name="Status">
/// The SCHEDULER's view - Pending, Running, Done, Failed, Skipped. Stored as the enum's name rather
/// than its number, so adding a member to <see cref="StepStatus"/> cannot silently reinterpret every
/// checkpoint already on disk.
/// </param>
/// <param name="Outcome">
/// The step's own verdict, for a step that finished. Kept SEPARATELY from <paramref name="Status"/>
/// because they answer different questions and a resumed run needs both: the scheduler decides what
/// may run next, and the outcome decides what the run as a whole amounts to. Null for a step that
/// never finished.
/// </param>
public sealed record CheckpointStep(
    Guid Id,
    string Title,
    IReadOnlyList<Guid> DependsOn,
    string Complexity,
    string Status,
    string? Outcome = null)
{
    public IReadOnlyList<string>? ObligationIds { get; init; }

    /// <summary>The output the plan declared for this step, so a resumed step is held to the same contract.</summary>
    public Enactive.Core.Tasks.StepOutputSchema? Output { get; init; }

    /// <summary>What a finished step handed on - its dependents, resumed, still receive it.</summary>
    public Enactive.Core.Tasks.StepOutput? Result { get; init; }

    // The graph as it grew (Phase 5.3): a resumed run carries on with the steps its items were given.
    public Enactive.Core.Tasks.ForEachSource? ForEach { get; init; }
    public IReadOnlyList<string>? Items { get; init; }
    public Guid? ExpandedFrom { get; init; }
    public bool Joins { get; init; }
    public string? NotExpanded { get; init; }
    public string? Report { get; init; }

    /// <summary>The files this step created and may therefore change (a step for one item) - see WriteBoundary.</summary>
    public IReadOnlyList<string>? Owned { get; init; }

    /// <summary>What the engine recorded about the step: outcome, cause, reason, and its last accepted result with its standing.</summary>
    public Enactive.Core.Tasks.StepRecord? Record { get; init; }

    /// <summary>Runs alone and is validated as soon as it ends (Phase 6).</summary>
    public bool Critical { get; init; }

    /// <summary>Only looks, and changes no file.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// The planner marked it as a step the request asks to change protected files (Plan Phase 3.4): a protected file it
    /// removes is shown to the reviewer, not sent back. Kept, so a resumed step is judged as it was planned.
    /// </summary>
    public bool ChangesProtected { get; init; }

    /// <summary>Every time this step was blocked, oldest first (Phase 7) - kept, so a resume loses no attempt.</summary>
    public IReadOnlyList<Enactive.Core.Tasks.StepBlock>? Blocks { get; init; }

    /// <summary>What the step consumes, so a resumed step is held to account for the same lists.</summary>
    public IReadOnlyList<Enactive.Core.Tasks.ConsumeSpec>? Consumes { get; init; }

    /// <summary>The dependencies the step only waits for - an order, not data.</summary>
    public IReadOnlyList<Guid>? OrderOnly { get; init; }
}

/// <summary>A step of a wave not yet validated, and what it changed as the engine recorded it.</summary>
public sealed record WaveCheckpointStep(Guid Id, int No, string Title, IReadOnlyList<string> Wrote, bool UnrecordedWrites);

/// <summary>
/// Where a plan's waves had got to (Phase 6), so a resumed run compares its next wave with what the last
/// validated one left - not with the run's baseline - and carries on the wave that was open.
/// </summary>
/// <param name="Reference">What each build and test target reported where the last validated wave left it.</param>
/// <param name="Open">The steps of the wave that had not been validated yet.</param>
/// <param name="Closed">How many waves had been closed, so the next one keeps its number.</param>
/// <param name="BeforeKept">Whether the files as the open wave found them were kept on disk beside the checkpoint.</param>
public sealed record WaveCheckpoint(IReadOnlyList<Enactive.Core.Builds.BaselineSnapshot> Reference,
    IReadOnlyList<WaveCheckpointStep> Open, int Closed, bool BeforeKept);

/// <summary>
/// Enough of an interrupted run to carry on from the last step boundary.
///
/// <para>The development spec carried this as "Resume does not exist ... a run waiting for a
/// decision is waiting IN MEMORY. Close the app and the run is gone, because there is nothing to
/// resume from." This is the something to resume from.</para>
///
/// <para><b>A step boundary, and no finer.</b> The work of a run lives in an async iterator and in
/// locals captured by closures - a conversation mid-turn, a tool call half-dispatched, a provider
/// stream half-consumed. None of that can be written down and picked up; resuming inside a step
/// would mean rewriting the orchestrator as a state machine. So a checkpoint is taken where the run
/// is genuinely between things, and the honest consequence is stated where a person can see it: a
/// step that was RUNNING when the process died is redone from its beginning, not continued.</para>
///
/// <para>Redoing a step is not free of consequence, and this is the part worth reading twice. The
/// files that step already wrote are on disk - they were written straight through - so the step
/// starts again in a workspace it has already changed. That is survivable because the file tools
/// read before they write and the step's instruction is unchanged; it is not invisible, and a
/// resumed run says so in its own event stream rather than leaving somebody to work it out.</para>
/// </summary>
/// <param name="Transcript">
/// The run's root conversation. At one step at a time this IS the run's memory - every step appends
/// to it - so dropping it would quietly turn a resumed run into a different kind of run. Above one
/// step at a time it is only the system and user prompt, and <paramref name="Digest"/> is what
/// carries across; both cases are served by keeping this and the digest together.
/// </param>
/// <param name="Settings">
/// What the run was allowed to do when it started. A resumed run uses THESE, not whatever the
/// window's sliders say today: continuing a run under permissions it did not have is not continuing
/// it. Staging is the exception and cannot be resumed at all - see <see cref="Staged"/>.
/// </param>
public sealed record RunCheckpoint(
    Guid RunId,
    Guid TaskId,
    DateTimeOffset StartedAt,
    DateTimeOffset At,
    string Request,
    string Title,
    string? WorkerId,
    string? Spec,
    IReadOnlyList<CheckpointStep> Steps,
    IReadOnlyList<string> Digest,
    IReadOnlyList<ChatMessage> Transcript,
    /// <summary>
    /// Workspace-relative PATHS of the files the interrupted run produced - not artifact handles.
    /// A handle's id belongs to the store that issued it, and re-offering an id no live store ever
    /// gave out would be a reference that resolves to nothing. The path is the part that is true
    /// about the workspace rather than about a process that has exited.
    /// </summary>
    IReadOnlyList<string> Artifacts,
    int StepsRun,
    long TokensSpent,
    RunSettings? Settings = null)
{
    /// <summary>
    /// Steps that still have to run: everything not finished, INCLUDING one that was running when
    /// the process died. A running step is not a finished step, and there is nobody running it.
    /// </summary>
    public int Remaining => Steps.Count(s => s.Status is not ("Done" or "Failed" or "Skipped"));

    /// <summary>
    /// Whether resuming would do anything. A checkpoint with nothing left is not resumable - it is
    /// a run that finished its steps and died in review, and re-running nothing is not a resume.
    /// </summary>
    public bool IsResumable => Remaining > 0 && Steps.Count > 0;

    /// <summary>Steps that will not be run again.</summary>
    public int Finished => Steps.Count - Remaining;

    /// <summary>
    /// True when the run staged its changes instead of writing them. Such a run is NEVER
    /// checkpointed - the field exists so the reason is written down next to the thing it is about.
    ///
    /// <para>Staged proposals live in a list in memory (<c>StagingArtifactStore</c>), so a resumed
    /// staged run would apply later steps on top of earlier changes that were never made. That is
    /// not a degraded resume, it is a wrong one, and the store refuses to write rather than offer
    /// it. The window already refuses to start a BACKGROUND run while staging is on, for exactly
    /// this reason.</para>
    /// </summary>
    public Enactive.Core.Tools.TaskActionPolicy? ActionPolicy { get; init; }
    public IReadOnlyList<Enactive.Core.Tools.TaskRestriction> Restrictions { get; init; } = [];

    public bool Staged => Settings?.Staged ?? false;
    /// <summary>
    /// What the workspace's build and tests reported before the run's first attempt. A resumed run
    /// compares against THIS, not against a baseline taken now over work already done - which would
    /// call the interrupted attempt's errors old. Null for older checkpoints, and for a run that took none.
    /// </summary>
    public IReadOnlyList<Enactive.Core.Builds.BaselineSnapshot>? Baseline { get; init; }
    /// <summary>The effective verification contract, preserved across resume. Null for older checkpoints.</summary>
    public IReadOnlyList<Enactive.Core.Templates.SuccessCriterionDefinition>? Checks { get; init; }

    /// <summary>Where the plan's waves had got to (Phase 6). Null when waves were not validated.</summary>
    public WaveCheckpoint? Waves { get; init; }

    /// <summary>
    /// The <see cref="Enactive.Core.Tasks.PlanContractVersions"/> version the plan was made under. Null: a checkpoint from
    /// before the contract, whose steps never declared what they consume - it must not be read as if they had.
    /// </summary>
    public int? PlanContractVersion { get; init; }

    /// <summary>
    /// What the plan leaves uncovered of the lists its steps depend on, after its one replan (Plan Phase 1.2): each a
    /// reason that keeps the run from Completed. Kept so a resumed run is held to it - resume does not plan, so it
    /// cannot find it again. Null: none, or a checkpoint from before the contract.
    /// </summary>
    public IReadOnlyList<string>? CoverageObligations { get; init; }

    /// <summary>
    /// Written by a run that was PAUSED (Plan Phase 4.2), not left behind by one that stopped: no step was running, so a
    /// resume redoes nothing and runs exactly the steps that are left. False for every other checkpoint - a step
    /// boundary written on the way, which is what a process that died leaves - and for every checkpoint written before
    /// pausing existed. Resumed through the same path as any other (<see cref="IsResumable"/>).
    /// </summary>
    public bool Paused { get; init; }

    /// <summary>
    /// The declared checks the run was composed with (Plan Phase 3.1), whole. A resumed run is judged by THESE, not by
    /// the manifests as they are when it is resumed: they may have been edited or deleted since, and a run carried on
    /// under checks it was never given is not the run that was started. Null: a checkpoint from before check manifests,
    /// whose run had none - its resume has none either.
    /// </summary>
    public Enactive.Core.Checks.CheckSnapshot? CheckSnapshot { get; init; }

    /// <summary>
    /// The checks the run owes and has not run yet - deferred to a wave or to apply (Plan Phase 3.2) - so a pause or an
    /// interruption cannot lose one: a deferred blocking check is not a pass. Null when none is owed.
    /// </summary>
    public IReadOnlyList<Enactive.Core.Checks.PendingCheck>? PendingChecks { get; init; }

    /// <summary>
    /// What the checks at a wave's end came to that holds the run back (Plan Phase 3.2) - a blocking check that failed or
    /// did not run there - without its whole output (that is kept in the run's folder). A wave's check is discharged once
    /// it has run, so it is no longer owed; its result was only in the running process, and a pause or a crash after the
    /// wave's end lost it: the resumed run, with nothing owed and nothing failing, ended Completed on a blocking check
    /// that never passed. Kept here, a resumed run's end is decided on it as the run's own would have been. Null when
    /// nothing holds the run back, and for a checkpoint from before.
    /// </summary>
    public IReadOnlyList<Enactive.Core.Templates.CriterionResult>? HeldBackBy { get; init; }

    /// <summary>
    /// The files the run's checks protect, with their hashes as its baseline found them (Plan Phase 3.4). A resumed run
    /// judges a step's removals against THESE: taken again on resume, they would be the files as the run's first half left
    /// them - a file it had already removed would no longer be protected, and one added meanwhile would be. Null when no
    /// check protects anything, and for a checkpoint from before protected files.
    /// </summary>
    public IReadOnlyList<Enactive.Core.Checks.ProtectedFile>? ProtectedFiles { get; init; }

    /// <summary>
    /// Protected files a step the request asks to change them has removed (Plan Phase 3.4): no longer looked for. Kept, so a
    /// resumed run does not take a removal it was asked to make for a loss - at a step whose changes were not measured, or
    /// at the boundary an obligation for that file is owed at. Null when none, and for a checkpoint from before.
    /// </summary>
    public IReadOnlyList<string>? ProtectedReleased { get; init; }
}

/// <summary>
/// Where interrupted runs are kept so they can be picked up again.
///
/// <para>There is one implementation and it is file-backed, which is deliberate rather than
/// unfinished. A checkpoint is about THIS machine's interrupted process and the working files beside
/// it; a run interrupted on one machine cannot be resumed on another, because the workspace it was
/// halfway through changing is not there. So a checkpoint follows the workspace folder and not the
/// history database, and a workspace whose runs live in MySQL still keeps its checkpoints locally.
/// </para>
/// </summary>
public interface IRunCheckpointStore
{
    /// <summary>Records where a run has got to. Overwrites the previous checkpoint for that run.</summary>
    Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct);

    /// <summary>Every interrupted run in this workspace, most recent first.</summary>
    Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct);

    Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct);

    /// <summary>
    /// Forgets a run's checkpoint. Called when a run reaches its end - however it ended.
    ///
    /// <para>A failed run is not a resumable run: it ran to a conclusion, and the conclusion was
    /// failure. What makes a run resumable is that nobody knows how it ended, which is the state a
    /// checkpoint left behind by a killed process describes.</para>
    /// </summary>
    Task DeleteAsync(Guid runId, CancellationToken ct);
}

/// <summary>Names of <see cref="StepStatus"/> and <see cref="StepOutcomeKind"/>, read back safely.</summary>
public static class CheckpointNames
{
    /// <summary>
    /// A stored status, or Pending when the name is one this build does not know. Unknown is treated
    /// as unfinished on purpose: doing a step twice is recoverable, and silently dropping one is not.
    /// </summary>
    public static StepStatus StatusOf(string? name)
        => Enum.TryParse<StepStatus>(name, ignoreCase: true, out var status) ? status : StepStatus.Pending;

    public static StepOutcomeKind? OutcomeOf(string? name)
        => Enum.TryParse<StepOutcomeKind>(name, ignoreCase: true, out var outcome) ? outcome : null;
}
