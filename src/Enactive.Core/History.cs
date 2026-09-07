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
    RunUsage? Usage = null,
    /// <summary>
    /// The resolved specification this run was started from, verbatim - the canonical JSON of a
    /// <c>ResolvedTaskSpec</c>. Null for a run that was typed rather than started from a template.
    ///
    /// <para>A STRING for the same reason <see cref="RunEventRecord.Payload"/> is one: it is a
    /// snapshot, and a snapshot's job is to still mean what it meant. The template it came from is
    /// editable and its version moves on; reading a finished run against the template as it is
    /// TODAY answers the wrong question. Parse it with <c>ResolvedTaskSpec.Parse</c>, which gives
    /// values back rather than leaving anyone to pick at the text.</para>
    /// </summary>
    string? Spec = null);

/// <summary>Persists and loads run records for a workspace.</summary>
public interface IRunStore
{
    Task SaveAsync(RunRecord record, CancellationToken ct);
    Task<IReadOnlyList<RunRecord>> LoadAllAsync(CancellationToken ct);

    /// <summary>
    /// Forgets one run. A run that is not there is not an error - it is the goal.
    ///
    /// <para>This removes the RECORD: what was asked, what the engine did, and the paths it wrote.
    /// It does not touch the files themselves. Those are the user's work sitting in their workspace,
    /// and a history window is no place to delete them from - somebody clearing a list of old runs
    /// is tidying a list, not asking for their code back.</para>
    /// </summary>
    Task DeleteAsync(Guid runId, CancellationToken ct);
}
