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
/// What one model was asked to do in a run, and what that cost.
///
/// <para>Not a breakdown for its own sake. A run with phase bindings is several models: the
/// screenshot of 2026-09-10 shows a plan on <c>Antropic/claude-sonnet-4-6</c> and the work on
/// <c>ollama/gemma4:31b-cloud</c>, and the record for it says <c>model: ollama/gemma4:31b-cloud</c>
/// and nothing else. That is not a rounding of the truth — the expensive half of the run is the
/// half the record leaves out, and a person reading a bill is looking for exactly that half.</para>
/// </summary>
/// <param name="Purpose">plan, execute or review — see <c>WorkEventPayload.WorkPurpose</c>.</param>
/// <param name="Calls">
/// How many times this model was asked. A phase that was BOUND but never ran does not appear here at
/// all: this records what was spent, not what was configured, and "the reviewer was bound to Sonnet"
/// is a different claim from "the reviewer ran".
/// </param>
public sealed record ModelSpend(
    string Purpose,
    string ProviderId,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    int Calls)
{
    public int Total => PromptTokens + CompletionTokens;

    /// <summary>"Antropic/claude-sonnet-4-6", as every other part of the app names a model.</summary>
    public string Ref => $"{ProviderId}/{Model}";

    /// <summary>
    /// How many of <see cref="PromptTokens"/> this model was not charged full price for, because
    /// the provider had the prefix already. Null when the provider does not report it — every local
    /// runtime, and every record written before 2026-09-11.
    ///
    /// <para>Null is not zero and the difference is the whole reason this is nullable. Zero says
    /// "the cache was cold, or the prompt was below the model's minimum"; null says "nobody counted
    /// here". Rendering the second as the first would tell somebody their caching is not working
    /// when what is actually true is that Ollama has no such number to give.</para>
    ///
    /// <para>An <c>init</c> property for the same reason as <see cref="RunUsage.ByModel"/>: there
    /// are already <c>new ModelSpend(...)</c> calls with six positional arguments, and every row
    /// stored before today has to deserialise into one.</para>
    /// </summary>
    public int? CachedPromptTokens { get; init; }

    /// <summary>
    /// The cached share as a percentage of the prompt, or null when there is nothing to divide.
    /// Rounded to whole points: this is read to answer "is caching doing anything", and a figure to
    /// two decimals invites a precision the sampling does not have.
    /// </summary>
    public int? CachedPercent
        => CachedPromptTokens is { } cached && PromptTokens > 0
            ? (int)Math.Round(100.0 * cached / PromptTokens)
            : null;
}

/// <summary>
/// What the run cost, summed over every turn. Providers report a total per turn rather than an
/// increment, so this is the sum of the turns, not of a running counter.
/// </summary>
public sealed record RunUsage(int PromptTokens, int CompletionTokens)
{
    public int Total => PromptTokens + CompletionTokens;

    /// <summary>
    /// The same tokens, split by the model and phase that spent them. Null — not empty — for a
    /// record written before this existed: "nobody wrote it down" and "one model did everything"
    /// are different facts, and a reader that shows an empty list as the second would be inventing
    /// an answer about every run in the history.
    ///
    /// <para>An <c>init</c> property rather than a constructor parameter so that every
    /// <c>new RunUsage(a, b)</c> already written still compiles and every row already stored still
    /// deserialises. There is no schema change: this rides in the same <c>usage_json</c>.</para>
    /// </summary>
    public IReadOnlyList<ModelSpend>? ByModel { get; init; }

    /// <summary>
    /// How much of this run's prompt was served from a cache, over every model that reported one.
    /// Null when none did — see <see cref="ModelSpend.CachedPromptTokens"/> on why that is not zero.
    ///
    /// <para>A SUM over the models that answered, not over all of them. A run whose worker is on
    /// Ollama and whose reviewer is on Anthropic reports the reviewer's cached tokens and says
    /// nothing about the worker's, which is exactly right: there is no number there to add.</para>
    /// </summary>
    public int? CachedPromptTokens { get; init; }

