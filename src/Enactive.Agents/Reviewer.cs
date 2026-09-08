namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>
/// What a proof pass answered, and what asking cost. The tokens are carried for the same reason the
/// verdict's are: this is a second call on the Review model, and a cost that is not counted is a
/// cost that gets attributed to nothing.
/// </summary>
public sealed record ProofOutcome(ProofClaim Claim, int PromptTokens = 0, int CompletionTokens = 0);

/// <summary>
/// Reviewer verdict for a step, plus what asking cost.
///
/// <para>The review call sits outside the tool loop, so its tokens were never counted; on a run that
/// reviews content, the reviewer reads whole documents on the most expensive model bound, which made
/// the uncounted share the LARGEST part of some runs. The counts cover the re-ask too, when there
/// was one - two calls were made, and two calls were paid for.</para>
/// </summary>
public sealed record ReviewResult(
    bool Pass, string Notes, int PromptTokens = 0, int CompletionTokens = 0);

/// <summary>One file the step wrote, as the reviewer needs to see it.</summary>
/// <param name="Content">
/// What the reviewer is shown. May be an excerpt.
/// </param>
/// <param name="TotalChars">
/// How long the file ACTUALLY is, however much of it <paramref name="Content"/> carries.
///
/// <para>Without this the prompt could not tell whether it was looking at a whole file. It measured
/// truncation by comparing its own slice against the string it had been handed - and the caller had
/// already cut that string to the same 8000 characters, so the comparison was 8000 &lt; 8000 and the
/// "showing the first N of M" line never appeared. On 2026-09-07 a 414-line page was reviewed as its
/// first 161 lines, ending mid-&lt;article&gt;, with nothing saying so. The reviewer is instructed to
/// fail on "a truncated line", so it did - and then, asked to name the offending lines, invented a
/// specific one: a &lt;div&gt; supposedly closed with "&lt;/div" at line 43. Nothing was wrong with
/// the file. The worker spent the rest of the run chasing that phantom until the stall detector
/// stopped it.</para>
///
/// <para>Two caps in two places, equal by coincidence, and the honesty check of the second was
/// defeated by the silence of the first. The size travels with the content now, so the notice fires
/// whoever did the cutting.</para>
/// </param>
public sealed record WrittenFile(string RelativePath, string Content, int TotalChars)
{
    public WrittenFile(string relativePath, string content)
        : this(relativePath, content, content.Length) { }

    /// <summary>Whether the reviewer is being shown less than the whole file.</summary>
    public bool IsExcerpt => Content.Length < TotalChars;
}

/// <summary>
/// What a step should be judged ON.
///
/// A step that RAN something can be checked against reality: the command either executed or it did
/// not, and its exit code is not a matter of opinion. A step that only WROTE something cannot —
/// there is no failing exit code in a document, so execution review has nothing to look at and
/// passes anything at all. That is not a hypothetical: on 2026-09-06 a local model produced a
/// cluster guide full of invented package names, invented pcs syntax, wrong Corosync ports and
/// stray CJK characters inside a resource identifier, and the run finished green because the write
/// itself had succeeded.
/// </summary>
public enum ReviewMode
{
    /// <summary>Did the commands actually run, and did they succeed? The original review.</summary>
    Execution,

    /// <summary>Is what was written actually true? For steps whose only output is content.</summary>
    Content
}

/// <summary>
/// The reasoning agent reviewing a coding step (PLAN_v2 multi-agent: Reasoner reviews the Coder's work).
/// One LLM call → PASS/FAIL + short notes, in one of two modes (see <see cref="ReviewMode"/>).
///
/// It fails CLOSED. An answer that carries no verdict earns exactly one re-ask with a stricter
/// instruction; a second unparseable answer is a FAIL, not a pass. "The reviewer could not tell us"
/// and "the reviewer approved" are different facts, and treating the first as the second is how a
/// gate ends up enforcing nothing while looking configured.
/// </summary>
public sealed class Reviewer
{
    /// <summary>Per file, and in total — a reviewer prompt is not the place to send a whole repository.</summary>
    private const int MaxContentCharsPerFile = 8000;
    private const int MaxContentCharsTotal = 20000;

