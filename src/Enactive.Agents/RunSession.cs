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
    public Dictionary<Guid, StepOutcomeKind> Outcomes { get; } = new();
    // Guarded by Outcomes, matching the outcome/reason snapshot used by the scheduler.
    public List<string> Reasons { get; } = new();
    private ExecutionJournal? _sharedJournal;
    private ReadLedger? _sharedReads;

    public void ConfigurePlan(bool sharedConversation, RunCheckpoint? resume)
    {
        if (resume is not null) Digest.AddRange(resume.Digest);
        if (!sharedConversation) return;
        _sharedJournal = new ExecutionJournal(spansSteps: true);
        _sharedReads = new ReadLedger();
        if (resume is { Transcript.Count: > 0 }) _sharedJournal.NotePriorTranscript();
    }

    public StepAttemptState BeginStep(List<ChatMessage> conversation, IArtifactScope store,
        IReadOnlyList<ChatMessage>? restartFrom = null)
    {
        var journal = _sharedJournal ?? new ExecutionJournal();
        var start = journal.Mark();
        return new(conversation, store, journal, _sharedReads ?? new ReadLedger(),
            _sharedJournal is null ? start : 0, start, restartFrom);
    }
}

/// <summary>Survives retries of one step; transcript and evidence keep the same history.</summary>
internal sealed record StepAttemptState(
    List<ChatMessage> Messages, IArtifactScope Store, ExecutionJournal Journal, ReadLedger Reads,
    int EvidenceStart, int StepStart, IReadOnlyList<ChatMessage>? RestartFrom);
