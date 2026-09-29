namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>What the step's criteria review is shown, beside the criteria: all of it assembled by the engine.</summary>
internal sealed record CriteriaReviewInput(
    string StepTitle, int StepNumber, string Report, string? HandedOn,
    IReadOnlyList<(string Path, string Text, bool Whole)> Files, EvidenceView Evidence,
    RequestObligations Obligations);

/// <summary>
/// A step judged against the semantic criteria its plan set - only those (Phase 1.4, 5.2). The plan's invariant: the
/// engine decides completion; judgement enters only through semantic criteria, and only with cited evidence. A step
/// review used to judge every sentence of the worker's report, and one "could not tell" about a figure the step never
/// had to produce left the step unconfirmed (run 793825, 2026-09-29: "38 and 16 tests" in a report, the step's work
/// done). Here the report is a claim, shown as one; each criterion is answered pass, fail or unknown, and a pass or a
/// fail cites evidence of a kind the criterion allows - a file read WHOLE (a read that covered it, or the file as the
/// engine shows it whole), a command that ran, or a call that succeeded. What cites nothing admissible is not taken.
/// </summary>
internal static class CriteriaReview
{
    private const string Instruction = """
        You judge one step of a run against the criteria its plan set for it - those criteria and nothing else. The step's
        report is the worker's claim, not evidence: judge each criterion from the evidence shown.
        For each criterion: pass - the evidence shows it holds; fail - the evidence shows it does not; unknown - it shows
        neither. A pass or a fail cites what shows it: calls [n] of a kind the criterion allows, or paths shown whole under
        FILES (they count as file_read). Kinds: file_read - a read of a file that covered it whole; command - a command that
        ran; call - any call that succeeded.
        Return ONLY one JSON object:
        {"criteria":[{"id":"C1","verdict":"pass|fail|unknown","reason":"...","calls":[n],"files":["path"]}]}
        One entry for every criterion, by id. A fail says what is wrong, concretely enough to be put right.
        """;