    public async Task<ReviewResult> ReviewAsync(
        string stepTitle, string coderOutput, string executionEvidence, IReadOnlyList<string> artifacts,
        IChatProvider provider, string model, CancellationToken ct,
        ReviewMode mode = ReviewMode.Execution,
        IReadOnlyList<WrittenFile>? writtenFiles = null)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(mode == ReviewMode.Content ? ContentSystemPrompt : ExecutionSystemPrompt),
            ChatMessage.User(mode == ReviewMode.Content
                ? BuildContentUserPrompt(stepTitle, coderOutput, writtenFiles ?? Array.Empty<WrittenFile>())
                : BuildExecutionUserPrompt(stepTitle, coderOutput, executionEvidence, artifacts))
        };

        var completion = await provider.CompleteAsync(
            new ChatRequest(model, messages, Temperature: 0.0, ResponseSchema: VerdictSchema), ct);
        var answer = completion.Message.Content ?? "";

        var prompt = completion.PromptTokens ?? 0;
        var output = completion.CompletionTokens ?? 0;

        if (Parse(answer) is { } verdict)
            return verdict with { PromptTokens = prompt, CompletionTokens = output };

        // No verdict in there. Ask once more, showing what came back and what was wanted, because a
        // model that wandered off format usually recovers when told exactly what shape to produce.
        messages.Add(new ChatMessage(ChatRole.Assistant, answer, null));
        messages.Add(ChatMessage.User(
            "That reply did not contain a verdict. Reply with NOTHING but a single JSON object, no prose, "
            + "no code fences, no explanation before or after it, in exactly this shape:\n"
            + "{\"verdict\":\"pass\",\"notes\":\"...\"}\n"
            + "or\n"
            + "{\"verdict\":\"fail\",\"notes\":\"...\"}"));

        var retry = await provider.CompleteAsync(
            new ChatRequest(model, messages, Temperature: 0.0, ResponseSchema: VerdictSchema), ct);

        prompt += retry.PromptTokens ?? 0;
        output += retry.CompletionTokens ?? 0;

        if (Parse(retry.Message.Content ?? "") is { } retried)
            return retried with { PromptTokens = prompt, CompletionTokens = output };

        // Twice with no verdict. A reviewer that cannot answer has not approved anything.
        return new ReviewResult(false,
            "the reviewer did not return a verdict, twice — treating the step as not reviewed",
            prompt, output);
    }

    /// <summary>
    /// The shape a verdict takes, offered to any provider that can hold a model to it
    /// (<c>FIX_PLAN.md</c> §9c). Two fields, both required, one of them an enumeration of two words.
    ///
    /// <para>The reviewer is where this was worth doing FIRST and the planner is not: there is
    /// nothing here to reason about, so constrained decoding cannot eat the thinking - which on a
    /// 12-14B model is exactly what it does to a plan. Measure before the planner, if ever.</para>
    ///
    /// <para>It changes NOTHING about how the answer is judged. <see cref="Parse"/> runs on the
    /// reply exactly as before, the re-ask below still happens when it comes back unreadable, and
    /// two unreadable answers still fail closed. A schema makes the bad path rarer; it must never
    /// become the thing correctness rests on, because a provider may ignore it, a gateway may not
    /// support it, and a well-formed claim is still only a claim.</para>
    /// </summary>
    internal const string VerdictSchema = """
        {
          "type": "object",
          "properties": {
            "verdict": { "type": "string", "enum": ["pass", "fail"] },
            "notes": { "type": "string" }
          },
          "required": ["verdict", "notes"],
          "additionalProperties": false
        }
        """;

    internal static string BuildExecutionUserPrompt(
        string stepTitle, string coderOutput, string executionEvidence, IReadOnlyList<string> artifacts)
    {
        var files = artifacts.Count == 0 ? "(none)" : string.Join(", ", artifacts);

        return $"Step: {stepTitle}\n\n"
             + $"What the coding agent reported:\n{coderOutput}\n\n"
             + $"Tool execution evidence — the ACTUAL commands run and their real stdout/stderr/exit codes "
             + $"(this is the ground truth; the agent's own words above may be wrong or invented):\n{executionEvidence}\n\n"
             + $"Files changed: {files}\n\n"
             + "Judge ONLY from the evidence, and judge the ANSWER — not which tools it was reached with. "
             + "FAIL if: the report above leans on a command that is not in the evidence (it claims a build "
             + "succeeded, a test passed, a value was printed); a command that DID run failed (non-zero exit "
             + "or an error in its output) and the report does not account for it; or the reported/saved result "
             + "is fabricated or a placeholder value not present in the real tool output. A step that only read "
             + "and listed has not failed for that — for an analysis step, reading IS the work. Otherwise PASS.";
    }

    internal static string BuildContentUserPrompt(
        string stepTitle, string coderOutput, IReadOnlyList<WrittenFile> writtenFiles)
    {
        var sb = new StringBuilder();
        sb.Append("Step: ").AppendLine(stepTitle).AppendLine();
        sb.AppendLine("What the coding agent reported:").AppendLine(coderOutput).AppendLine();

        if (writtenFiles.Count == 0)
        {
            sb.AppendLine("The step wrote no content at all.");
            return sb.ToString();
        }

        sb.AppendLine("This step ran no commands — it produced text. Here is exactly what it wrote:");

        var budget = MaxContentCharsTotal;
        foreach (var file in writtenFiles)
        {
            sb.AppendLine().Append("----- ").Append(file.RelativePath).AppendLine(" -----");

            var slice = file.Content.Length > MaxContentCharsPerFile
                ? file.Content[..MaxContentCharsPerFile]
                : file.Content;

            if (slice.Length > budget)
                slice = slice[..Math.Max(0, budget)];

            budget -= slice.Length;
            sb.AppendLine(slice);

            // Against the file's REAL size, not against the string handed to us - which the caller
            // may already have cut to exactly this budget, making the comparison always false.
            if (slice.Length < file.TotalChars)
                sb.AppendLine()
                  .AppendLine($"----- END OF EXCERPT: the {file.RelativePath} above is the first "
                            + $"{slice.Length} characters of {file.TotalChars}. The rest of the file "
                            + "was not shown to you and is NOT missing from it. -----");

            if (budget <= 0)
            {
                sb.AppendLine().AppendLine("… (further files omitted — the review budget was reached)");
                break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The second question, asked of a step whose report the reviewer has already found TRUE:
    /// what in the evidence SHOWS the step's objective was met?
    ///
    /// <para>A different question from the first, and the one nobody was asking. Every fact in a
    /// report can be in the evidence while the conclusion follows from none of it — a test
    /// "targeted" that was already failing and stayed failing, reported honestly, passed. Truth is
    /// not soundness.</para>
    ///
    /// <para>It asks for a POINTER rather than an opinion: the numbers of the calls that show it.
    /// A number can be looked up, and <see cref="ProofAudit"/> looks it up against the journal
    /// rather than believing it. That is what makes this different from adding a fifth clause to a
    /// prompt whose fourth was already one too many.</para>
    ///
    /// <para>Fails CLOSED, like the verdict above and for the same reason: two unreadable answers
    /// are "the pass could not tell us", which is not "the step is proven".</para>
    /// </summary>
    public async Task<ProofOutcome> ProveAsync(
        string stepTitle, string coderOutput, string executionEvidence,
        IChatProvider provider, string model, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(ProofSystemPrompt),
            ChatMessage.User(BuildProofUserPrompt(stepTitle, coderOutput, executionEvidence))
        };

        var completion = await provider.CompleteAsync(
            new ChatRequest(model, messages, Temperature: 0.0, ResponseSchema: ProofSchema), ct);
        var answer = completion.Message.Content ?? "";

        var prompt = completion.PromptTokens ?? 0;
        var output = completion.CompletionTokens ?? 0;

        if (ParseProof(answer) is { } claim)
            return new ProofOutcome(claim, prompt, output);

        messages.Add(new ChatMessage(ChatRole.Assistant, answer, null));
        messages.Add(ChatMessage.User(
            "That reply did not contain an answer. Reply with NOTHING but a single JSON object, no prose, "
            + "no code fences, in exactly this shape:\n"
            + "{\"shown\":\"yes\",\"calls\":[3],\"what\":\"...\"}\n"
            + "or {\"shown\":\"no\",\"calls\":[],\"what\":\"...\"}\n"
            + "or {\"shown\":\"not-by-any-call\",\"calls\":[],\"what\":\"...\"}"));

        var retry = await provider.CompleteAsync(
            new ChatRequest(model, messages, Temperature: 0.0, ResponseSchema: ProofSchema), ct);

        prompt += retry.PromptTokens ?? 0;
        output += retry.CompletionTokens ?? 0;

        if (ParseProof(retry.Message.Content ?? "") is { } retried)
            return new ProofOutcome(retried, prompt, output);

        // Twice with nothing usable. Not proven — the same rule as the verdict, because "we could
        // not find out" and "it is fine" are different facts.
        return new ProofOutcome(
            new ProofClaim(ProofClaimKind.NotShown, Array.Empty<int>(),
                           "the proof pass did not answer, twice"),
            prompt, output);
    }

    /// <summary>The shape of a proof answer, for a provider that can hold a model to one.</summary>
    internal const string ProofSchema = """
        {
          "type": "object",
          "properties": {
            "shown": { "type": "string", "enum": ["yes", "no", "not-by-any-call"] },
            "calls": { "type": "array", "items": { "type": "integer" } },
            "what": { "type": "string" }
          },
          "required": ["shown", "calls", "what"],
          "additionalProperties": false
        }
        """;

    internal static string BuildProofUserPrompt(
        string stepTitle, string coderOutput, string executionEvidence)
        => $"The step's objective:\n{stepTitle}\n\n"
         + $"What the agent reported:\n{coderOutput}\n\n"
         + $"Every call the step made, numbered:\n{executionEvidence}\n\n"
         + "Which of these calls SHOWS that the objective above was met? Answer with their numbers.";

    /// <summary>
    /// Deliberately short. This pass has one question and a long prompt would invite it to answer a
    /// different one — which is how the execution reviewer came to fail steps for not running
    /// commands nobody asked for.
    /// </summary>
    internal const string ProofSystemPrompt =
        "You are checking whether a step's reported success FOLLOWS from what the step actually did. "
        + "Another reviewer has already confirmed that the report is truthful about the evidence; "
        + "that is not your question. Yours is narrower: does the evidence SHOW the objective was met?\n\n"
        + "Respond with ONLY a JSON object, no prose and no code fences:\n"
        + "{\"shown\":\"yes\"|\"no\"|\"not-by-any-call\",\"calls\":[numbers],\"what\":\"one sentence\"}\n\n"
        + "\"yes\" — the evidence shows it. Put in \"calls\" the number of EVERY call that shows it, "
        + "as numbered in the evidence, and nothing else. Only cite a call you can actually see. A "
        + "citation is checked against what really happened, so a number you are unsure of is worse "
        + "than one fewer number.\n\n"
        + "\"no\" — a call could have shown it, and none of these does. Use this when the report draws "
        + "a conclusion the calls do not support: a fix reported over a test that still fails, a "
        + "problem called solved by calls that only looked at it, a claim that the calls are merely "
        + "consistent with rather than evidence for. Say which conclusion is unsupported in \"what\".\n\n"
        + "\"not-by-any-call\" — the objective is not the kind of thing a tool call settles: reading, "
        + "analysing, deciding, explaining, or writing a document. WHICH TOOLS a step uses are the "
        + "agent's to choose, and many correct steps run no command at all. Use this answer freely; "
        + "it is never held against the step. Never answer \"no\" merely because you would have "
        + "expected some command to be run — that is this answer, not that one.\n\n"
        + "A call marked ERROR or REFUSED did not do its job and cannot be what shows an objective "
        + "was met. A call marked NOTHING THERE ran and answered — a file that is absent, an offset "
        + "past the end — and can be exactly what shows one.";

    /// <summary>The claim, or null when the answer carried none.</summary>
    internal static ProofClaim? ParseProof(string text)
    {
        var json = ModelText.ExtractJsonObject(ModelText.StripThink(text));
        if (json is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("shown", out var s)
                || s.ValueKind != JsonValueKind.String)
                return null;

            // The word has to be one of the three. Anything else is not an answer, and picking a
            // default here is how a gate comes to enforce nothing.
            var kind = s.GetString()?.Trim().ToLowerInvariant() switch
            {
                "yes" => ProofClaimKind.Shown,
                "no" => ProofClaimKind.NotShown,
                "not-by-any-call" or "not_by_any_call" or "notbyanycall" => ProofClaimKind.NotByAnyCall,
                _ => (ProofClaimKind?)null
            };
            if (kind is not { } claimKind)
                return null;

            var calls = new List<int>();
            if (root.TryGetProperty("calls", out var c) && c.ValueKind == JsonValueKind.Array)
                foreach (var item in c.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n))
                        calls.Add(n);
                    // A model that answers with the number as it appears in the text - "3", "[3]",
                    // "#3" - has still pointed at a call, and refusing to read it would fail a proof
                    // over its punctuation.
                    else if (item.ValueKind == JsonValueKind.String
                             && int.TryParse(item.GetString()?.Trim().Trim('[', ']', '#'), out var parsed))
                        calls.Add(parsed);

            var what = root.TryGetProperty("what", out var w) && w.ValueKind == JsonValueKind.String
                ? w.GetString() ?? ""
                : "";

            return new ProofClaim(claimKind, calls.Distinct().ToArray(), what);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Returns the verdict, or null when the answer carried none.</summary>
    private static ReviewResult? Parse(string text)
    {
        var json = ModelText.ExtractJsonObject(ModelText.StripThink(text));
        if (json is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("verdict", out var v)
                || v.ValueKind != JsonValueKind.String)
                return null;

            var verdict = v.GetString();

            // The verdict has to SAY one of the two things. Anything else — an empty string, "unsure",
            // a sentence — is not a verdict, and defaulting it to pass is the bug this replaces.
            var pass = string.Equals(verdict, "pass", StringComparison.OrdinalIgnoreCase);
            var fail = string.Equals(verdict, "fail", StringComparison.OrdinalIgnoreCase);
            if (!pass && !fail)
                return null;

            var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? ""
                : "";

            return new ReviewResult(pass, notes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal const string ExecutionSystemPrompt =
        "You are a senior code reviewer verifying a coding agent's step against real tool-execution evidence. "
        + "Trust the execution evidence (actual commands + their real output/exit codes) over the agent's own summary, "
        + "which may be mistaken or fabricated. Respond with ONLY a JSON object, no prose and no code fences: "
        + "{\"verdict\":\"pass\" or \"fail\",\"notes\":\"short, specific feedback\"}. "
        // "the REQUIRED command" was the wording, and it made the reviewer decide for itself what a
        // step required. Anchored to the report instead: a command matters here when the answer
        // leans on it. See the paragraph on choosing tools below.
        + "Fail if the agent's report leans on a command that was never run, a command it did run "
        + "failed, or a reported/saved value is fabricated or a placeholder not present in the real "
        + "output. Otherwise pass.\n\n"
        // The clause that stops the reviewer failing work it simply could not see. On 2026-09-07 the
        // evidence was cut after the first two calls and it concluded, correctly from what it had,
        // that no source files were ever read. Five had been. Every call is listed now, and this
        // says what a shortened RESULT means so the two are never confused again.
        + "The evidence lists EVERY call the step made, oldest first, and says how many there were. "
        + "A result may be shortened and says so where it is: a shortened result is still a call that "
        + "HAPPENED, and is never grounds to say the work was not done. Judge by the calls listed. If "
        + "a call you would expect is genuinely absent from the list, that is a real finding; if you "
        + "can see the call and only part of its output, it is not.\n\n"
        // Added 2026-09-07 20:16. A step titled "Analyze test coverage and identify gaps" read the
        // test project and two source files and produced a specific, correct analysis. It was failed
        // for "no actual analysis or test coverage commands were executed" — a requirement nobody
        // stated, inferred from the step's TITLE. The retry then ran commands to satisfy the
        // reviewer rather than to learn anything, and the run died on one of them.
        + "WHICH TOOLS a step uses are the agent's to choose. Reading and listing IS the work of an "
        + "analysis, review or planning step, and many steps correctly run no command at all — a "
        + "step is never deficient merely for not having run one. So do not ask whether a command "
        + "OUGHT to have been run; ask whether THE ANSWER IS SUPPORTED. Fail when the agent reports "
        + "something only a command could have produced — a build that succeeded, a test that "
        + "passed, a version or a measurement it printed — and no such call is in the evidence. An "
        + "answer drawn from files the evidence shows it read is supported, however few commands it "
        + "ran.\n\n"
        // Without this the reviewer reads "exit code 1" and fails the step by its own rule, which
        // would put the run back exactly where it was.
        + "A call may DECLARE the exit codes it expects, in an \"expectedExitCodes\" argument you can "
        + "see in the evidence. A test runner that returns 1 because a test failed is reporting, not "
        + "malfunctioning, and a call that got a code it declared is recorded as succeeded — its exit "
        + "code alone is then not a finding. What IS a finding: a declaration that does not fit the "
        + "command (a build declaring failure acceptable), or a report that contradicts the output "
        + "under it, such as \"all tests pass\" over output listing failures.\n\n"
        // Added after this clause's own words were turned against a step: "the evidence shows errors
        // for offsets 800 and 400 being past the end of the file. The agent's report does not
        // account for these errors." Those calls answered. Nothing was wrong.
        + "A result marked NOTHING THERE is not an error and is not something the report has to "
        + "account for. It is a lookup that ran and found nothing: a file that does not exist, an "
        + "offset past the end of one. Asking and being told no is how anything explores a tree it "
        + "has not seen, and the agent is free to say nothing about it. ERROR is the label for a "
        + "call that went wrong; judge only those.";

    // Deliberately narrow. A content reviewer that fails on anything it is merely unsure about blocks
    // every run and gets switched off, so it is told to fail only on a specific, nameable falsehood —
    // and told explicitly that style, length and completeness are none of its business.
    internal const string ContentSystemPrompt =
        "You are a senior technical reviewer checking a document another agent just wrote, for FACTUAL "
        + "correctness. The agent may be a small model that invents plausible-looking detail.\n\n"
        + "Respond with ONLY a JSON object, no prose and no code fences: "
        + "{\"verdict\":\"pass\" or \"fail\",\"notes\":\"short, specific feedback\"}.\n\n"
        + "FAIL when you can point to a SPECIFIC assertion that is wrong or invented, such as:\n"
        + "- a package, tool, command or file that does not exist under that name;\n"
        + "- command syntax, subcommands, flags or configuration keys that are not real;\n"
        + "- wrong port numbers, protocols, paths, API names or version requirements;\n"
        + "- steps that contradict each other, or that cannot work in the stated order;\n"
        + "- text corruption: stray characters from another script or language inside an identifier, "
        + "mojibake, or a truncated line.\n\n"
        + "In notes, name the offending lines so the agent can fix exactly those. Only name something "
        + "you can actually see in the text above; if you cannot point at it, do not report it.\n\n"
        + "Where an excerpt is marked as such, judge ONLY what it contains. It stops where the excerpt "
        + "stops, not where the file does: an unclosed tag, bracket or sentence at the very end is the "
        + "cut, never a defect, and neither is anything you expected to find further down.\n\n"
        + "Do NOT fail for style, tone, formatting, length, or for being incomplete — a short document is "
        + "not a wrong one. Do NOT fail because you would have written it differently. If you are unsure "
        + "whether something exists, do not fail on it: say so in notes and pass. Judge the content, not "
        + "the effort.";
}
