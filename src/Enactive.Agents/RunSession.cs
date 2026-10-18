namespace Enactive.Agents;

using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Tasks;

/// <summary>Mutable state owned by one submission, never by the reusable orchestrator.</summary>
internal sealed class RunSession(RunScope scope, List<ChatMessage> messages)
{
    public RunScope Scope { get; } = scope;
    public List<ChatMessage> Messages { get; } = messages;
    public List<string> Digest { get; } = new();
    public RequestObligations? Obligations { get; set; }

    /// <summary>What each finished step handed on, by step id - see StepOutputContract.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, StepOutput> Outputs { get; } = new();
    /// <summary>What the workspace's build reported before any work. See BuildRegression.</summary>
    public IReadOnlyList<BuildBaseline> Builds { get; init; } = [];

    /// <summary>
    /// The workspace as the first step found it - right after <see cref="Builds"/> was taken, with nothing of the work
    /// done in between - so a step check of an ecosystem can be held against the baseline while nothing it covers has
    /// changed since (StepStartCheck). Set at the first step of a planned run that is not resumed, and at a quick action's
    /// start - including one that carries a task on. It says where THIS run began, not that <see cref="Builds"/> describes
    /// that state: a baseline kept from before an earlier attempt (BuildBaseline.FromAnEarlierAttempt) describes a
    /// workspace that attempt then worked in, and whoever holds a baseline against this must leave such entries out
    /// (Orchestrator.BeforeThisStepAsync does). Null where the run's start was not measured or the plan run was resumed.
    /// </summary>
    public WorkspaceSnapshot? BaselineAt { get; set; }

    /// <summary>
    /// What the declared step checks found at the gates of this run's steps, each with the workspace it ran on - how a
    /// later step's check stood when that step began, while nothing it covers has changed since (StepStartCheck). At most
    /// StepStartCheck.KeptPerCheck per check. Guarded by its own lock.
    /// </summary>
    public List<StepStartCheck.Known> KnownChecks { get; } = [];

    /// <summary>
    /// The declared checks this run is judged by (Plan Phase 3.1): the composed snapshot, or a resumed run's checkpoint's.
    /// Null when there are none. What every checkpoint the run writes carries.
    /// </summary>
    public Enactive.Core.Checks.CheckSnapshot? Checks { get; init; }

    /// <summary>
    /// The checks owed and not yet run (Plan Phase 3.2) - carried over from a resumed checkpoint, added to where a check
    /// is deferred. Written into every checkpoint, so a pause cannot lose one. Guarded by its own lock.
    /// </summary>
    public List<Enactive.Core.Checks.PendingCheck> PendingChecks { get; init; } = [];

    /// <summary>
    /// What the checks owed at a wave's end came to (Plan Phase 3.2) - already said where they ran, and kept for the run's
    /// outcome: the last wave's blocking check that failed has no step left to stop, and only the run's end can hold the
    /// run back on it. Guarded by its own lock.
    /// </summary>
    public List<Enactive.Core.Templates.CriterionResult> BoundaryChecks { get; } = [];

    /// <summary>
    /// The files the run's checks protect, as its baseline hashed them (Plan Phase 3.4) - taken when the run started, or a
    /// resumed run's checkpoint's. Empty when no check protects anything. What every checkpoint the run writes carries.
    /// </summary>
    public IReadOnlyList<Enactive.Core.Checks.ProtectedFile> Protected { get; init; } = [];

