namespace Enactive.Agents;

using Enactive.Core.Chat;
using Enactive.Core.Providers;

/// <summary>What an analysis looked at, and what it concluded.</summary>
/// <param name="LinesSent">How many lines of the log the model was actually shown.</param>
/// <param name="LinesTotal">How many the log has.</param>
public sealed record LogAnalysisResult(
    string Answer,
    string Model,
    int LinesSent,
    int LinesTotal,
    int PromptTokens = 0,
    int CompletionTokens = 0)
{
    public bool WasExcerpt => LinesSent < LinesTotal;

    /// <summary>What the window says above the answer, so nobody has to guess what was read.</summary>
    public string Provenance => WasExcerpt
        ? $"{Model} · read {LinesSent:N0} of {LinesTotal:N0} lines — the start and the end, "
          + "with the middle omitted"
        : $"{Model} · read all {LinesTotal:N0} lines";
}

/// <summary>
/// Reads a log the way a person would like to, and says what went wrong in it.
///
/// <para>A log is the one artifact in this application that is complete and unreadable at the same
/// time. Twelve thousand lines hold the answer to "why did that run fail" and finding it means
/// knowing what to look for, which is exactly what somebody who is asking does not have.</para>
///
/// <para>Two things make this different from pasting a file into a chat window, and both are the
/// same lesson this codebase keeps relearning: <b>a log that does not fit is cut, and the cut has to
/// announce itself</b> — to the reader in the window's header, and to the model in the prompt, so it
/// never reasons from "the log ends here" when the log does not. And the middle goes rather than the
/// end, because a failure is at the end and the request is at the start.</para>
/// </summary>
public sealed class LogAnalyst
{
    /// <summary>
    /// Marks where the middle of a log was removed. Long and explicit on purpose: a model that reads
    /// past this must not be able to treat the two halves as adjacent.
    /// </summary>
    public const string Gap = "===== THE MIDDLE OF THIS LOG IS NOT SHOWN =====";

    /// <summary>
    /// Roughly how many characters fit in a token. Characters rather than tokens for the same reason
    /// <see cref="Transcript.Size"/> uses them: Core has no tokenizer and every provider's differs.
    /// Deliberately pessimistic — over-estimating the size of the log costs a few hundred lines of
    /// context, while under-estimating it costs the whole request.
    /// </summary>
    private const int CharsPerToken = 3;

    /// <summary>
    /// What is left for the log after the instructions and the model's own answer are paid for.
    /// A third of the window is reserved: the answer is prose and can be long, and a request that
    /// does not fit is a total loss rather than a shorter answer.
    /// </summary>
    public static int BudgetChars(int? contextWindowTokens)
        => Math.Max(4_000, (int)((contextWindowTokens ?? 16_000) * 0.60) * CharsPerToken);

    public async Task<LogAnalysisResult> AnalyseAsync(
        string log, IChatProvider provider, string model, int? contextWindowTokens, CancellationToken ct)
    {
        var lines = (log ?? "").Replace("\r\n", "\n").Split('\n');
        var shown = Excerpt(lines, BudgetChars(contextWindowTokens), out var sent);

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(SystemPrompt),
            ChatMessage.User(
                (sent < lines.Length
                    ? $"This is an EXCERPT: {sent} of the log's {lines.Length} lines. The middle was "
                      + $"removed and the place is marked. Nothing is missing from the file itself.\n\n"
                    : $"This is the whole log, all {lines.Length} lines.\n\n")
                + "----- LOG -----\n" + shown)
        };

        var completion = await provider.CompleteAsync(
            new ChatRequest(model, messages, Temperature: 0.0), ct);

        var answer = completion.Message.Content ?? "";

