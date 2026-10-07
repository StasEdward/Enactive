namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Tools;

/// <summary>What a step's verdict review is shown - all of it assembled by the engine.</summary>
/// <summary>A file a step changed, as a review is shown it: <paramref name="Heading"/> says what the text is, when it is not
/// simply the file - a diff, a deletion, who changed it. <paramref name="Whole"/>: the file as it is now is in the text whole.</summary>
internal sealed record ShownFile(string Path, string Text, bool Whole, string? Heading = null);

internal sealed record StepVerdictInput(
    string Request, string StepTitle, int? StepNumber, IReadOnlyList<string> OtherSteps, string Report, string? HandedOn,
    IReadOnlyList<ShownFile> Files, EvidenceView Evidence,
    // What the step is, from the plan (FanOut.ScopeNote, a read-only plan): the item it is for and the document the engine
    // assembles, or that it changes nothing. The earlier review was told; this one was not (code review of the move to
    // one short review, 2026-09-30) - and a step for one item was failed for "no findings file" in run d91b6a45.
    string? ScopeNote = null,
    // The lines of the request the plan gives this step. The earlier review judged each step against them; this one saw
    // the title alone, and a line the title did not name was judged by no one (code review of the move, 2026-09-30).
    IReadOnlyList<string>? Owns = null,
    // What the request forbids, as the check of the plan found it (TaskRestriction): the earlier review classified it
    // again and failed the step in code on a recorded deletion; the tools refuse the ones they recognise, and this
    // review, shown every deletion now, is told the rule (code review of the move to one short review, 2026-09-30).
    IReadOnlyList<TaskRestriction>? Forbidden = null);

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
        A call marked NOTHING THERE ran and answered - a file that is absent, a search that found nothing: it is no error, and
        can be exactly what shows a step done.
        Return ONLY one JSON object: {"verdict":"pass"|"fail","reason":"...","calls":[n],"files":["path"]}
        pass: the step's purpose is done, and what the report says about it is true; cite the calls [n] and files that
        show it. fail: say concretely what is not done, not true, or not shown - so the worker can put it right.
        A fail where a file the step made or changed is right as it is - asked for, and nothing wrong in it - names it in
        "keep":["path"]: if the step is rejected, the files named are kept and the rest of what it changed is put back. A file
        with something wrong in it, or a change the request did not ask for, is not kept.
        A step that reports something done or so - by itself, or by a step before it - has it only where a call shows it,
        made after the work it rests on; what would follow from the work is not shown, and neither are the values a step
        handed on. With no such call, it fails.
        Judge this step only: what the other steps are for is theirs. Do not fail a step on style, on wording, or on a
        detail its purpose does not depend on.
        A change the step made that the request did not ask for is not a detail. Where the request says to leave something
        alone, or limits what the work may change, a step that changed it fails - name the change - however right the
        change itself may be.
        """;

    // NOTHING THERE: the journal's mark for a call that answered with an absence. The earlier review was told it is no
    // error; this one was not (2026-10-01). Measured on the recorded short reviews since the move (43 with NOTHING THERE or
    // expectedExitCodes in their evidence, then the four that differed three times more): no verdict made wrong by it, and
    // of the four, 9 of 12 right with it against 7 of 12 without. The earlier review's word on expectedExitCodes was
    // measured too, and left out: no recorded review needed it, and with it the four came to 8 of 12 either way.

    // "Done or so ... only where a call shows it, made after the work": the earlier review's "nothing-to-do" answer was
    // believed only with the calls that looked (Proof.cs), and this review was first told so in those words - "a call it
    // made shows it looked". Measured 2026-10-01 on setup-step's second step, after the first had generated the missing
    // file: told that, Sonnet passed the step where nobody ran the tests after the fix (5 of 6 - "the fix was applied, so
    // they pass", one answer citing the values step 1 handed on), and DeepSeek failed it where step 1 had run them (7 of
    // 12). Worded as it is now: DeepSeek 72 of 72 over those and fifteen recorded right passes, Sonnet 2 of 3 on the step
    // nobody checked against 1 of 3, and every right pass kept.

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
    ///
    /// <para>Always told, since 2026-10-01. It was a setting - on in the application's, off in an engine built without
    /// them - so the same step was reviewed by two rules depending on who built the engine; no run had turned it off.</para>
    ///
    /// <para><b>A selection is derived too.</b> Run 0a2be9, 2026-10-05: "the five processes that use the most memory"
    /// were picked by a local worker from 370 rows in its head - three of the five wrong - and the review, shown the
    /// result with 18,776 characters cut from its middle, checked that the five values were in it and passed. Asked again
    /// six times each (Sonnet 5): without the sentence, the wrong five passed 6 of 6 on the shortened result; with it they
    /// failed 6 of 6. The right five failed 6 of 6 with it as well - on a shortened result a selection cannot be checked,
    /// and that is the honest verdict; shown the whole result, the right five passed and the wrong five failed 6 of 6 under
    /// either wording. The nineteen recorded reviews of 3-4 October, three times each, came out 42 of 57 right with the
    /// sentence and 40 of 57 without, no review that should pass failing because of it.</para>
    /// </summary>
    private const string DerivedFigures = """

        A figure the work derives from other figures - a total, a difference, a percentage, an average - is a claim too:
        work it out from the values the calls show, and fail it if it is wrong. Rounding, a change of units, and figures
        copied as they are from a call's output are not derived.
        A selection the work makes from a result - the largest, the first five, the ones that match, that none does - is
        derived the same way: check it against every row of the result. Where the result is shortened and the rows the
        selection depends on are not shown, the selection is not shown: fail it, and say which result has to be narrowed or
        put in order by a call for the selection to be checked.
        """;

    /// <param name="budget">Why the reviewer may not be asked (again), given what this review has spent - or null when it may.</param>
    public static async Task<ReviewResult> RunAsync(StepVerdictInput input, IChatProvider provider, string model,
        Func<int, int, string?>? budget, CancellationToken ct)
    {
        var round = await StructuredAnswer.AskAsync(provider,
            [ChatMessage.System(Instruction + DerivedFigures), ChatMessage.User(Prompt(input))],
            messages => new ChatRequest(model, messages, Temperature: 0, Purpose: GenerationPurpose.Review, OutputTokenLimit: 2048),
            (answer, _) => Read(answer, input),
            errors => StructuredAnswer.Listed(errors, "Return the corrected JSON object."),
            budget, requireComplete: true, ct);

        return ReviewResult.Of(round.Kind switch
        {
            AnswerKind.Answered => round.Value!,
            AnswerKind.OutOfBudget => new ReviewVerdict.OutOfBudget(round.Problem!),
            AnswerKind.Failed => new ReviewVerdict.Unavailable("review error: " + round.Problem),
            _ => new ReviewVerdict.Unavailable("the step's review could not be used after correction")
        }, round);
    }

    private static string Prompt(StepVerdictInput input)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The request the run is for (context - this step does a part of it):").AppendLine(input.Request).AppendLine();
        sb.AppendLine(LocalTime.Line() + " - a date or time the work states is judged against this, not against what you remember.");
        sb.AppendLine();
        sb.AppendLine(input.StepNumber is { } no ? $"THIS STEP ({no}): {input.StepTitle}" : $"THE WORK: {input.StepTitle}");
        if (input.ScopeNote is { Length: > 0 } scope)
            sb.AppendLine("What this step is, from the plan: " + scope);
        foreach (var rule in input.Forbidden ?? [])
            if (rule.Effect == ForbiddenTaskEffect.FileDeletion)
                sb.AppendLine($"The request forbids deleting files (\"{rule.SourceQuote}\"): a step that deleted a file of the "
                    + "workspace fails, whatever its reason - putting it back after does not undo that.");
        if (input.Owns is { Count: > 0 } owns)
        {
            sb.AppendLine("Lines of the request the plan gives this step - it does or checks its part of each; the step is not done while a part of one that is its own is not:");
            foreach (var line in owns) sb.AppendLine("- " + line);
        }
        if (input.OtherSteps.Count > 0)
            sb.AppendLine("Other steps of the plan (theirs, not this step's): " + string.Join("; ", input.OtherSteps));
        sb.AppendLine();
        sb.AppendLine("REPORT - the worker's claim:").AppendLine(input.Report).AppendLine();
        if (input.HandedOn is { Length: > 0 } handed)
            sb.AppendLine("HANDED ON by the step (accepted values):").AppendLine(handed).AppendLine();
        sb.AppendLine("FILES this step changed:");
        foreach (var file in input.Files)
            sb.AppendLine($"--- {file.Path}{(file.Whole ? "" : " (shown in part)")}{(file.Heading is { } heading ? " - " + heading : "")}").AppendLine(file.Text);
        if (input.Files.Count == 0) sb.AppendLine("(none)");
        sb.AppendLine();
        sb.AppendLine("TOOL CALLS (cite them by [n]):").AppendLine(input.Evidence.Text);
        return sb.ToString();
    }

    /// <summary>The answer checked and read. A pass must cite what shows it; a fail must say what is wrong.</summary>
    internal static (ReviewVerdict? Verdict, IReadOnlyList<string> Errors) Read(string answer, StepVerdictInput input)
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

        // The files that are right, on a fail that is elsewhere: a step rejected on it keeps them, as the earlier review's
        // "implementation: pass" with no repair to a saved file did (ReviewResult.Keep; code review of the move to one
        // short review, 2026-09-30). Only a fail names them; a pass keeps everything anyway.
        var keep = verdict == "fail"
            ? Arr("keep").Where(x => x.ValueKind == JsonValueKind.String).Select(x => ShellLookup.Normal(x.GetString()!))
                .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        return verdict == "pass"
            ? (new ReviewVerdict.Pass(reason), [])
            : (new ReviewVerdict.Fail(reason, reason, keep), []);
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
