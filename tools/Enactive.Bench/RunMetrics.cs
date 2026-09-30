namespace Enactive.Bench;

using System.Text.Json;
using Enactive.Core.History;

/// <summary>Tokens one purpose of a run spent: planning, the steps, the reviews.</summary>
internal sealed record Tokens(long In, long Out, long Cached);

/// <summary>
/// What a run did, read from its record - the engine's own typed events, never the wording of a report. The metrics of
/// plan phase 0: outcome, steps and their outcomes, reviews and retries, tool calls, tokens by purpose, time, questions
/// asked, trims, and the prompt cache breaking (PrefixCacheWatch).
/// </summary>
internal sealed record RunMetrics(
    string Outcome,
    string? Reason,
    double Seconds,
    int Steps,
    IReadOnlyDictionary<string, int> StepOutcomes,
    int ReviewsPassed,
    int ReviewsFailed,
    int ToolCalls,
    IReadOnlyDictionary<string, Tokens> TokensByPurpose,
    int Questions,
    int Trims,
    int CacheBreaks,
    int CacheRewrites)
{
    public long TokensIn => TokensByPurpose.Values.Sum(t => t.In);
    public long TokensOut => TokensByPurpose.Values.Sum(t => t.Out);

    public static RunMetrics Of(RunRecord record)
    {
        var events = record.Events;
        int Count(string kind) => events.Count(e => e.Kind == kind);

        var stepOutcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in events.Where(e => e.Kind == "StepCompleted"))
        {
            var outcome = Field(e.Payload, "stepOutcome") ?? "unknown";
            stepOutcomes[outcome] = stepOutcomes.TryGetValue(outcome, out var n) ? n + 1 : 1;
        }

        var tokens = new Dictionary<string, Tokens>(StringComparer.Ordinal);
        foreach (var e in events.Where(e => e.Kind == "UsageReported"))
        {
            var purpose = Field(e.Payload, "purpose") ?? "other";
            var had = tokens.TryGetValue(purpose, out var t) ? t : new Tokens(0, 0, 0);
            tokens[purpose] = new Tokens(had.In + Number(e.Payload, "in"), had.Out + Number(e.Payload, "out"),
                had.Cached + Number(e.Payload, "cached"));
        }

        var terminal = events.LastOrDefault(e => e.Kind is "TaskCompleted" or "TaskFailed");
        var cacheNotes = events.Where(e => e.Kind == "ContextAssembled" && e.Summary.StartsWith("Prompt cache:", StringComparison.Ordinal)).ToArray();

        return new RunMetrics(
            RunReport.OutcomeOf(record)?.ToString() ?? "none",
            Field(terminal?.Payload, "reason") ?? terminal?.Summary,
            (record.FinishedAt - record.StartedAt).TotalSeconds,
            Count("StepCompleted"),
            stepOutcomes,
            Count("ReviewPassed"),
            Count("ReviewFailed"),
            Count("ToolInvoked"),
            tokens,
            Count("DecisionRequested"),
            Count("ContextTrimmed"),
            cacheNotes.Count(e => e.Summary.Contains("only grew", StringComparison.Ordinal)),
            cacheNotes.Count(e => e.Summary.Contains("was rewritten", StringComparison.Ordinal)));
    }

    private static string? Field(string? payload, string name)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v)
                ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static long Number(string? payload, string name)
        => long.TryParse(Field(payload, name), out var n) ? n : 0;
}
