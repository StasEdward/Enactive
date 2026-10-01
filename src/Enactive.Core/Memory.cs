namespace Enactive.Core.Memory;

/// <summary>
/// A durable fact about the project — the "memory" the Timeline folds into.
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

/// <summary>
/// The kinds of entry this engine writes. Strings on the wire (the stores keep a column, and a
/// record written by an older build must still load), named here so the two ends agree.
/// </summary>
public static class MemoryKind
{
    /// <summary>Something the user decided - an approval, a refusal. Durable by nature.</summary>
    public const string Decision = "decision";

    /// <summary>How a run ended, in one line. What the NEXT run wants to know first.</summary>
    public const string Outcome = "outcome";
}

/// <summary>Persists and loads the project's memory entries for a workspace.</summary>
public interface IMemoryStore
{
    Task AppendAsync(MemoryEntry entry, CancellationToken ct);
    Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken ct);
}
