namespace Enactive.Core.Rules;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Enactive.Core.History;

/// <summary>
/// Where a rule applies (Plan Phase 5.1). Workspace is the narrowest and the default - a lesson from one project is
/// not assumed to hold for every other. Written by name: kept in stores and run records, where a number would change
/// meaning if the list did.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuleScope>))]
public enum RuleScope
{
    Workspace,
    Global
}

/// <summary>
/// Where a rule stands. Only a Confirmed one reaches a run: a Candidate is what the engine saw and nobody has agreed
/// to yet, and a Dismissed one is kept so the same observation is not put to the person again.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuleStatus>))]
public enum RuleStatus
{
    Candidate,
    Confirmed,
    Dismissed
}

/// <summary>The headings a lesson is filed under - AI-DLC's four, so a person reads them as one list across runs.</summary>
public static class RuleHeading
{
    public const string Interpretations = "Interpretations";
    public const string Deviations = "Deviations";
    public const string Tradeoffs = "Tradeoffs";
    public const string OpenQuestions = "Open questions";
}

/// <summary>
/// A lesson: as the engine proposed it (a candidate), or as a person confirmed or wrote it (a rule).
/// </summary>
/// <param name="Text">What reaches a run's prompts once confirmed. A candidate's is the observation as it was made;
/// the person edits it into an instruction before keeping it, or keeps it as it is.</param>
/// <param name="SourceRunId">The run it came from, null for one a person wrote.</param>
public sealed record RuleEntry(
    Guid Id,
    RuleScope Scope,
    string Heading,
    string Text,
    Guid? SourceRunId,
    DateTimeOffset At,
    RuleStatus Status)
{
    /// <summary>Where the observation was made, for the person deciding on it - "run X, step 3, the review". Never sent to a model.</summary>
    public string? Origin { get; init; }

    /// <summary>
    /// What observation this is, as a key: a later run that sees the same thing does not put it to the person again,
    /// whatever they decided about it. Null for a rule a person wrote.
    /// </summary>
    public string? Source { get; init; }
}

/// <summary>
/// The rules of one layer. Rules are a person's decisions, so a write that fails throws - a keep that silently did not
/// keep is worse than an error. A run that reads them catches instead (RunComposer): no rule is better than no run.
/// </summary>
public interface IRuleStore
{
    Task<IReadOnlyList<RuleEntry>> ListAsync(CancellationToken ct);

    /// <summary>Adds the entry, or replaces the one with its id.</summary>
    Task SaveAsync(RuleEntry entry, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);
}

/// <summary>One confirmed rule as a run was given it.</summary>
public sealed record AppliedRule(Guid Id, RuleScope Scope, string Heading, string Text, Guid? SourceRunId);

/// <summary>
/// The confirmed rules a run was started with, whole and in the order they were given (Plan Phase 5.3). Kept in the
/// run's settings, and so in its record and its checkpoint: a resumed run is given the same text even if the rules were
/// edited or deleted since, and a past run can be read against the rules it actually had. A hash alone identifies a
/// rule set; it cannot give one back.
/// </summary>
/// <param name="Left">Confirmed rules that did not fit under <see cref="MaxChars"/> and were not given.</param>
public sealed record AppliedRules(IReadOnlyList<AppliedRule> Rules, int Left = 0)
{
    /// <summary>
    /// How much rule text a run is given, in characters. Each rule is in front of every worker turn - a local model with
    /// an 8k window pays for it on every call - and confirmed rules only accumulate. Past this the newest are left out
    /// and the count is said, so a person prunes rather than a window silently filling.
    /// </summary>
    public const int MaxChars = 3000;

    /// <summary>One rule's text, at most. A rule is a standing instruction, not a report; a longer one is cut and marked.</summary>
    public const int MaxRuleChars = 500;

    public static AppliedRules None { get; } = new([]);

