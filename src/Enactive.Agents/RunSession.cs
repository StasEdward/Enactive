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
    /// <summary>What the workspace's build reported before any work. See BuildRegression.</summary>
    public IReadOnlyList<BuildBaseline> Builds { get; init; } = [];
    public Dictionary<Guid, StepOutcomeKind> Outcomes { get; } = new();
    // Guarded by Outcomes, matching the outcome/reason snapshot used by the scheduler.
    public List<string> Reasons { get; } = new();
    private ExecutionJournal? _sharedJournal;
    private ReadLedger? _sharedReads;
    private readonly object _evidenceGate = new();
    private readonly HashSet<ExecutionJournal> _journals = new();
    private readonly List<IArtifactScope> _stores = new();
    public bool NeedsFinalReview { get; private set; }
    public void ObserveReview(ReviewResult review)
    {
        if (review.Obligations is not { } claims) return;
        lock (_evidenceGate)
            NeedsFinalReview |= claims.Any(c => c.Proof.Kind == ProofClaimKind.NotShown
                || c.Requirements?.Any(r => r.Proof.Kind == ProofClaimKind.NotShown) == true);
    }
    public void Track(ExecutionJournal journal, IArtifactScope store)
    {
        lock (_evidenceGate) { _journals.Add(journal); _stores.Add(store); }
    }
    public IReadOnlyList<IArtifactScope> Stores { get { lock (_evidenceGate) return _stores.ToArray(); } }
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
                    action.Origin);
            if (_resumed) result.NotePriorTranscript();
        }
        return result;
    }
    private bool _resumed;

    public void ConfigurePlan(bool sharedConversation, RunCheckpoint? resume)
    {
        if (resume is not null) { Digest.AddRange(resume.Digest); NeedsFinalReview = _resumed = true; }
        if (!sharedConversation) return;
        _sharedJournal = new ExecutionJournal(spansSteps: true);
        _sharedReads = new ReadLedger();
        if (resume is { Transcript.Count: > 0 }) _sharedJournal.NotePriorTranscript();
    }

    public StepAttemptState BeginStep(List<ChatMessage> conversation, IArtifactScope store,
        IReadOnlyList<ChatMessage>? restartFrom = null)
    {
        var journal = _sharedJournal ?? new ExecutionJournal();
        Track(journal, store);
        var start = journal.Mark();
        return new(conversation, store, journal, _sharedReads ?? new ReadLedger(),
            _sharedJournal is null ? start : 0, start, restartFrom);
    }
}

/// <summary>Survives retries of one step; transcript and evidence keep the same history.</summary>
internal sealed record StepAttemptState(
    List<ChatMessage> Messages, IArtifactScope Store, ExecutionJournal Journal, ReadLedger Reads,
    int EvidenceStart, int StepStart, IReadOnlyList<ChatMessage>? RestartFrom);
