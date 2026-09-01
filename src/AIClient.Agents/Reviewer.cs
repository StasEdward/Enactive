namespace AIClient.Agents;

using System.Text.Json;
using AIClient.Core.Chat;
using AIClient.Core.Providers;

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
        string stepTitle, string coderOutput, IReadOnlyList<string> artifacts,
        IChatProvider provider, string model, CancellationToken ct)
    {
        var files = artifacts.Count == 0 ? "(none)" : string.Join(", ", artifacts);
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(SystemPrompt),
            ChatMessage.User(
                $"Step: {stepTitle}\n\n"
                + $"What the coding agent reported:\n{coderOutput}\n\n"
                + $"Files changed: {files}\n\n"
                + "Did the coder correctly and completely accomplish this step?")
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
        "You are a senior code reviewer. Judge whether the coding agent correctly and completely accomplished "
        + "the given step. Respond with ONLY a JSON object, no prose and no code fences: "
        + "{\"verdict\":\"pass\" or \"fail\",\"notes\":\"short, specific feedback\"}. Pass unless there is a real problem.";
}
