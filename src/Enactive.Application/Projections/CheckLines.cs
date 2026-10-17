namespace Enactive.Application.Projections;

using Enactive.Core.Checks;
using Enactive.Core.History;

/// <summary>How a declared check came out, as a step card shows it.</summary>
internal enum CheckVerdict { Pass, Fail, NotRun }

/// <summary>
/// One declared check's result, in the words a card shows it in (Plan Phase 3.5): which check, PASS / FAIL / NOT RUN, where it
/// fired, why - the reason a check that did not run gave, or where it was put off to - and the file that holds its whole
/// output, workspace-relative, when one was kept.
/// </summary>
internal sealed record CheckLine(string CheckId, CheckVerdict Verdict, string Word, string Where, string? Reason, string? DetailPath)
{
    /// <summary>The line in one: "FAIL lint — at the step's end — exit 2".</summary>
    public string Text => $"{Word} {CheckId} — {Where}" + (Reason is { Length: > 0 } why ? " — " + why : "");

    public bool HasDetail => DetailPath is { Length: > 0 };
}

/// <summary>
/// The checks that fired, as the window's step cards and the service's draw them - one projection of the run's fold
/// (RunFeed: <see cref="FeedCard.Checks"/>, <see cref="RunFeed.RunChecks"/>), so the two hosts cannot word one result two ways.
/// </summary>
internal static class CheckLines
{
    /// <summary>The longest a reason is shown; the whole of it is in the run's timeline and the check's file.</summary>
    private const int ReasonChars = 120;

    public static IReadOnlyList<CheckLine> Of(IEnumerable<FeedCheck> checks) => checks.Select(Of).ToList();

    public static CheckLine Of(FeedCheck check)
    {
        var firing = check.Firing;
        var verdict = check.Outcome switch
        {
            "Passed" => CheckVerdict.Pass,
            "Failed" => CheckVerdict.Fail,
            // Unknown, and any outcome a later build names: not a pass, which is what matters on a card.
            _ => CheckVerdict.NotRun
        };
        var word = verdict switch { CheckVerdict.Pass => "PASS", CheckVerdict.Fail => "FAIL", _ => "NOT RUN" };

        var why = new List<string>();
        if (Deferral(firing) is { } deferred)
            why.Add(deferred);
        if (verdict == CheckVerdict.NotRun)
            why.Add(NotRunReason(check.Summary) ?? "it did not run");
        else if (verdict == CheckVerdict.Fail && check.ExitCode is { } code)
            why.Add($"exit {code}");

        return new CheckLine(firing.CheckId, verdict, word, Where(check), why.Count == 0 ? null : string.Join("; ", why),
            // A check with no output kept no file; a link that opened nothing would be worse than none.
            string.IsNullOrWhiteSpace(firing.DetailPath) ? null : firing.DetailPath);
    }

    private static string Where(FeedCheck check) => check.Firing.FiredOn switch
    {
        CheckBoundary.Write => check.Times > 1 ? $"after a write (the latest of {check.Times})" : "after a write",
        CheckBoundary.Step => "at the step's end",
        CheckBoundary.Wave => check.Firing.Wave is { } wave ? $"wave {wave}" : "at a wave's end",
        CheckBoundary.Apply => "on apply",
        _ => "at the run's end"
    };

    /// <summary>Where a check owed elsewhere was run - said by where, not by the engine's longer reason, which the timeline keeps.</summary>
    private static string? Deferral(CheckFiring firing)
        => firing.Deferred is not { Length: > 0 } ? null
            : firing.FiredOn switch
            {
                CheckBoundary.Wave => firing.Wave is { } wave ? $"deferred to wave {wave}" : "deferred to a wave's end",
                CheckBoundary.Run => "deferred to the run's end",
                // Owed at apply: the reason it was not run says it ("staged: needs the applied files").
                _ => null
            };

    /// <summary>
    /// Why a check did not run, as the engine said it: the head of its detail - "not run: needs permission", "staged: needs the
    /// applied files", "not run to its end: ..." - before the " - " the engine puts the longer explanation after. The payload
    /// has no reason of its own, so it is read from the sentence: a step's or the run's check gives it on the line under its
    /// verdict (CriterionResult.Describe), a write check after its id ("Write-time check 'x': &lt;why&gt; - run after ...").
    /// Null when the sentence names none - a write check that said only "NOT CHECKED".
    /// </summary>
    internal static string? NotRunReason(string summary)
    {
        string said;
        var newline = summary.IndexOf('\n');
        if (newline >= 0)
            said = summary[(newline + 1)..].Trim();
        else if (summary.IndexOf("': ", StringComparison.Ordinal) is var colon and >= 0)
            said = summary[(colon + 3)..].Trim();
        else
            return null;

        var head = said.IndexOf(" - ", StringComparison.Ordinal) is var dash and > 0 ? said[..dash] : said;
        head = head.Split('\n')[0].Trim().TrimEnd('.');
        // A verdict word, not a reason.
        if (head.Length == 0 || head.StartsWith("NOT CHECKED", StringComparison.Ordinal))
            return null;
        return head.Length <= ReasonChars ? head : head[..ReasonChars] + "…";
    }
}
