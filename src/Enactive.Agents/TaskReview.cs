namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>What a step review could not establish: the step, what it was about, why, and the calls it named.</summary>
public sealed record OpenItem(int Step, string StepTitle, string Label, string Reason, IReadOnlyList<string> Calls);

/// <summary>What the engine assembled for the task review - never the transcript.</summary>
internal sealed record TaskReviewInput(
    RequestObligations Obligations,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Checks,
    IReadOnlyList<(string Path, string Text)> Files,
    EvidenceView Evidence,
    IReadOnlyList<OpenItem> Open);

/// <summary>The task review's answer as the engine reads it: the outcome it supports, or none, and why.</summary>
internal sealed record TaskReviewResult(RunOutcomeKind? Outcome, string Reason, IReadOnlyList<(OpenItem Item, string Verdict, string Why)> Items,
    int PromptTokens, int CompletionTokens, int? CachedPromptTokens, int? CacheCreationPromptTokens)
{
    public static TaskReviewResult Unavailable(string why, int prompt = 0, int completion = 0)
        => new(null, why, [], prompt, completion, null, null);
}

/// <summary>
/// Phase 9: the run judged as a whole, where its steps could not be. Step reviews judge one step each, against what
/// that step showed; what they could not establish stays open, and the run is Incomplete on it even when a later step
/// showed it (run feed29, 2026-09-29: coverage per class "not visible in the excerpt", then shown by the next step's
/// files and test runs). The task review is shown what the engine assembled - the request, the plan and how each step
/// ended, what the steps handed on, the engine's own checks, the files as the run leaves them, the calls of the whole
/// run - and answers each open question, each obligation of the request, and any contradiction between the results.
///
/// <para>It moves the outcome only on evidence it cites: every open question confirmed and every obligation met is
/// Completed; anything refuted, an obligation not met or a contradiction is Failed; the rest stays Incomplete. It is
/// asked only where the outcome can still become Completed (only DONE, NOT VERIFIED steps short of it), and a review
/// that cannot answer changes nothing.</para>
/// </summary>
internal static class TaskReview
{
    internal const int MaxFileChars = 12_000;
    internal const int MaxFilesChars = 48_000;
    internal const int EvidenceChars = 24_000;

    private const string Instruction = """
        You review a finished run as a whole. Its steps were reviewed one by one, and some things could not be established
        within a step; later steps may show them. You are shown what the engine assembled: the request, the plan and how each
        step ended, what the steps handed on, the engine's own checks, the files as the run leaves them, and the tool calls of
        the whole run. Decide from this evidence only; a claim in a step's report is not evidence of itself.
        Return ONLY one JSON object:
        {"open_items":[{"id":"Q1","verdict":"confirmed|refuted|still-unknown","reason":"...","calls":[n],"files":["path"]}],
         "obligations":[{"id":"O001","verdict":"pass|fail|unknown","reason":"...","calls":[n],"files":["path"]}],
         "inconsistencies":[{"finding":"...","calls":[n],"files":["path"]}],
         "notes":"..."}
        One entry for every open item and for every obligation, by id. confirmed, refuted, pass and fail each cite at least
        one call [n] from the evidence or one path shown under FILES. confirmed: the evidence shows it true; refuted: it shows
        it false; still-unknown: it shows neither. An obligation passes when the run as a whole does what that part of the
        request asks. An inconsistency is a contradiction between the run's results - a report against the files it describes,
        one step's result against another's - and is cited; do not report style or wording. [] when there is none.
        """;

