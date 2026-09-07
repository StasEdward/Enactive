namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;

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

        var completion = await provider.CompleteAsync(new ChatRequest(model, messages, Temperature: 0.0), ct);
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

        var retry = await provider.CompleteAsync(new ChatRequest(model, messages, Temperature: 0.0), ct);

        prompt += retry.PromptTokens ?? 0;
        output += retry.CompletionTokens ?? 0;

        if (Parse(retry.Message.Content ?? "") is { } retried)
            return retried with { PromptTokens = prompt, CompletionTokens = output };

        // Twice with no verdict. A reviewer that cannot answer has not approved anything.
        return new ReviewResult(false,
            "the reviewer did not return a verdict, twice — treating the step as not reviewed",
            prompt, output);
    }

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
        + "under it, such as \"all tests pass\" over output listing failures.";

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