    /// <summary>The cached share of the prompt, in whole percent, or null when nothing reported one.</summary>
    public int? CachedPercent
        => CachedPromptTokens is { } cached && PromptTokens > 0
            ? (int)Math.Round(100.0 * cached / PromptTokens)
            : null;
}

/// <summary>
/// The fields that identify a run and place it in a list: what a row, a grouping or a title needs.
///
/// <para>Here so that the helpers which only ever wanted these — <see cref="RunTitle"/>,
/// <see cref="RunHistory"/> — do not have to be written twice, once for a whole
/// <see cref="RunRecord"/> and once for a <see cref="RunSummary"/>. It deliberately exposes no
/// events: a caller that has only a header must not be able to ask for a transcript and be told
/// "none".</para>
/// </summary>
public interface IRunHeader
{
    Guid RunId { get; }
    Guid TaskId { get; }
    string Title { get; }
    DateTimeOffset StartedAt { get; }
    string Status { get; }

    /// <summary>Null for a run that was typed rather than started from a template.</summary>
    string? Spec { get; }
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
    string? Spec = null) : IRunHeader;

/// <summary>
/// A run without its EVENTS: everything a list, a timeline or a grouping needs, and nothing that
/// grows with how long the run was.
///
/// <para>The history window used to read every run whole to fill six fields per row. A run's events
/// are its whole transcript - every prompt, response, tool call and payload - so a workspace with a
/// few hundred runs behind it read tens of megabytes to draw a list. <c>PLAN_v2.md</c> §11 carried
/// it as "it will bite at a few hundred runs".</para>
///
/// <para>A distinct TYPE rather than a <see cref="RunRecord"/> with an empty <c>Events</c> list. A
/// record that says it has no events when it has thousands is the same lie as a result that says
/// "done" for work that was refused: the caller cannot tell "none" from "not loaded", and would
/// eventually read one as the other. This type cannot be asked what it does not know.</para>
/// </summary>
public sealed record RunSummary(
    Guid RunId,
    Guid TaskId,
    string Title,
    string? Model,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Status,
    IReadOnlyList<string> Artifacts,
    IReadOnlyList<string> Decisions,
    RunSettings? Settings = null,
    RunUsage? Usage = null,
    string? Spec = null) : IRunHeader
{
    /// <summary>The header of a record already in hand.</summary>
    public static RunSummary Of(RunRecord record)
        => new(record.RunId, record.TaskId, record.Title, record.Model, record.StartedAt,
               record.FinishedAt, record.Status, record.Artifacts, record.Decisions,
               record.Settings, record.Usage, record.Spec);
}

/// <summary>Persists and loads run records for a workspace.</summary>
public interface IRunStore
{
    Task SaveAsync(RunRecord record, CancellationToken ct);
    Task<IReadOnlyList<RunRecord>> LoadAllAsync(CancellationToken ct);

    /// <summary>
    /// Every run's HEADER, newest first - what a list needs, without the transcripts.
    ///
    /// <para>Defaulted so a store written before this existed still compiles and still works; the
    /// default reads everything and throws the events away, which is honest about being no faster.
    /// Every store that ships overrides it with a query that never fetches them.</para>
    /// </summary>
    async Task<IReadOnlyList<RunSummary>> LoadSummariesAsync(CancellationToken ct)
        => (await LoadAllAsync(ct)).Select(RunSummary.Of).ToArray();

    /// <summary>
    /// One run, whole. What opening a row costs, paid when a row is opened rather than for every
    /// row in the list. Null when there is no such run.
    /// </summary>
    async Task<RunRecord?> LoadAsync(Guid runId, CancellationToken ct)
        => (await LoadAllAsync(ct)).FirstOrDefault(r => r.RunId == runId);

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
