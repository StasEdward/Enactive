namespace Enactive.Workspace;

using System.Text;
using Enactive.Core.History;
using Enactive.Core.Memory;

/// <summary>
/// Projects runs + memory entries into the "project memory" view (PLAN_v2 §2.6/§2.7): the Timeline is
/// not a raw log but a memory of the project — the durable Decisions and notes first, then everything
/// the project has produced (Artifacts), then the run history. Pure projection, no side effects.
/// </summary>
public static class ProjectMemory
{
    public static string Render(IReadOnlyList<RunRecord> runs, IReadOnlyList<MemoryEntry> memory, string root)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"PROJECT MEMORY — {root}");
        sb.AppendLine();

        // ── Decisions & notes (the durable "why"), newest first ─────────────────
        var mem = memory.OrderByDescending(m => m.At).ToList();
        sb.AppendLine($"── Decisions & notes ({mem.Count}) ──");
        if (mem.Count == 0)
            sb.AppendLine("  (none yet)");
        else
            foreach (var m in mem.Take(50))
                sb.AppendLine($"  {m.At.ToLocalTime():yyyy-MM-dd HH:mm}  [{m.Kind}] {m.Content}");
        sb.AppendLine();

        // ── Artifacts across all runs (what exists), distinct, newest first ──────
        var flat = new List<(DateTimeOffset At, string Text)>();
        foreach (var run in runs)
            foreach (var a in run.Artifacts)
                flat.Add((run.FinishedAt, a));
        var artifacts = flat
            .GroupBy(a => a.Text)
            .Select(g => (At: g.Max(x => x.At), Text: g.Key))
            .OrderByDescending(a => a.At)
            .ToList();
        sb.AppendLine($"── Artifacts ({artifacts.Count}) ──");
        if (artifacts.Count == 0)
            sb.AppendLine("  (none yet)");
        else
            foreach (var a in artifacts.Take(80))
                sb.AppendLine($"  {a.At.ToLocalTime():yyyy-MM-dd}  {a.Text}");
        sb.AppendLine();

        // ── Run history (chronological, grouped by day) ─────────────────────────
        sb.AppendLine($"── Runs ({runs.Count}) ──");
        if (runs.Count == 0)
        {
            sb.AppendLine("  (no runs yet)");
        }
        else
        {
            string? currentDay = null;
            foreach (var run in runs)
            {
                var day = run.StartedAt.ToLocalTime().ToString("yyyy-MM-dd");
                if (day != currentDay) { sb.AppendLine("  " + day); currentDay = day; }
                sb.AppendLine($"    {run.StartedAt.ToLocalTime():HH:mm}  {run.Status,-10} {run.Title}");
                if (run.Decisions.Count > 0)
                    sb.AppendLine($"          decisions: {string.Join("; ", run.Decisions)}");
                if (run.Artifacts.Count > 0)
                    sb.AppendLine($"          artifacts: {string.Join(", ", run.Artifacts)}");
            }
        }
        return sb.ToString();
    }
}
