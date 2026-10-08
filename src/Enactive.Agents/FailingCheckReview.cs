namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Templates;

/// <summary>
/// A final check the planner proposed that already FAILS before any work, asked of once: does the request ask for what
/// would make it pass?
///
/// <para><b>Why.</b> A check that fails before the work and passes after is the proof the work was done - when the work
/// is what makes it pass. When its failure lies outside what was asked, it can only pass by the worker going outside the
/// request. Benchmark scenario build-error, 2026-09-30: "add Median to Stats; leave the rest of the code alone" in a
/// project that did not build before; the planner proposed "the build passes", the worker fixed another file's error
/// to satisfy it, the review let it through, and the run was Completed against the request's own ban. Which of the two
/// a failing check is, is the request's meaning - a judgement, made once here by the planning model with the request
/// and the check's own output in front of it, not by the worker under the check's pressure.</para>
///
/// <para>A check it drops is not replaced by nothing: the engine's own "no new build errors" still compares the build
/// with the one before the work. No answer, an unusable one, or none about a check: the check stays as it was.</para>
/// </summary>
internal static class FailingCheckReview
{
    /// <summary>How much of a failing check's output the decision is shown: its end, where a runner says what failed.</summary>
    internal const int OutputTailChars = 1500;

    private const string Instruction = """
        Before any work, the engine ran the final checks proposed for this request. The checks below already FAIL.
        A check that already fails can only pass after changes that remove what makes it fail. Decide for each one:
        keep - the request asks for those changes (fixing a build, making tests pass, producing what the check looks for);
        drop - what makes it fail lies outside what the request asks for, or in what the request says to leave alone, so
        making it pass would take the work beyond the request.
        Return ONLY JSON {"checks":[{"name":"...","keep":true|false,"reason":"..."}]} with every check listed once.
        """;

    internal sealed record Decision(IReadOnlyList<(SuccessCriterionDefinition Check, string Reason)> Dropped,
        int PromptTokens, int CompletionTokens, int? CachedPromptTokens, int? CacheCreationPromptTokens, string? Problem = null);

    public static async Task<Decision> RunAsync(string request, IReadOnlyList<(SuccessCriterionDefinition Check, CriterionResult Before)> failing,
        IChatProvider provider, string model, RunBudget budget, int outputBudget, CancellationToken ct)
    {
        if (failing.Count == 0) return new([], 0, 0, null, null);
        var shown = failing.Select(f => new
        {
            name = f.Check.Name,
            command = f.Check.Command,
            exitCode = f.Before.ExitCode,
            output = Tail(f.Before.Output ?? f.Before.Detail ?? "")
        });
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(Instruction),
            ChatMessage.User(RequestObligations.ExecutionPrompt(request) + "\n\nChecks that already fail before any work:\n"
                + JsonSerializer.Serialize(shown))
        };

        // The round every structured answer is asked through (StructuredAnswer): an answer that cannot be used is told
        // what was wrong once, and the turn's budget is kept. This was asked once, by hand, and an unreadable answer
        // kept a check the request may not ask to pass; the budget it was handed was never looked at.
        var round = await StructuredAnswer.AskAsync(provider, messages,
            current => new ChatRequest(model, current, Temperature: 0, Purpose: GenerationPurpose.Planning,
                OutputTokenLimit: Math.Max(1, outputBudget)),
            (answer, _) => Read(answer, failing.Select(f => f.Check).ToArray()),
            errors => StructuredAnswer.Listed(errors,
                "Return ONLY JSON {\"checks\":[{\"name\":\"...\",\"keep\":true|false,\"reason\":\"...\"}]} with every check listed once."),
            budget.TurnExhaustedAfter, requireComplete: true, ct);

        var problem = round.Kind switch
        {
            AnswerKind.Answered => null,
            AnswerKind.Failed => "the decision failed: " + round.Problem,
            AnswerKind.OutOfBudget => "no budget was left to ask: " + round.Problem,
            _ => round.CutOff ? "the decision was cut off" : "the decision could not be read: " + string.Join("; ", round.Errors)
        };
        return new(round.Value ?? [], round.PromptTokens, round.CompletionTokens, round.CachedPromptTokens,
            round.CacheCreationPromptTokens, problem);
    }

    /// <summary>
    /// The checks an answer drops, with why. Lenient about each entry - one it cannot read, or one that keeps its
    /// check, leaves the check as planned - and strict only about the answer being the object asked for at all.
    /// </summary>
    private static (IReadOnlyList<(SuccessCriterionDefinition Check, string Reason)>? Value, IReadOnlyList<string> Errors) Read(
        string answer, IReadOnlyList<SuccessCriterionDefinition> failing)
    {
        if (ModelText.ExtractJsonObject(ModelText.StripThink(answer)) is not { } json)
            return (null, ["there is no JSON object in the answer"]);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
                return (null, ["the object has no \"checks\" array"]);
            var dropped = new List<(SuccessCriterionDefinition, string)>();
            foreach (var item in checks.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("keep", out var keep) || keep.ValueKind != JsonValueKind.False)
                    continue;
                var check = failing.FirstOrDefault(c => c.Name == name.GetString());
                var reason = item.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
                if (check is not null && dropped.All(d => d.Item1 != check)) dropped.Add((check, reason));
            }
            return (dropped, []);
        }
        catch (JsonException) { return (null, ["the answer is not valid JSON"]); }
    }

    private static string Tail(string text)
    {
        text = text.Trim();
        return text.Length <= OutputTailChars ? text
            : $"({text.Length - OutputTailChars} characters of the start not shown; the end follows) " + text[^OutputTailChars..];
    }
}
