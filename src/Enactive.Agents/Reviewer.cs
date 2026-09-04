namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;

/// <summary>Reviewer verdict for a step.</summary>
public sealed record ReviewResult(bool Pass, string Notes);

/// <summary>
/// The reasoning agent reviewing a coding step (PLAN_v2 multi-agent: Reasoner reviews the Coder's work).
/// One LLM call → PASS/FAIL + short notes. Fails open (treats an unparyable answer as PASS) so a review
/// glitch never blocks the run.
/// </summary>
public sealed class Reviewer
{
    public async Task<ReviewResult> ReviewAsync(
        string stepTitle, string coderOutput, string executionEvidence, IReadOnlyList<string> artifacts,
        IChatProvider provider, string model, CancellationToken ct)
    {
        var files = artifacts.Count == 0 ? "(none)" : string.Join(", ", artifacts);
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(SystemPrompt),
            ChatMessage.User(
                $"Step: {stepTitle}\n\n"
                + $"What the coding agent reported:\n{coderOutput}\n\n"
                + $"Tool execution evidence — the ACTUAL commands run and their real stdout/stderr/exit codes "
                + $"(this is the ground truth; the agent's own words above may be wrong or invented):\n{executionEvidence}\n\n"
                + $"Files changed: {files}\n\n"
                + "Judge ONLY from the evidence. FAIL if: the step required running a command but none was actually "
                + "run; a required command failed (non-zero exit or an error in its output); or the reported/saved "
                + "result is fabricated or a placeholder value not present in the real tool output. Otherwise PASS.")
        };

        var completion = await provider.CompleteAsync(new ChatRequest(model, messages, Temperature: 0.0), ct);
        return Parse(completion.Message.Content ?? "");
    }

    private static ReviewResult Parse(string text)
    {
        var json = ExtractJson(StripThink(text));
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var verdict = root.TryGetProperty("verdict", out var v) ? v.GetString() : null;
                var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                var pass = !string.Equals(verdict, "fail", StringComparison.OrdinalIgnoreCase);
                return new ReviewResult(pass, notes);
            }
            catch (JsonException) { }
        }

        // Fail open: if we can't parse a verdict, don't block the run.
        return new ReviewResult(true, "review not parseable");
    }

    private static string StripThink(string text)
    {
        const string open = "<think>";
        const string close = "</think>";
        var start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        var end = text.IndexOf(close, StringComparison.OrdinalIgnoreCase);
        return start >= 0 && end > start ? text.Remove(start, end + close.Length - start) : text;
    }

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private const string SystemPrompt =
        "You are a senior code reviewer verifying a coding agent's step against real tool-execution evidence. "
        + "Trust the execution evidence (actual commands + their real output/exit codes) over the agent's own summary, "
        + "which may be mistaken or fabricated. Respond with ONLY a JSON object, no prose and no code fences: "
        + "{\"verdict\":\"pass\" or \"fail\",\"notes\":\"short, specific feedback\"}. "
        + "Fail if the required command was never actually run, a required command failed, or a reported/saved value "
        + "is fabricated or a placeholder not present in the real output. Otherwise pass.";
}
