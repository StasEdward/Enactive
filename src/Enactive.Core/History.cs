namespace Enactive.Core.History;

/// <summary>A persisted event within a run (Timeline is a projection over these).</summary>
public sealed record RunEventRecord(DateTimeOffset At, string Kind, string Summary);

/// <summary>
/// A persisted agent run (PLAN_v2 §2A.6) plus its events, artifacts and decisions. The Timeline —
/// "the memory of the project" — is built from these records.
/// </summary>
public sealed record RunRecord(
    Guid RunId,
    Guid TaskId,
    string Title,
    string? Model,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Status,
    IReadOnlyList<RunEventRecord> Events,
    IReadOnlyList<string> Artifacts,
    IReadOnlyList<string> Decisions);

/// <summary>Persists and loads run records for a workspace.</summary>
public interface IRunStore
{
    Task SaveAsync(RunRecord record, CancellationToken ct);
    Task<IReadOnlyList<RunRecord>> LoadAllAsync(CancellationToken ct);
}
