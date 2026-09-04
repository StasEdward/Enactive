namespace Enactive.Core.Memory;

/// <summary>
/// A durable fact about the project — the "memory" the Timeline folds into (PLAN_v2 §2.6/§2.9).
/// Decisions the user resolved are recorded here (Kind "decision"); notes and milestones can be added
/// later. Kept separate from a run's transient events so it survives and accumulates across runs.
/// </summary>
public sealed record MemoryEntry(
    Guid Id,
    Guid WorkspaceId,
    string Kind,               // "decision" | "note" | "milestone" | ...
    string Content,
    Guid? SourceDecisionId,
    DateTimeOffset At);

/// <summary>Persists and loads the project's memory entries for a workspace.</summary>
public interface IMemoryStore
{
    Task AppendAsync(MemoryEntry entry, CancellationToken ct);
    Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken ct);
}
