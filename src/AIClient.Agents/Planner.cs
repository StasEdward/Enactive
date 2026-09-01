namespace AIClient.Agents;

using System.Text.Json;
using AIClient.Core.Chat;
using AIClient.Core.Context;
using AIClient.Core.Providers;
using AIClient.Core.Tasks;

/// <summary>The result of the understand/plan phase.</summary>
public sealed record PlanResult(IntentDisposition Disposition, string Title, IReadOnlyList<string> Steps);

/// <summary>
/// Turns an intent into a routing decision + optional plan with one LLM call (PLAN_v2 §3, "Understand").
/// Falls back to a QuickAction if the model's answer can't be parsed.
/// </summary>
public sealed class Planner
{
    public async Task<PlanResult> PlanAsync(
        string request, WorkContext context, IChatProvider provider, string model, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(SystemPrompt),
            ChatMessage.User(request)
        };

        var completion = await provider.CompleteAsync(new ChatRequest(model, messages, Temperature: 0.0), ct);
        return Parse(completion.Message.Content ?? "", request);
    }

    private static PlanResult Parse(string text, string fallbackTitle)
    {
        var json = ExtractJson(StripThink(text));
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var disposition = root.TryGetProperty("disposition", out var d)
                    && string.Equals(d.GetString(), "task", StringComparison.OrdinalIgnoreCase)
                    ? IntentDisposition.Task
                    : IntentDisposition.QuickAction;

                var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? (t.GetString() ?? fallbackTitle)
                    : fallbackTitle;

                var steps = new List<string>();
                if (root.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in s.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } step)
                            steps.Add(step);
                    }
                }

                // A "task" with no steps is really a quick action.
                if (disposition == IntentDisposition.Task && steps.Count == 0)
                    disposition = IntentDisposition.QuickAction;

                return new PlanResult(disposition, Truncate(title, 80), steps);
            }
            catch (JsonException)
            {
                // fall through to the safe default
            }
        }

        return new PlanResult(IntentDisposition.QuickAction, Truncate(fallbackTitle, 80), Array.Empty<string>());
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

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private const string SystemPrompt =
        "You are a planning assistant for a developer agent. Decide whether the user's request is a simple "
        + "one-shot action or needs a short multi-step plan. Respond with ONLY a JSON object, no prose and no "
        + "code fences: {\"disposition\":\"quick_action\" or \"task\",\"title\":\"short title\",\"steps\":[\"step\",...]}. "
        + "Use \"quick_action\" for a single obvious action (steps empty). Use \"task\" for multi-step work with 2-5 "
        + "concrete steps. Keep the title under 8 words.";
}