    /// <summary>
    /// Protected files a step the request asks to change them has removed: no longer looked for, so a later step whose
    /// changes were not measured is not sent back for a removal the run was asked to make. Guarded by its own lock.
    /// </summary>
    public HashSet<string> ProtectedReleased { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the request says may be changed, held against each step's first change to a file the run found. Null when it says nothing.</summary>
    public ChangeLimitGuard? ChangeLimits { get; init; }

    /// <summary>The plan's waves and what they were last validated against (Phase 6), or null when not validated.</summary>
    public WaveLedger? Waves { get; set; }

    /// <summary>What each step is, from the plan, for whoever judges it - see FanOut.ScopeNote. By step number.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<int, string> ScopeNotes { get; } = new();
    public Dictionary<Guid, StepOutcomeKind> Outcomes { get; } = new();

    /// <summary>What the engine recorded about each step that has ended - see StepRecord.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, StepRecord> Records { get; } = new();

    /// <summary>Why each step that did not succeed ended as it did, by step - for the steps that report on it.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, string> ReasonOf { get; } = new();
    private ExecutionJournal? _sharedJournal;
    private ReadLedger? _sharedReads;
    private readonly object _evidenceGate = new();
    private readonly HashSet<ExecutionJournal> _journals = new();

    public void Track(ExecutionJournal journal)
    {
        lock (_evidenceGate) _journals.Add(journal);
    }
    public ExecutionJournal RunEvidence()
    {
        var result = new ExecutionJournal(spansSteps: true);
        lock (_evidenceGate)
        {
            foreach (var action in _journals.SelectMany(j => j.Actions).OrderBy(a => a.At))
                // Every field, origin included. Copied without it, the run's evidence called every
                // call Native, and the per-model count of how calls arrived - the one ToolCallOrigin
                // exists to give - would have read the run-wide journal and seen only native calls.
                result.Record(action.Step, action.Tool, action.Arguments, action.Outcome, action.Output,
                    action.WorkspaceEffect, action.ChangedPaths, action.ExitCode, action.FileDeletion,
                    action.Origin, action.Named, action.Change);
            if (_resumed) result.NotePriorTranscript();
        }
        return result;
    }
    private bool _resumed;

    public void ConfigurePlan(bool sharedConversation, RunCheckpoint? resume)
    {
        if (resume is not null) { Digest.AddRange(resume.Digest); _resumed = true; }
        if (!sharedConversation) return;
        _sharedJournal = new ExecutionJournal(spansSteps: true);
        _sharedReads = new ReadLedger();
        if (resume is { Transcript.Count: > 0 }) _sharedJournal.NotePriorTranscript();
    }

    /// <param name="ownConversation">
    /// The step has a conversation of its own even though the run shares one (a step for one item).
    /// Its journal and what it has read are then its own too: the evidence window is the transcript
    /// window, and a reviewer must not judge it against calls it never saw.
    /// </param>
    public StepAttemptState BeginStep(List<ChatMessage> conversation, IArtifactScope store,
        IReadOnlyList<ChatMessage>? restartFrom = null, StepOutputSchema? output = null, bool ownConversation = false)
    {
        var shared = ownConversation ? null : _sharedJournal;
        var journal = shared ?? new ExecutionJournal();
        Track(journal);
        var start = journal.Mark();
        return new(conversation, store, journal, (ownConversation ? null : _sharedReads) ?? new ReadLedger(),
            shared is null ? start : 0, start, restartFrom) { Output = output };
    }
}

/// <summary>Survives retries of one step; transcript and evidence keep the same history.</summary>
internal sealed record StepAttemptState(
    List<ChatMessage> Messages, IArtifactScope Store, ExecutionJournal Journal, ReadLedger Reads,
    int EvidenceStart, int StepStart, IReadOnlyList<ChatMessage>? RestartFrom)
{
    /// <summary>What this step must hand on as values, when the plan declared it. Null: prose, as before.</summary>
    public StepOutputSchema? Output { get; init; }

    /// <summary>What it has handed on so far. Survives retries of the step, like its transcript.</summary>
    public StepOutputSlot OutputSlot { get; } = new();

    /// <summary>What this step may change - see WriteBoundary. Null: nothing beyond the ordinary gates.</summary>
    public WriteBoundary? Boundary { get; init; }

    /// <summary>Whether tools the boundary cannot check are kept from this step (a step whose results the engine assembles).</summary>
    public bool WithholdUnchecked { get; init; }

    /// <summary>
    /// The tools this step has loaded from its catalog, in the order it loaded them (see ToolBudget). Kept with the
    /// step and not with one attempt at it: an attempt after a rejected review carries on in the same conversation,
    /// which already uses them, and would otherwise have to load each one again before it could call it.
    /// </summary>
    public List<string> LoadedTools { get; } = [];

    /// <summary>The hand-over as the whole run offers it (see StepOutputContract.RunTool), or null for a run without one.</summary>
    public Enactive.Core.Tools.ToolDefinition? SubmitTool { get; init; }

    /// <summary>The criteria the plan attached to this step, checked when it ends (file criteria only).</summary>
    public IReadOnlyList<Enactive.Core.Templates.SuccessCriterionDefinition> Criteria { get; init; } = [];

    /// <summary>
    /// The coverage of the lists this step consumes, decided at its gate before any review (StepCoverageGate). Empty for
    /// a step that consumes none, and for a step done for each item or its join, whose coverage stays at the run's end.
    /// </summary>
    public IReadOnlyList<Enactive.Core.Templates.SuccessCriterionDefinition> Coverage { get; init; } = [];

    /// <summary>When the workspace checks owed for what this step changes run (Plan Phase 3.2) - see StepCheckTiming.</summary>
    public StepCheckTiming CheckTiming { get; init; } = StepCheckTiming.AtGate;

    /// <summary>The plan step this is, for a check deferred on its account; null for a quick action's one step.</summary>
    public Guid? StepId { get; init; }

    /// <summary>
    /// The planner marked it as one the request asks to change protected files (Plan Phase 3.4): a protected file it removes
    /// is shown to the reviewer instead of sending it back.
    /// </summary>
    public bool ChangesProtected { get; init; }

    /// <summary>
    /// Which attempt at the step is running (1 first), set by the attempt loop. Recorded on the step's check results so a
    /// card that shows a check twice - the step was sent back and gated again - can say which attempt each judged.
    /// </summary>
    public int Attempt { get; set; } = 1;

    /// <summary>The attempt for a check result's record: null on the first, which is what every run without retries has.</summary>
    public int? LaterAttempt => Attempt > 1 ? Attempt : null;

    /// <summary>The protected files as this step found them, before its first attempt (Orchestrator.ProtectedAtStepStart); null when none are protected.</summary>
    public IReadOnlyDictionary<string, string?>? ProtectedAtStart { get; init; }
}