    public static async Task<ReviewResult> RunAsync(CriteriaReviewInput input, IReadOnlyList<SuccessCriterionDefinition> criteria,
        Func<ExecutedAction, EvidenceKind, bool> admits, IChatProvider provider, string model,
        Func<int, int, string?>? beforeRetry, CancellationToken ct)
    {
        var numbered = criteria.Select((c, i) => ($"C{i + 1}", c)).ToArray();
        var messages = new List<ChatMessage> { ChatMessage.System(Instruction), ChatMessage.User(Prompt(input, numbered)) };
        int prompt = 0, output = 0;
        int? cached = null, created = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0 && beforeRetry?.Invoke(prompt, output) is { } spent)
                return new ReviewResult(false, spent, prompt, output) { BudgetExhausted = spent };
            ChatCompletion completion;
            try
            {
                completion = await provider.CompleteAsync(GenerationAllowance.Fit(new ChatRequest(model, messages, Temperature: 0,
                    Purpose: GenerationPurpose.Review, OutputTokenLimit: 4096 + 512 * numbered.Length), provider), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new ReviewResult(false, "review error: " + ex.Message, prompt, output)
                    { IncompleteReason = "review error: " + ex.Message, VerdictUnavailable = true };
            }
            prompt += completion.PromptTokens ?? 0;
            output += completion.CompletionTokens ?? 0;
            cached = TokenCounts.Add(cached, completion.CachedPromptTokens);
            created = TokenCounts.Add(created, completion.CacheCreationPromptTokens);
            var answer = completion.Message.Content ?? "";
            var (result, errors) = Read(answer, input, numbered, admits);
            if (errors.Count == 0 && result is not null)
                return result with { PromptTokens = prompt, CompletionTokens = output, CachedPromptTokens = cached, CacheCreationPromptTokens = created };
            messages.Add(ChatMessage.Assistant(answer));
            messages.Add(ChatMessage.User("Your answer could not be used:\n" + string.Join("\n", errors.Select(e => "- " + e))
                + "\nReturn the complete corrected JSON object."));
        }
        const string why = "the criteria review could not be used after correction";
        return new ReviewResult(false, why, prompt, output)
            { IncompleteReason = why, VerdictUnavailable = true, CachedPromptTokens = cached, CacheCreationPromptTokens = created };
    }

    private static string Prompt(CriteriaReviewInput input, IReadOnlyList<(string Id, SuccessCriterionDefinition C)> criteria)
    {
        var sb = new StringBuilder();
        sb.AppendLine(input.Obligations.Describe());
        sb.AppendLine($"STEP {input.StepNumber}: {input.StepTitle}").AppendLine();
        sb.AppendLine("CRITERIA - judge these, and only these:");
        foreach (var (id, c) in criteria)
            sb.AppendLine($"- {id}: {c.Typed!.Text} (evidence: {TypedCriteria.KindsOf(c.Typed)})");
        sb.AppendLine();
        sb.AppendLine("REPORT - the worker's claim, not evidence:").AppendLine(input.Report).AppendLine();
        if (input.HandedOn is { Length: > 0 } handed)
            sb.AppendLine("HANDED ON by the step (accepted values):").AppendLine(handed).AppendLine();
        sb.AppendLine("FILES this step wrote, as they are now:");
        foreach (var (path, text, whole) in input.Files)
            sb.AppendLine($"--- {path}{(whole ? "" : " (shown in part - not whole)")}").AppendLine(text);
        if (input.Files.Count == 0) sb.AppendLine("(none)");
        sb.AppendLine();
        sb.AppendLine("TOOL CALLS (cite them by [n]):").AppendLine(input.Evidence.Text);
        return sb.ToString();
    }

    /// <summary>The answer checked and read; each criterion's verdict stands only on evidence of a kind it allows.</summary>
    internal static (ReviewResult? Result, IReadOnlyList<string> Errors) Read(string answer, CriteriaReviewInput input,
        IReadOnlyList<(string Id, SuccessCriterionDefinition C)> criteria, Func<ExecutedAction, EvidenceKind, bool> admits)
    {
        if (ModelText.ExtractJsonObject(ModelText.StripThink(answer)) is not { } json) return (null, ["no JSON object"]);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return (null, ["not JSON: " + ex.Message]); }
        using var owned = doc;
        string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        IEnumerable<JsonElement> Arr(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

        var errors = new List<string>();
        var whole = input.Files.Where(f => f.Whole).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shown = input.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var answered = Arr(doc.RootElement, "criteria").ToDictionary(e => Str(e, "id") ?? "", e => e, StringComparer.OrdinalIgnoreCase);
        var verdicts = new List<(SuccessCriterionDefinition C, string Verdict, string Why, IReadOnlyList<string> Cited)>();
        foreach (var (id, c) in criteria)
        {
            if (!answered.TryGetValue(id, out var e)) { errors.Add($"{id} is not answered"); continue; }
            var verdict = Str(e, "verdict");
            if (verdict is not ("pass" or "fail" or "unknown")) { errors.Add($"{id}: verdict must be pass, fail or unknown"); continue; }
            var kinds = c.Typed!.Kinds is { Count: > 0 } k ? k : [EvidenceKind.FileRead, EvidenceKind.Command, EvidenceKind.Call];
            var cited = new List<string>();
            foreach (var call in Arr(e, "calls").Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32()))
            {
                if (input.Evidence.Cited(call) is not { } action) { errors.Add($"{id}: call {call} is not in the evidence shown"); continue; }
                if (kinds.Any(kind => admits(action, kind))) cited.Add($"[{call}] {action.Tool}");
                else errors.Add($"{id}: call {call} ({action.Tool}) is not evidence of a kind this criterion allows "
                    + $"({TypedCriteria.KindsOf(c.Typed)}){(action.Tool is { } t && kinds.Contains(EvidenceKind.FileRead) ? " - a read counts only where it covered the file whole" : "")}");
            }
            foreach (var file in Arr(e, "files").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!))
            {
                if (!shown.Contains(file)) { errors.Add($"{id}: '{file}' is not among the FILES shown"); continue; }
                if (!kinds.Contains(EvidenceKind.FileRead)) { errors.Add($"{id}: a file is file_read evidence, which this criterion does not allow"); continue; }
                if (!whole.Contains(file)) { errors.Add($"{id}: '{file}' is shown in part, not whole"); continue; }
                cited.Add(file);
            }
            if (verdict != "unknown" && cited.Count == 0 && !errors.Any(x => x.StartsWith(id + ":", StringComparison.Ordinal)))
                errors.Add($"{id}: {verdict} needs cited evidence of a kind it allows ({TypedCriteria.KindsOf(c.Typed)})");
            verdicts.Add((c, verdict, Str(e, "reason") ?? "", cited));
        }
        if (errors.Count > 0) return (null, errors);

        string Line((SuccessCriterionDefinition C, string Verdict, string Why, IReadOnlyList<string> Cited) v)
            => $"{v.C.Typed!.Text} - {v.Verdict}: {v.Why}" + (v.Cited.Count > 0 ? $" [{string.Join(", ", v.Cited)}]" : "");
        var lines = string.Join("\n", verdicts.Select(Line));
        var failed = verdicts.Where(v => v.Verdict == "fail").ToArray();
        if (failed.Length > 0)
        {
            var advice = string.Join("\n", failed.Select(v => $"- {v.C.Typed!.Text}: {v.Why}"));
            return (new ReviewResult(false, lines) { RepairAdvice = "These criteria of the step are not met:\n" + advice }, []);
        }
        var unknown = verdicts.Where(v => v.Verdict == "unknown").ToArray();
        if (unknown.Length > 0)
            return (new ReviewResult(false, lines)
            {
                IncompleteReason = lines, VerdictUnavailable = true, Undecided = true,
                Unknowns = unknown.Select(v => new OpenItem(input.StepNumber, input.StepTitle, v.C.Typed!.Text!, v.Why, [])).ToArray()
            }, []);
        return (new ReviewResult(true, lines), []);
    }
}
