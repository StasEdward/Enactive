namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Templates;

internal sealed record CheckDecision(int Index, string Kind, string Reason, string Command);

/// <summary>What the diagnosis came to - decisions for every failed check, or why there are none - and what it cost.</summary>
internal sealed record CheckDiagnosisResult(IReadOnlyList<CheckDecision>? Decisions, string? Error, TokenUsage? Usage = null)
{
    public TokenUsage Spent => Usage ?? TokenUsage.None;
}

/// <summary>The planner, never the worker, adjudicates a failed model-proposed verification.
/// One read-only round; no tools and no permission to weaken user/template criteria.</summary>
internal static class CheckDiagnosis
{
    private const string Instruction =
        "You are the planner checking your proposed verification, not executing work. "
        + "Commands start in the CURRENT WORKSPACE ROOT. A nonzero exit may mean a work defect, "
        + "a mistaken check (wrong target/shell/assumption), or insufficient information. Never move/copy "
        + "the project to fit a mistaken check. Judge against the ORIGINAL REQUEST. "
        + "Return {decisions:[{index,kind,reason,command}]} for every input once. kind=work means "
        + "a concrete defect for the worker: retain the exact command and explain the defect. "
        + "kind=check means your check is wrong: provide a corrected command checking the SAME "
        + "requirement, without weakening it or violating any original restriction on commands, tools, network or paths. "
        + "Permissions granted by the host do not waive task restrictions. If no compliant correction is known, use kind=unknown. "
        + "kind=unknown means do not modify the workspace. "
        + "Do not invent missing filesystem facts or mark a failure as success. No tools.";

    internal static async Task<CheckDiagnosisResult> RunAsync(string request, WorkContext context,
        IReadOnlyList<CriterionResult> failed, IChatProvider provider, string model, RunBudget budget, CancellationToken ct)
    {
        if (budget.TurnExhausted is { } spent) return new(null, spent);
        var body = JsonSerializer.Serialize(failed.Select((c, i) => new { index = i, c.Command, c.ExitCode, c.Detail }));
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(Instruction),
            ChatMessage.User(Planner.Where(context) + request + "\nFailed proposed checks:\n" + body)
        };

        // The round every structured answer is asked through (StructuredAnswer): a diagnosis that cannot be used is told
        // what was wrong with it once. It was asked once, by hand, and one wrong entry - an index out of range, a "work"
        // that changed the command - stopped the repair outright with the original criteria retained.
        var round = await StructuredAnswer.AskAsync(provider, messages,
            current => new ChatRequest(model, current, Temperature: 0, Purpose: GenerationPurpose.Planning, OutputTokenLimit: 2048)
                { RetryBudget = budget },
            (answer, _) => Read(answer, failed),
            errors => StructuredAnswer.Listed(errors,
                "Return the corrected {decisions:[{index,kind,reason,command}]}, one decision for every failed check."),
            budget.TurnExhaustedAfter, requireComplete: true, ct);

        // Without a diagnosis nothing is repaired, so the criteria stay as they were - said, as it was for an unusable one.
        var error = round.Shortfall("Check diagnosis") is { } shortfall ? shortfall + "; the original criteria are kept." : null;
        return new(round.Value, error, round.Usage);
    }

    /// <summary>
    /// The decisions, one for every failed check - or every way the answer falls short of that, said so the model can put
    /// it right. A "work" decision keeps the exact command; a "check" decision changes it; "unknown" changes nothing.
    /// </summary>
    internal static (IReadOnlyList<CheckDecision>? Value, IReadOnlyList<string> Errors) Read(string answer, IReadOnlyList<CriterionResult> failed)
    {
        if (ModelText.ExtractJsonObject(ModelText.StripThink(answer)) is not { } json)
            return (null, ["there is no JSON object in the answer"]);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("decisions", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return (null, ["the object has no \"decisions\" array"]);

            var found = new List<CheckDecision>();
            var errors = new List<string>();
            var seen = new HashSet<int>();
            foreach (var item in entries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("index", out var i) || !i.TryGetInt32(out var index)
                    || Text(item, "kind") is not { } kind || Text(item, "reason") is not { } reason || Text(item, "command") is not { } command)
                {
                    errors.Add("each decision needs an integer index and the strings kind, reason and command");
                    continue;
                }
                if (index < 0 || index >= failed.Count) errors.Add($"index {index} is not one of the failed checks (0 to {failed.Count - 1})");
                else if (!seen.Add(index)) errors.Add($"check {index} is decided more than once");
                else if (string.IsNullOrWhiteSpace(reason)) errors.Add($"check {index} has no reason");
                else if (kind is not ("work" or "check" or "unknown")) errors.Add($"check {index}: kind must be work, check or unknown, not \"{kind}\"");
                else if (kind == "check" && (string.IsNullOrWhiteSpace(command) || command == failed[index].Command))
                    errors.Add($"check {index}: a \"check\" decision must give a corrected command, different from the one that failed");
                else if (kind == "work" && command != failed[index].Command)
                    errors.Add($"check {index}: a \"work\" decision keeps the exact command that failed");
                else found.Add(new(index, kind, reason, command));
            }
            var missing = Enumerable.Range(0, failed.Count).Where(index => !seen.Contains(index)).ToArray();
            if (missing.Length > 0) errors.Add("no decision for check(s) " + string.Join(", ", missing));
            return errors.Count == 0 ? (found, []) : (null, errors.Distinct().ToArray());
        }
        catch (JsonException) { return (null, ["the answer is not valid JSON"]); }

        static string? Text(JsonElement item, string name)
            => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