    public static async Task<TaskReviewResult> RunAsync(TaskReviewInput input, IChatProvider provider, string model,
        Func<int, int, string?>? beforeRetry, CancellationToken ct)
    {
        var questions = input.Open.Select((item, i) => ($"Q{i + 1}", item)).ToArray();
        var messages = new List<ChatMessage> { ChatMessage.System(Instruction), ChatMessage.User(Prompt(input, questions)) };
        int prompt = 0, output = 0;
        int? cached = null, created = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0 && beforeRetry?.Invoke(prompt, output) is { } spent)
                return TaskReviewResult.Unavailable(spent, prompt, output);
            ChatCompletion completion;
            try
            {
                completion = await provider.CompleteAsync(GenerationAllowance.Fit(new ChatRequest(model, messages, Temperature: 0,
                    Purpose: GenerationPurpose.Review, OutputTokenLimit: 8192), provider), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return TaskReviewResult.Unavailable("task review error: " + ex.Message, prompt, output); }
            prompt += completion.PromptTokens ?? 0;
            output += completion.CompletionTokens ?? 0;
            cached = TokenCounts.Add(cached, completion.CachedPromptTokens);
            created = TokenCounts.Add(created, completion.CacheCreationPromptTokens);
            var answer = completion.Message.Content ?? "";
            var (read, errors) = Read(answer, input, questions);
            if (errors.Count == 0 && read is not null)
                return read with { PromptTokens = prompt, CompletionTokens = output, CachedPromptTokens = cached, CacheCreationPromptTokens = created };
            messages.Add(ChatMessage.Assistant(answer));
            messages.Add(ChatMessage.User("Your answer could not be used:\n" + string.Join("\n", errors.Select(e => "- " + e))
                + "\nReturn the complete corrected JSON object."));
        }
        return TaskReviewResult.Unavailable("task review answer could not be used after correction", prompt, output);
    }

    private static string Prompt(TaskReviewInput input, IReadOnlyList<(string Id, OpenItem Item)> questions)
    {
        var sb = new StringBuilder();
        sb.AppendLine(input.Obligations.Describe());
        sb.AppendLine("PLAN - how each step ended:").AppendJoin('\n', input.Steps).AppendLine().AppendLine();
        if (input.Outputs.Count > 0)
            sb.AppendLine("HANDED ON by the steps (accepted values):").AppendJoin('\n', input.Outputs).AppendLine().AppendLine();
        if (input.Checks.Count > 0)
            sb.AppendLine("CHECKED BY THE ENGINE ITSELF:").AppendJoin('\n', input.Checks).AppendLine().AppendLine();
        sb.AppendLine("OPEN - what the step reviews could not establish:");
        foreach (var (id, item) in questions)
            sb.AppendLine($"- {id} (step {item.Step}, {item.StepTitle}) {item.Label}: {item.Reason}"
                + (item.Calls.Count > 0 ? " [the step review looked at: " + string.Join("; ", item.Calls) + "]" : ""));
        sb.AppendLine();
        sb.AppendLine("FILES as the run leaves them:");
        foreach (var (path, text) in input.Files)
            sb.AppendLine($"--- {path}").AppendLine(text);
        if (input.Files.Count == 0) sb.AppendLine("(none)");
        sb.AppendLine();
        sb.AppendLine("TOOL CALLS of the whole run (cite them by [n]):").AppendLine(input.Evidence.Text);
        return sb.ToString();
    }

    /// <summary>The answer checked and read; the outcome is the engine's reading of it, not the reviewer's own word.</summary>
    internal static (TaskReviewResult? Result, IReadOnlyList<string> Errors) Read(string answer, TaskReviewInput input,
        IReadOnlyList<(string Id, OpenItem Item)> questions)
    {
        var errors = new List<string>();
        if (ModelText.ExtractJsonObject(ModelText.StripThink(answer)) is not { } json) return (null, ["no JSON object"]);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return (null, ["not JSON: " + ex.Message]); }
        using var owned = doc;
        var root = doc.RootElement;
        var files = input.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        IEnumerable<JsonElement> Arr(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
        bool Cites(JsonElement e, string path)
        {
            var calls = Arr(e, "calls").Where(c => c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out _)).Select(c => c.GetInt32()).ToArray();
            var named = Arr(e, "files").Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()!).ToArray();
            foreach (var call in calls.Where(c => input.Evidence.Cited(c) is null))
                errors.Add($"{path}: call {call} is not in the evidence shown");
            foreach (var file in named.Where(f => !files.Contains(f)))
                errors.Add($"{path}: '{file}' is not among the FILES shown");
            return calls.Any(c => input.Evidence.Cited(c) is not null) || named.Any(files.Contains);
        }

        var items = new List<(OpenItem, string, string)>();
        var answered = Arr(root, "open_items").ToDictionary(e => Str(e, "id") ?? "", e => e, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, item) in questions)
        {
            if (!answered.TryGetValue(id, out var e)) { errors.Add($"open_items: {id} is not answered"); continue; }
            var verdict = Str(e, "verdict");
            if (verdict is not ("confirmed" or "refuted" or "still-unknown")) { errors.Add($"open_items {id}: verdict must be confirmed, refuted or still-unknown"); continue; }
            if (verdict != "still-unknown" && !Cites(e, $"open_items {id}")) errors.Add($"open_items {id}: {verdict} needs a cited call or file");
            items.Add((item, verdict, Str(e, "reason") ?? ""));
        }

        var obligations = new List<(string Id, string Verdict, string Why)>();
        var assessed = Arr(root, "obligations").ToDictionary(e => Str(e, "id") ?? "", e => e, StringComparer.OrdinalIgnoreCase);
        foreach (var o in input.Obligations.Items)
        {
            if (!assessed.TryGetValue(o.Id, out var e)) { errors.Add($"obligations: {o.Id} is not assessed"); continue; }
            var verdict = Str(e, "verdict");
            if (verdict is not ("pass" or "fail" or "unknown")) { errors.Add($"obligations {o.Id}: verdict must be pass, fail or unknown"); continue; }
            if (verdict != "unknown" && !Cites(e, $"obligations {o.Id}")) errors.Add($"obligations {o.Id}: {verdict} needs a cited call or file");
            obligations.Add((o.Id, verdict, Str(e, "reason") ?? ""));
        }

        var inconsistencies = new List<string>();
        var n = 0;
        foreach (var e in Arr(root, "inconsistencies"))
        {
            var finding = Str(e, "finding");
            if (string.IsNullOrWhiteSpace(finding)) { errors.Add($"inconsistencies[{n}]: no finding"); n++; continue; }
            if (!Cites(e, $"inconsistencies[{n}]")) errors.Add($"inconsistencies[{n}]: needs a cited call or file");
            inconsistencies.Add(finding!);
            n++;
        }
        if (errors.Count > 0) return (null, errors);

        string Line(OpenItem i, string why) => $"[{i.Step}] {i.Label}: {why}";
        var refuted = items.Where(i => i.Item2 == "refuted").Select(i => "refuted - " + Line(i.Item1, i.Item3))
            .Concat(obligations.Where(o => o.Verdict == "fail").Select(o => $"{o.Id} not met - {o.Why}"))
            .Concat(inconsistencies.Select(f => "inconsistent - " + f)).ToArray();
        if (refuted.Length > 0)
            return (new(RunOutcomeKind.Failed, "Task review: " + string.Join("; ", refuted), items, 0, 0, null, null), []);
        var open = items.Where(i => i.Item2 == "still-unknown").Select(i => Line(i.Item1, i.Item3))
            .Concat(obligations.Where(o => o.Verdict == "unknown").Select(o => $"{o.Id}: {o.Why}")).ToArray();
        if (open.Length > 0)
            return (new(RunOutcomeKind.Incomplete, "Task review: still not established - " + string.Join("; ", open), items, 0, 0, null, null), []);
        return (new(RunOutcomeKind.Completed, "Task review: what the steps left open is shown by the run - "
            + string.Join("; ", items.Select(i => Line(i.Item1, i.Item3))), items, 0, 0, null, null), []);
    }
}
