namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Templates;

internal sealed record CheckDecision(int Index, string Kind, string Reason, string Command);
internal sealed record CheckDiagnosisResult(IReadOnlyList<CheckDecision>? Decisions, ChatCompletion? Completion, string? Error);

/// <summary>The planner, never the worker, adjudicates a failed model-proposed verification.
/// One read-only turn; no tools and no permission to weaken user/template criteria.</summary>
internal static class CheckDiagnosis
{
    internal static async Task<CheckDiagnosisResult> RunAsync(string request, WorkContext context,
        IReadOnlyList<CriterionResult> failed, IChatProvider provider, string model, RunBudget budget, CancellationToken ct)
    {
        if (budget.TurnExhausted is { } spent) return new(null, null, spent);
        ChatCompletion? completion = null;
        try
        {
            var body = JsonSerializer.Serialize(failed.Select((c, i) => new { index = i, c.Command, c.ExitCode, c.Detail }));
            completion = await provider.CompleteAsync(GenerationAllowance.Fit(new(model,
                [ChatMessage.System("You are the planner checking your proposed verification, not executing work. "
                    + "Commands start in the CURRENT WORKSPACE ROOT. A nonzero exit may mean a work defect, "
                    + "a mistaken check (wrong target/shell/assumption), or insufficient information. Never move/copy "
                    + "the project to fit a mistaken check. Judge against the ORIGINAL REQUEST. "
                    + "Return {decisions:[{index,kind,reason,command}]} for every input once. kind=work means "
                    + "a concrete defect for the worker: retain the exact command and explain the defect. "
                    + "kind=check means your check is wrong: provide a corrected command checking the SAME "
                    + "requirement, without weakening it or violating any original restriction on commands, tools, network or paths. "
                    + "Permissions granted by the host do not waive task restrictions. If no compliant correction is known, use kind=unknown. "
                    + "kind=unknown means do not modify the workspace. "
                    + "Do not invent missing filesystem facts or mark a failure as success. No tools."),
                 ChatMessage.User(Planner.Where(context) + request + "\nFailed proposed checks:\n" + body)],
                Temperature: 0, Purpose: GenerationPurpose.Planning, OutputTokenLimit: 2048)
                { RetryBudget = budget }, provider), ct);
            if (completion.FinishReason is "length" or "max_tokens" || completion.Message.ToolCalls is { Count: > 0 })
                return new(null, completion, "Check diagnosis was incomplete.");
            using var doc = JsonDocument.Parse(ModelText.ExtractJsonObject(ModelText.StripThink(completion.Message.Content ?? "")) ?? "{}");
            var entries = doc.RootElement.GetProperty("decisions");
            var found = new List<CheckDecision>();
            var seen = new HashSet<int>();
            foreach (var item in entries.EnumerateArray())
            {
                var index = item.GetProperty("index").GetInt32();
                var kind = item.GetProperty("kind").GetString()!;
                var reason = item.GetProperty("reason").GetString()!;
                var command = item.GetProperty("command").GetString()!;
                if (index < 0 || index >= failed.Count || !seen.Add(index) || string.IsNullOrWhiteSpace(reason)
                    || kind is not ("work" or "check" or "unknown")
                    || (kind == "check" && (string.IsNullOrWhiteSpace(command) || command == failed[index].Command))
                    || (kind == "work" && command != failed[index].Command))
                    return new(null, completion, "Invalid check diagnosis; original criteria retained.");
                found.Add(new(index, kind, reason, command));
            }
            return seen.Count == failed.Count ? new(found, completion, null)
                : new(null, completion, "Check diagnosis omitted criteria; original criteria retained.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(null, completion, "Check diagnosis failed: " + ex.Message); }
    }
}
