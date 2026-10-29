namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Core.Rules;

/// <summary>A workspace rule a person is about to keep goes against a Global rule: which one, and why.</summary>
public sealed record RuleConflict(string GlobalRule, string Why);

/// <summary>
/// Before a person keeps a rule for one workspace, whether it goes against a rule every workspace is given (Plan Phase 5.2).
/// One question to a strong model - a contradiction is a matter of meaning, not of words - asked only when there are Global
/// rules. An aid, not a boundary: the answer is shown, and the person keeps the rule anyway or changes it. Both rules would
/// otherwise reach every run here, the worker told two things it cannot both do, and the run would show it only as a
/// worker that did one of them.
/// </summary>
public static class RuleConsistency
{
    /// <summary>The answer is a short JSON object; past this the model is explaining, not answering.</summary>
    internal const int AnswerTokens = 400;

    internal const string Instruction = """
        A person keeps standing rules for an AI agent's work: GLOBAL rules for every workspace, and rules for one workspace.
        They are about to keep a NEW rule for one workspace. Say whether it CONTRADICTS a Global rule: an agent given both
        could not follow both at once. A rule that is narrower, stricter, about something else, or that repeats one, does
        not contradict it.
        Return ONLY one JSON object: {"contradicts": n, "why": "..."} - n is the number of the Global rule it contradicts,
        0 for none; "why" says in one sentence what an agent could not do at once.
        """;

    /// <summary>The Global rule the new one contradicts, or null - for none, for no Global rule, or for an answer that says nothing.</summary>
    public static async Task<RuleConflict?> CheckAsync(string text, IReadOnlyList<RuleEntry> global, IChatProvider provider, string model,
        CancellationToken ct)
    {
        var confirmed = global.Where(r => r.Status == RuleStatus.Confirmed).OrderBy(r => r.At).ToArray();
        if (confirmed.Length == 0 || string.IsNullOrWhiteSpace(text))
            return null;
        var sb = new StringBuilder("GLOBAL rules:\n");
        for (var i = 0; i < confirmed.Length; i++)
            sb.AppendLine($"{i + 1}. {RuleText.Flat(confirmed[i].Text, AppliedRules.MaxRuleChars)}");
        sb.AppendLine().AppendLine("NEW rule for one workspace:").AppendLine(RuleText.Flat(text, AppliedRules.MaxRuleChars));
        var completion = await provider.CompleteAsync(new ChatRequest(model,
            [ChatMessage.System(Instruction), ChatMessage.User(sb.ToString())], Temperature: 0, OutputTokenLimit: AnswerTokens), ct);
        // Cut off at its limit, the JSON is a fragment: read, it could say "0" for an answer that was going to name a rule.
        // Not read - the caller tells the person the rule was not checked (LessonsViewModel), as a review cut off is refused.
        if (completion.FinishReason == "length")
            throw new InvalidOperationException($"the answer was cut off at its {AnswerTokens}-token limit");
        return Read(completion.Message.Content, confirmed);
    }

    /// <summary>The answer read field by field; anything unreadable is no conflict - an aid that cannot answer says nothing.</summary>
    internal static RuleConflict? Read(string? answer, IReadOnlyList<RuleEntry> confirmed)
    {
        if (answer is null)
            return null;
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(answer[start..(end + 1)]);
            var root = doc.RootElement;
            if (!root.TryGetProperty("contradicts", out var n) || !n.TryGetInt32(out var number) || number < 1 || number > confirmed.Count)
                return null;
            var why = root.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? "" : "";
            return new RuleConflict(confirmed[number - 1].Text, why.Trim());
        }
        catch (JsonException) { return null; }
    }
}
