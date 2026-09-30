namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        A change the step made that the request did not ask for is not a detail. Where the request says to leave something
        alone, or limits what the work may change, a step that changed it fails - name the change - however right the
        change itself may be.
        """;

    // The last paragraph of the instruction: benchmark scenario build-error, 2026-09-30. "Add Median; leave the rest of the
    // code alone" - the worker also fixed another file's error, and the review passed it as "out of scope but harmless",
    // twice. Measured on the twenty step reviews of both benchmark runs, each twice, with and without it: the review of
    // that step went from pass to fail (2 of 2), naming the change; no other review failed because of it.

    /// <summary>
    /// Run 1ec9e8, 2026-09-29: a disk report said "Total capacity: ~12 231 GB - 38.1 %"; its rows add up to 12 301.18 GB
    /// and 37.9 %. No call had computed a total - the worker added in its head - and three step reviews passed it with
    /// every row in front of them. Measured on ten recorded reviews of three runs, each twice, and the corrected report
    /// three times: told to work a derived figure out, the reviewer failed both steps that carried the wrong total, with the
    /// sum, and passed everything else; told that such a figure counts only where a call computed it, it passed the
    /// wrong total as "simple sums, consistent with what was measured".
    /// </summary>
    private const string DerivedFigures = """

        A figure the work derives from other figures - a total, a difference, a percentage, an average - is a claim too:
        work it out from the values the calls show, and fail it if it is wrong. Rounding, a change of units, and figures
        copied as they are from a call's output are not derived.
        """;

    public static async Task<ReviewResult> RunAsync(StepVerdictInput input, IChatProvider provider, string model,
        Func<int, int, string?>? beforeRetry, CancellationToken ct, bool checkDerivedFigures = false)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(checkDerivedFigures ? Instruction + DerivedFigures : Instruction), ChatMessage.User(Prompt(input))
        };
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
            // An answer cut off at its length, or one that called for a tool, is not a finished verdict, however whole the
            // JSON in it looks - the earlier review took neither as final (code review of engeen_v4, P2).
            var unfinished = completion.FinishReason is "length" or "max_tokens"
                ? "your answer was cut off at its length limit; return the JSON object alone, with a short reason"
                : completion.Message.ToolCalls is { Count: > 0 }
                    ? "no tools are offered here; return the JSON object alone"
                    : null;
            var (result, errors) = unfinished is null ? Read(answer, input) : (null, [unfinished]);
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
        sb.AppendLine(LocalTime.Line() + " - a date or time the work states is judged against this, not against what you remember.");
        sb.AppendLine();
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
        var didNotRun = new List<int>();
        foreach (var call in Arr("calls").Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32()))
        {
            if (input.Evidence.Cited(call) is not { } action) errors.Add($"call {call} is not in the evidence shown");
            else if (Shows(action)) cited++;
            else didNotRun.Add(call);
        }
        foreach (var file in Arr("files").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!))
        {
            if (shown.Contains(file)) cited++;
            // A file the calls shown work with - the attachment of a mail, a page an earlier step wrote - that this step
            // did not write: not shown, so it counts for nothing, but it is not a mistake worth a second round. In every
            // disk run of 2026-09-29 the step that mailed the report cited it, was told "not among the FILES shown", and
            // answered the same pass again without it. A file no call mentions at all still goes back.
            else if (!Mentioned(file, input.Evidence.Text)) errors.Add($"'{file}' is not among the FILES shown");
        }
        // A step that made no call and wrote nothing has nothing to cite; anything else passes on what shows it.
        var anythingToCite = input.Evidence.VisibleActionIds.Count > 0 || input.Files.Count > 0;
        if (verdict == "pass" && cited == 0 && anythingToCite)
            errors.Add(didNotRun.Count > 0
                ? $"a pass cites the calls or files that show the step done; {string.Join(", ", didNotRun.Select(n => $"[{n}]"))} "
                  + "did not run or did not do what it was called for (refused, or a tool that failed), so it shows nothing done"
                : "a pass cites the calls or files that show the step done");
        if (errors.Count > 0) return (null, errors);

        return verdict == "pass"
            ? (new ReviewResult(true, reason), [])
            : (new ReviewResult(false, reason) { RepairAdvice = reason }, []);
    }

    /// <summary>
    /// Whether a call can show a step done. Code review of engeen_v4, P1: a pass citing only a refused send_email was
    /// accepted - a call being in the list is not the call having happened. One that was refused never ran; a tool
    /// that failed without running a process (a mail not sent, an edit that did not apply) did not do what it was called
    /// for. A process that ran shows what it showed, whatever its exit code: a failure can be the very result asked for
    /// (a test that must fail before the fix), and whether it is, is the reviewer's to say - as it was the earlier
    /// review's "expected-failure".
    /// </summary>
    private static bool Shows(ExecutedAction action)
        => action.Outcome switch
        {
            ActionOutcome.Refused => false,
            ActionOutcome.Failed => action.ExitCode is not null,
            _ => true
        };

    /// <summary>Whether the evidence names this path - as written, with either slash, or escaped in a call's JSON.</summary>
    private static bool Mentioned(string path, string evidence)
    {
        var plain = path.Trim().Replace('\\', '/');
        if (plain.StartsWith("./", StringComparison.Ordinal)) plain = plain[2..];
        if (plain.Length == 0) return false;
        // Not part of a longer name: "report.md" is not mentioned by "old_report.md" or "docs/report.md.bak".
        return new[] { plain, plain.Replace('/', '\\'), plain.Replace("/", @"\\") }.Distinct().Any(form =>
            Regex.IsMatch(evidence, @"(?<![\w.\-])" + Regex.Escape(form) + @"(?![\w\-]|\.\w)", RegexOptions.IgnoreCase));
    }
}
