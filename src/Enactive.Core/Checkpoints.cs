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
}

/// <summary>
/// Enough of an interrupted run to carry on from the last step boundary.
///
/// <para><c>PLAN_v2.md</c> §11 carried this as "Resume does not exist ... a run waiting for a
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
    /// <summary>The effective verification contract, preserved across resume. Null for older checkpoints.</summary>
    public IReadOnlyList<Enactive.Core.Templates.SuccessCriterionDefinition>? Checks { get; init; }
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