        return new LogAnalysisResult(
            string.IsNullOrWhiteSpace(answer)
                ? "The model returned nothing. That is not a finding about the log — it is a failed "
                  + "request. Try again, or try a different model."
                : answer.Trim(),
            model, sent, lines.Length,
            completion.PromptTokens ?? 0, completion.CompletionTokens ?? 0);
    }

    /// <summary>
    /// As much of the log as fits: the start, the end, and a marker where the middle was.
    ///
    /// <para>Weighted towards the end — a run's request and routing are in the first lines and rarely
    /// change, while what actually went wrong is in the last ones. Taking the first N characters, the
    /// obvious implementation, would reliably hand over the part with no failures in it and then be
    /// asked what failed.</para>
    /// </summary>
    public static string Excerpt(IReadOnlyList<string> lines, int budgetChars, out int linesSent)
    {
        var total = lines.Sum(l => l.Length + 1);
        if (total <= budgetChars)
        {
            linesSent = lines.Count;
            return string.Join("\n", lines);
        }

        var room = Math.Max(0, budgetChars - Gap.Length - 2);
        var headRoom = room * 3 / 10;

        var head = new List<string>();
        var used = 0;
        foreach (var line in lines)
        {
            if (used + line.Length + 1 > headRoom) break;
            head.Add(line);
            used += line.Length + 1;
        }

        var tail = new List<string>();
        var tailUsed = 0;
        for (var i = lines.Count - 1; i >= head.Count; i--)
        {
            if (tailUsed + lines[i].Length + 1 > room - used) break;
            tail.Add(lines[i]);
            tailUsed += lines[i].Length + 1;
        }
        tail.Reverse();

        linesSent = head.Count + tail.Count;
        var omitted = lines.Count - linesSent;

        return string.Join("\n", head)
             + $"\n{Gap} ({omitted:N0} lines) =====\n"
             + string.Join("\n", tail);
    }

    /// <summary>
    /// The instructions, and every clause in them is here because of something that has actually gone
    /// wrong in this project.
    ///
    /// <para>"Only what you can point at" and "say so when nothing is wrong" are the two that matter:
    /// a reviewer told to find problems finds them, and on 2026-09-07 one invented a specific
    /// closing tag at a specific line number in a file that was correct. An analyst asked what went
    /// wrong will produce a list whether or not there is one, so it is told plainly that a short
    /// answer is the right answer to a clean log.</para>
    ///
    /// <para>The excerpt clause is the other: the log may end mid-sentence because we cut it, and a
    /// model that reasons from where the text stops will report the cut as the failure.</para>
    /// </summary>
    public const string SystemPrompt =
        "You are reading an application's own log to explain, to the person who ran it, what "
        + "happened and what went wrong. The log is machine-written: timestamps, a level (INF, WRN, "
        + "ERR), a source, a run id, and indented '    | ' continuation lines carrying prompts, "
        + "model replies and tool output.\n\n"
        + "Answer in plain prose with short headings. Cover, in this order:\n"
        + "1. WHAT WAS ASKED FOR — the request the run started from, in one sentence.\n"
        + "2. WHAT HAPPENED — the sequence that matters, not every line. Which model ran, which "
        + "tools were called, what they returned, how it ended.\n"
        + "3. PROBLEMS — most serious first. For each: what went wrong, the evidence in the log that "
        + "shows it (quote the line), and what it caused. A problem is something that failed, was "
        + "refused, was retried, contradicted itself, or produced a result nothing verified.\n"
        + "4. WHAT TO DO — only where the log actually supports a suggestion.\n\n"
        + "Rules:\n"
        + "- Only report what you can point at IN THE LOG. Quote the line. Never infer a cause the "
        + "log does not show, and never invent a line, a file, a number or an error message.\n"
        + "- If nothing went wrong, say that in one or two sentences and stop. A clean log deserves "
        + "a short answer, and a list of imaginary concerns is worse than no analysis at all.\n"
        + "- Repetition is a finding: the same call failing over and over means the model was not "
        + "told what was wrong with it. Say how many times, and quote one.\n"
        + "- A run that ended 'Completed' having verified nothing is worth mentioning as such.\n"
        + "- If the log is marked as an excerpt, the middle is missing and the text may begin or end "
        + "abruptly. That is the cut, not a defect. Do not report it, and do not draw conclusions "
        + "from where the text stops.\n"
        + "- Say nothing about the log's formatting, verbosity or style. You are reading it for what "
        + "it records, not reviewing it.";
}
