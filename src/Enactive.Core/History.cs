namespace Enactive.Core.History;

/// <summary>
/// A persisted event within a run (Timeline is a projection over these).
///
/// <para><c>Step</c> is the PLAN POSITION the event belongs to, or null for events that belong to
/// the run rather than to a step. It is recorded because a replay cannot recover it: with more than
/// one step in flight, "the step that started most recently" is not the step a tool call came from,
/// and attributing it by order puts the call under the wrong card. Defaulted, so records written
/// before this existed still load - they simply have no step, and replay shows them as a timeline
/// rather than as cards.</para>
/// </summary>
/// <param name="Payload">
/// The event's typed payload, kept verbatim so a stored run can be read the same way a live one is.
/// Without it the only machine-readable thing a past run had was its English summary, and reading a
/// past run meant parsing prose — the exact habit the typed payloads were introduced to end. Null
/// for runs recorded before this was kept, which is why every reader treats null as "not known"
/// rather than as a default.
/// </param>
public sealed record RunEventRecord(
    DateTimeOffset At, string Kind, string Summary, int? Step = null, string? Payload = null);

/// <summary>
/// How the run was set up: the autonomy it was given, the worker role it took, and whether its
/// changes were staged for review. Recorded because "why did it do that" is usually answered by
/// what it was allowed to do, and a setting read off the window today is the setting as it is NOW,
/// not as it was when the run happened.
/// </summary>
public sealed record RunSettings(int Autonomy, string AutonomyName, string? Worker, bool Staged);

/// <summary>
/// What the run cost, summed over every turn. Providers report a total per turn rather than an
/// increment, so this is the sum of the turns, not of a running counter.
/// </summary>
public sealed record RunUsage(int PromptTokens, int CompletionTokens)
{
    public int Total => PromptTokens + CompletionTokens;
}

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
    IReadOnlyList<string> Decisions,
    /// <summary>Null for runs recorded before the settings were kept.</summary>
    RunSettings? Settings = null,
    /// <summary>Null for runs recorded before tokens were counted, and for a provider that does not
    /// report them - which is a different thing from zero and is shown differently.</summary>
    RunUsage? Usage = null);

/// <summary>Persists and loads run records for a workspace.</summary>
public interface IRunStore
{
    Task SaveAsync(RunRecord record, CancellationToken ct);
    Task<IReadOnlyList<RunRecord>> LoadAllAsync(CancellationToken ct);
}