    /// <summary>The confirmed rules of both layers: Global first, then the workspace's - nothing overrides anything, a
    /// run is given both - each oldest first, so a new rule is the one a full set leaves out.</summary>
    public static AppliedRules Compose(IEnumerable<RuleEntry> global, IEnumerable<RuleEntry> workspace)
    {
        var given = new List<AppliedRule>();
        var used = 0;
        var left = 0;
        foreach (var rule in global.Where(r => r.Status == RuleStatus.Confirmed).OrderBy(r => r.At)
                     .Concat(workspace.Where(r => r.Status == RuleStatus.Confirmed).OrderBy(r => r.At)))
        {
            var text = RuleText.Flat(rule.Text, MaxRuleChars);
            if (text.Length == 0)
                continue;
            if (used + text.Length > MaxChars)
            {
                left++;
                continue;
            }
            used += text.Length;
            given.Add(new AppliedRule(rule.Id, rule.Scope, rule.Heading, text, rule.SourceRunId));
        }
        return new AppliedRules(given, left);
    }

    /// <summary>Which rule set this is, short enough to read in a log. Of the text the prompts were given, nothing else.</summary>
    [JsonIgnore]
    public string Hash
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Rules.Select(r => $"{r.Scope}|{r.Heading}|{r.Text}")))))[..12]
            .ToLowerInvariant();

    /// <summary>
    /// The block a prompt is given, or empty with no rules. Said to be a person's confirmed instructions, and below the
    /// request: a rule from last month must not outrank what was asked today.
    /// </summary>
    public string Block()
    {
        if (Rules.Count == 0)
            return "";
        var sb = new StringBuilder();
        sb.AppendLine("## Workspace rules (confirmed by a person after earlier runs)");
        sb.AppendLine("Standing instructions for work in this workspace. Follow them as part of the request; where one "
                      + "disagrees with the request, the request wins.");
        foreach (var rule in Rules)
            sb.AppendLine($"- {rule.Text}");
        return sb.ToString();
    }
}

/// <summary>
/// The lessons a finished run proposes (Plan Phase 5.1): what the ENGINE saw go wrong, not what a model says it
/// learned - a weak worker asked for a diary invents one. In this version, the failed reviews and the engine's failed
/// checks, one per step, as their notes say it. A person keeps, edits or dismisses each; until then none reaches a run.
/// </summary>
public static class LessonCandidates
{
    /// <summary>
    /// Candidates one run may propose. A run that failed twelve pages for one reason would otherwise put twelve near
    /// copies in front of the person, and a list nobody reads through is a list nobody confirms.
    /// </summary>
    public const int PerRun = 5;

    /// <summary>The Inbox kind of "a run nobody watched left lessons to decide" (RunRecorder).</summary>
    public const string InboxKind = "lesson";

    /// <summary>How much of a failure's notes a candidate keeps - enough to tell what was wrong, short of a rule's cap.</summary>
    private const int NotesChars = AppliedRules.MaxRuleChars;

    public static IReadOnlyList<RuleEntry> From(RunRecord run, IEnumerable<RuleEntry> known)
    {
        var seen = new HashSet<string>(known.Select(k => k.Source).OfType<string>(), StringComparer.Ordinal);
        var found = new List<RuleEntry>();
        var steps = new HashSet<int>();
        foreach (var ev in run.Events.Where(e => e.Kind == nameof(Events.EventKind.ReviewFailed)))
        {
            if (found.Count >= PerRun)
                break;
            var notes = Notes(ev.Payload);
            if (string.IsNullOrWhiteSpace(notes))
                continue;
            // The first failure of a step is the lesson: the ones after it are the repair being judged.
            if (ev.Step is { } step && !steps.Add(step))
                continue;
            var text = RuleText.Flat(notes, NotesChars);
            var source = "review:" + Key(text);
            if (!seen.Add(source))
                continue;
            found.Add(new RuleEntry(Guid.NewGuid(), RuleScope.Workspace, RuleHeading.Deviations, text, run.RunId,
                run.FinishedAt, RuleStatus.Candidate)
            {
                Origin = $"run \"{run.Title}\"" + (ev.Step is { } no ? $", step {no}" : "") + ": " + ev.Summary.Split(':')[0].Trim(),
                Source = source
            });
        }
        return found;
    }

    private static string? Notes(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("notes", out var value)
                   && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The same observation in other spacing or case is the same observation.</summary>
    private static string Key(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))))[..16];
}

/// <summary>A rule's text as a prompt line: one line, so a rule cannot open a section of its own, and cut to its cap.</summary>
public static class RuleText
{
    public static string Flat(string? text, int max)
    {
        var flat = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}
