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
public sealed record WrittenFile(string RelativePath, string Content);

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

    private static string BuildExecutionUserPrompt(
        string stepTitle, string coderOutput, string executionEvidence, IReadOnlyList<string> artifacts)
    {
        var files = artifacts.Count == 0 ? "(none)" : string.Join(", ", artifacts);

        return $"Step: {stepTitle}\n\n"
             + $"What the coding agent reported:\n{coderOutput}\n\n"
             + $"Tool execution evidence — the ACTUAL commands run and their real stdout/stderr/exit codes "
             + $"(this is the ground truth; the agent's own words above may be wrong or invented):\n{executionEvidence}\n\n"
             + $"Files changed: {files}\n\n"
             + "Judge ONLY from the evidence. FAIL if: the step required running a command but none was actually "
             + "run; a required command failed (non-zero exit or an error in its output); or the reported/saved "
             + "result is fabricated or a placeholder value not present in the real tool output. Otherwise PASS.";
    }

    private static string BuildContentUserPrompt(
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

            if (slice.Length < file.Content.Length)
                sb.AppendLine($"… (showing the first {slice.Length} of {file.Content.Length} characters)");

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
        var json = ExtractJson(StripThink(text));
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

    private static string StripThink(string text)
    {
        const string open = "<think>";
        const string close = "</think>";
        var start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        var end = text.IndexOf(close, StringComparison.OrdinalIgnoreCase);
        return start >= 0 && end > start ? text.Remove(start, end + close.Length - start) : text;
    }

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private const string ExecutionSystemPrompt =
        "You are a senior code reviewer verifying a coding agent's step against real tool-execution evidence. "
        + "Trust the execution evidence (actual commands + their real output/exit codes) over the agent's own summary, "
        + "which may be mistaken or fabricated. Respond with ONLY a JSON object, no prose and no code fences: "
        + "{\"verdict\":\"pass\" or \"fail\",\"notes\":\"short, specific feedback\"}. "
        + "Fail if the required command was never actually run, a required command failed, or a reported/saved value "
        + "is fabricated or a placeholder not present in the real output. Otherwise pass.";

    // Deliberately narrow. A content reviewer that fails on anything it is merely unsure about blocks
    // every run and gets switched off, so it is told to fail only on a specific, nameable falsehood —
    // and told explicitly that style, length and completeness are none of its business.
    private const string ContentSystemPrompt =
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
        + "In notes, name the offending lines so the agent can fix exactly those.\n\n"
        + "Do NOT fail for style, tone, formatting, length, or for being incomplete — a short document is "
        + "not a wrong one. Do NOT fail because you would have written it differently. If you are unsure "
        + "whether something exists, do not fail on it: say so in notes and pass. Judge the content, not "
        + "the effort.";
}
