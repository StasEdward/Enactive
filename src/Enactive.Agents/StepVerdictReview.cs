namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>What a step's verdict review is shown - all of it assembled by the engine.</summary>
internal sealed record StepVerdictInput(
    string Request, string StepTitle, int? StepNumber, IReadOnlyList<string> OtherSteps, string Report, string? HandedOn,
    IReadOnlyList<(string Path, string Text, bool Whole)> Files, EvidenceView Evidence);

/// <summary>
/// One short verdict on one step: did the worker do what this step was for, and is what it reported true? Pass or fail,
/// and what shows it.
///
/// <para><b>Why.</b> The step review this replaces asked for nine sections - every sentence of the report checked on its
/// own, the request's units mapped to steps, proofs, "not shown", repairs - with two numberings to keep apart. Most of what
/// went wrong on 2026-09-29 was that protocol, not the work: a step left unconfirmed over a figure no goal needed, a
/// requirement deferred to a final review that then failed on one misplaced field, answers corrected by parts that lost
/// what they were for. A task that took five minutes took twenty and failed. Here a step is judged on its own purpose;
/// the task is done when every step is, and the engine's own checks are green - no review of the whole run after it.</para>
///
/// <para>The one rule kept from everything before: a claim counts only where a recorded call or a file as it is now shows
/// it. What is not shown is a fail that says so, and the worker shows it - it does not become "could not tell".</para>
/// </summary>
internal static class StepVerdictReview
{
    private const string Instruction = """
        You check one step of a run: did the worker do what this step is for, and is what it reported true?
        Judge from the evidence shown - the tool calls the engine recorded, and the files as they are now. The worker's
        report is its claim: a claim counts only where a call or a file shows it.
        Return ONLY one JSON object: {"verdict":"pass"|"fail","reason":"...","calls":[n],"files":["path"]}
        pass: the step's purpose is done, and what the report says about it is true; cite the calls [n] and files that
        show it. fail: say concretely what is not done, not true, or not shown - so the worker can put it right.
        Judge this step only: what the other steps are for is theirs. Do not fail a step on style, on wording, or on a
        detail its purpose does not depend on.
        """;

    public static async Task<ReviewResult> RunAsync(StepVerdictInput input, IChatProvider provider, string model,
        Func<int, int, string?>? beforeRetry, CancellationToken ct)
    {
        var messages = new List<ChatMessage> { ChatMessage.System(Instruction), ChatMessage.User(Prompt(input)) };
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
                    Purpose: GenerationPurpose.Review, OutputTokenLimit: 2048), provider), ct);
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
            var (result, errors) = Read(answer, input);
            if (errors.Count == 0 && result is not null)
                return result with { PromptTokens = prompt, CompletionTokens = output, CachedPromptTokens = cached, CacheCreationPromptTokens = created };
            messages.Add(ChatMessage.Assistant(answer));
            messages.Add(ChatMessage.User("Your answer could not be used:\n" + string.Join("\n", errors.Select(e => "- " + e))
                + "\nReturn the corrected JSON object."));
        }
        const string why = "the step's review could not be used after correction";
        return new ReviewResult(false, why, prompt, output)
            { IncompleteReason = why, VerdictUnavailable = true, CachedPromptTokens = cached, CacheCreationPromptTokens = created };
    }

    private static string Prompt(StepVerdictInput input)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The request the run is for (context - this step does a part of it):").AppendLine(input.Request).AppendLine();
        sb.AppendLine(input.StepNumber is { } no ? $"THIS STEP ({no}): {input.StepTitle}" : $"THE WORK: {input.StepTitle}");
        if (input.OtherSteps.Count > 0)
            sb.AppendLine("Other steps of the plan (theirs, not this step's): " + string.Join("; ", input.OtherSteps));
        sb.AppendLine();
        sb.AppendLine("REPORT - the worker's claim:").AppendLine(input.Report).AppendLine();
        if (input.HandedOn is { Length: > 0 } handed)
            sb.AppendLine("HANDED ON by the step (accepted values):").AppendLine(handed).AppendLine();
        sb.AppendLine("FILES this step wrote, as they are now:");
        foreach (var (path, text, whole) in input.Files)
            sb.AppendLine($"--- {path}{(whole ? "" : " (shown in part)")}").AppendLine(text);
        if (input.Files.Count == 0) sb.AppendLine("(none)");
        sb.AppendLine();
        sb.AppendLine("TOOL CALLS (cite them by [n]):").AppendLine(input.Evidence.Text);
        return sb.ToString();
    }

    /// <summary>The answer checked and read. A pass must cite what shows it; a fail must say what is wrong.</summary>
    internal static (ReviewResult? Result, IReadOnlyList<string> Errors) Read(string answer, StepVerdictInput input)
    {
        if (ModelText.ExtractJsonObject(ModelText.StripThink(answer)) is not { } json) return (null, ["no JSON object"]);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return (null, ["not JSON: " + ex.Message]); }
        using var owned = doc;
        var root = doc.RootElement;
        string? Str(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        IEnumerable<JsonElement> Arr(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

        var errors = new List<string>();
        var verdict = Str("verdict");
        var reason = Str("reason")?.Trim() ?? "";
        if (verdict is not ("pass" or "fail")) errors.Add("verdict must be pass or fail");
        if (reason.Length == 0) errors.Add("reason is empty");
        var shown = input.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cited = 0;
        foreach (var call in Arr("calls").Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32()))
        {
            if (input.Evidence.Cited(call) is null) errors.Add($"call {call} is not in the evidence shown");
            else cited++;
        }
        foreach (var file in Arr("files").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!))
        {
            if (!shown.Contains(file)) errors.Add($"'{file}' is not among the FILES shown");
            else cited++;
        }
        // A step that made no call and wrote nothing has nothing to cite; anything else passes on what shows it.
        var anythingToCite = input.Evidence.VisibleActionIds.Count > 0 || input.Files.Count > 0;
        if (verdict == "pass" && cited == 0 && anythingToCite) errors.Add("a pass cites the calls or files that show the step done");
        if (errors.Count > 0) return (null, errors);

        return verdict == "pass"
            ? (new ReviewResult(true, reason), [])
            : (new ReviewResult(false, reason) { RepairAdvice = reason }, []);
    }
}
