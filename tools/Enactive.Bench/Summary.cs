namespace Enactive.Bench;

using System.Globalization;
using System.Text;

/// <summary>One scenario's run: the engine's outcome, the truth, and the verdict on the engine.</summary>
internal sealed record ScenarioResult(
    string Scenario,
    string Outcome,
    string Expected,
    bool TruthHolds,
    string Verdict,
    int? ExitCode,
    double WallSeconds,
    string? Note,
    RunMetrics? Metrics,
    IReadOnlyList<CheckResult> Checks,
    string Workspace);

/// <summary>One benchmark: which engine it measured, when, and every scenario.</summary>
internal sealed record BenchSummary(string Stamp, string Commit, bool Dirty, DateTimeOffset At, IReadOnlyList<ScenarioResult> Results);

/// <summary>The benchmark as a table a person reads, beside the baseline where there is one.</summary>
internal static class Summary
{
    public static string Render(BenchSummary now, BenchSummary? baseline)
    {
        static string N(double v) => v.ToString("0", CultureInfo.InvariantCulture);
        static string K(long v) => v >= 10_000 ? (v / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k" : v.ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine($"# Benchmark {now.Stamp}").AppendLine();
        sb.AppendLine($"Engine commit `{now.Commit}`{(now.Dirty ? " with uncommitted changes in src" : "")}"
            + (baseline is null ? ", no baseline to compare with." : $", compared with the baseline `{baseline.Stamp}`."));
        sb.AppendLine();
        sb.AppendLine("| Scenario | Outcome | Truth | Verdict | Time, s | Steps | Reviews ✓/✗ | Tools | Tokens in/out (cached) | Cache breaks | Coverage |");
        sb.AppendLine("| --- | --- | --- | --- | ---: | ---: | --- | ---: | --- | ---: | --- |");
        foreach (var r in now.Results)
        {
            var m = r.Metrics;
            var was = baseline?.Results.FirstOrDefault(b => b.Scenario == r.Scenario);
            string Delta(double? a, double? b) => a is { } x && b is { } y && y > 0 ? $" ({(x - y >= 0 ? "+" : "")}{N((x - y) / y * 100)}%)" : "";
            var coverage = r.Checks.Where(c => c.OutOf is not null).Select(c => $"{c.Found}/{c.OutOf}").FirstOrDefault() ?? "";
            var cached = m is null ? 0 : m.TokensByPurpose.Values.Sum(t => t.Cached);
            sb.Append($"| {r.Scenario} | {r.Outcome} | {(r.TruthHolds ? "holds" : "fails")} | **{r.Verdict}**")
              .Append(was is not null && was.Verdict != r.Verdict ? $" (was {was.Verdict})" : "")
              .Append($" | {N(m?.Seconds ?? r.WallSeconds)}{Delta(m?.Seconds, was?.Metrics?.Seconds)}")
              .Append($" | {m?.Steps} | {m?.ReviewsPassed}/{m?.ReviewsFailed} | {m?.ToolCalls}")
              .Append($" | {(m is null ? "" : $"{K(m.TokensIn)}/{K(m.TokensOut)} ({K(cached)})")}{Delta(m?.TokensIn, was?.Metrics?.TokensIn)}")
              .Append($" | {m?.CacheBreaks} | {coverage} |").AppendLine();
        }

        var wrong = now.Results.Count(r => r.Verdict.StartsWith("FALSE", StringComparison.Ordinal));
        sb.AppendLine().AppendLine($"False PASS: {now.Results.Count(r => r.Verdict == "FALSE PASS")}, false FAIL: "
            + $"{now.Results.Count(r => r.Verdict == "FALSE FAIL")}, task success (truth holds): "
            + $"{now.Results.Count(r => r.TruthHolds)} of {now.Results.Count}.");

        foreach (var r in now.Results)
        {
            var failed = r.Checks.Where(c => !c.Passed).ToArray();
            if (failed.Length == 0 && r.Note is null && r.Metrics?.Reason is null) continue;
            sb.AppendLine().AppendLine($"## {r.Scenario}");
            if (r.Note is not null) sb.AppendLine($"- Run: {r.Note}");
            if (r.Metrics?.Reason is { Length: > 0 } reason) sb.AppendLine($"- Engine's reason: {reason}");
            foreach (var c in failed) sb.AppendLine($"- Truth `{c.Id}`{(c.Required ? "" : " (optional)")}: {c.Detail}");
            sb.AppendLine($"- Workspace left at `{r.Workspace}`");
        }
        return sb.ToString();
    }
}
