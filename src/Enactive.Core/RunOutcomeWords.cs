namespace Enactive.Core.Events;

/// <summary>
/// Why a run or a step did not simply succeed, in the words a person reads.
///
/// <para><b>The failure this exists to end.</b> On 2026-09-11 a run was started with Ollama switched
/// off. The engine diagnosed it exactly — <i>"Nothing is listening at http://localhost:11434/v1, so
/// ollama/gemma4:31b-cloud could not be asked. If that is a local model server, it is not
/// running."</i> — recorded it in the step's payload, and then told the person two things: a card
/// that said <c>Failed</c>, and a run that said <c>1 step(s) failed; 1 step(s) skipped</c>.</para>
///
/// <para>Both are true and neither is the answer. The cause existed, was correct, and reached
/// nobody: the step card rendered a literal instead of the reason it had been handed, and the run's
/// terminal event counted its steps instead of naming what stopped them. Counting is what a summary
/// does when nothing carried the reason up.</para>
///
/// <para>In Core so the sentences can be tested. The run half is assembled in
/// <c>Enactive.Agents</c>, which tests can reach, and the step half is rendered by
/// <c>Enactive.App.Ui</c>, which they cannot — and a card is exactly where a literal like "Failed"
/// gets written and never questioned.</para>
/// </summary>
public static class RunOutcomeWords
{
    /// <summary>
    /// How long a cause may be before it stops being a line somebody reads. It rides in an Inbox
    /// row and a one-line status, and a paragraph there pushes out everything beside it.
    /// </summary>
    public const int MaxReason = 220;

    /// <summary>
    /// What the step card says under its title.
    ///
    /// <para>The REASON when the step recorded one, because that is the whole content of the event;
    /// the outcome word alone otherwise. A skipped step is never given a cause even when one is
    /// present — nothing went wrong in that step, and putting another step's failure under it sends
    /// somebody looking for the fault in the wrong card.</para>
    /// </summary>
    public static string StepActivity(StepOutcomeKind? outcome, string? reason)
    {
        if (outcome == StepOutcomeKind.Skipped)
            return "Skipped — a dependency failed";

        var word = outcome switch
        {
            StepOutcomeKind.Succeeded => "Done",
            StepOutcomeKind.ReviewRejected => "Rejected by the reviewer",
            StepOutcomeKind.Incomplete => "Incomplete",
            StepOutcomeKind.DoneUnverified => "Done, not verified",
            _ => "Failed"
        };

        return Trim(reason) is { Length: > 0 } why ? $"{word} — {why}" : word;
    }

    /// <summary>
    /// A short, honest summary of why a run did not simply complete.
    /// </summary>
    /// <param name="reasons">
    /// What each step that did not succeed said, in the order the steps settled. Nulls and blanks
    /// are ignored — a step may fail without having anything to add.
    /// </param>
    /// <param name="limit">
    /// A ceiling the run hit, if any. First, because it EXPLAINS the skipped steps that follow it:
    /// without it a run that ran out of budget reports "4 step(s) skipped" and nothing about why.
    /// </param>
    /// <returns>Null when there is nothing to explain — a run that simply completed.</returns>
    public static string? Explain(
        IReadOnlyCollection<StepOutcomeKind> steps,
        IEnumerable<string?> reasons,
        bool cycle,
        string? limit = null)
    {
        var counts = new List<string>();

        if (!string.IsNullOrWhiteSpace(limit))
            counts.Add(limit!.Trim());

        var failed = steps.Count(s => s == StepOutcomeKind.Failed);
        var rejected = steps.Count(s => s == StepOutcomeKind.ReviewRejected);
        var incomplete = steps.Count(s => s == StepOutcomeKind.Incomplete);
        var skipped = steps.Count(s => s == StepOutcomeKind.Skipped);
        // Counted, or a run held short of Completed by nothing else would explain itself with
        // nothing at all.
        var unverified = steps.Count(s => s == StepOutcomeKind.DoneUnverified);

        if (failed > 0) counts.Add($"{failed} step(s) failed");
        if (rejected > 0) counts.Add($"{rejected} step(s) rejected by the reviewer");
        if (incomplete > 0) counts.Add($"{incomplete} step(s) did not finish");
        if (unverified > 0) counts.Add($"{unverified} step(s) done but not verified");
        if (skipped > 0) counts.Add($"{skipped} step(s) skipped");
        if (cycle) counts.Add("the plan had unresolvable dependencies");

        if (counts.Count == 0)
            return null;

        var tally = string.Join("; ", counts);

        // The CAUSE leads and the counts follow in brackets. The other way round reads more
        // naturally and truncates wrongly: an Inbox row that runs out of width should lose
        // "1 step(s) skipped", which the reader can see for themselves, and not the one sentence
        // that says the model server is off.
        var cause = reasons.Select(Trim).FirstOrDefault(r => r is { Length: > 0 });

        return cause is { Length: > 0 } ? $"{cause} ({tally})" : tally;
    }

    /// <summary>
    /// One line, capped. A reason is written for a person and occasionally arrives as a provider's
    /// whole error document; cutting it at a word boundary keeps the useful half of a long one and
    /// leaves a short one exactly as it was written.
    /// </summary>
    private static string? Trim(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;

        var text = string.Join(' ', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (text.Length <= MaxReason)
            return text;

        var cut = text.LastIndexOf(' ', MaxReason - 1);
        return (cut > MaxReason / 2 ? text[..cut] : text[..(MaxReason - 1)]) + "…";
    }
}
