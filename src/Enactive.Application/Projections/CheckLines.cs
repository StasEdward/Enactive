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

        string? why = verdict switch
        {
            // The payload's reason; for a result recorded before it was kept, read from the sentence as it was then.
            CheckVerdict.NotRun => (check.Reason is { Length: > 0 } reason ? Head(reason) : NotRunReason(check.Summary)) ?? "it did not run",
            CheckVerdict.Fail when check.ExitCode is { } code => $"exit {code}",
            _ => null
        };

        return new CheckLine(firing.CheckId, verdict, word, Where(check), why,
            // A check with no output kept no file; a link that opened nothing would be worse than none.
            string.IsNullOrWhiteSpace(firing.DetailPath) ? null : firing.DetailPath);
    }

    /// <summary>
    /// Where it fired. One owed elsewhere says where it was put off to - said by where, not by the engine's longer reason,
    /// which the timeline keeps - and in place of where it ran, which is the same place: "wave 2 — deferred to wave 2" said
    /// it twice.
    /// </summary>
    private static string Where(FeedCheck check)
    {
        var firing = check.Firing;
        var deferred = firing.Deferred is { Length: > 0 };
        return firing.FiredOn switch
        {
            CheckBoundary.Write => check.Times > 1 ? $"after a write (the latest of {check.Times})" : "after a write",
            CheckBoundary.Step => firing.Attempt is { } attempt ? $"at the step's end (attempt {attempt})" : "at the step's end",
            CheckBoundary.Wave => firing.Wave is { } wave ? (deferred ? $"deferred to wave {wave}" : $"wave {wave}")
                : deferred ? "deferred to a wave's end" : "at a wave's end",
            CheckBoundary.Apply => "on apply",
            _ => deferred ? "deferred to the run's end" : "at the run's end"
        };
    }

    /// <summary>The head of the engine's reason, before the " - " it puts the longer explanation after.</summary>
    private static string? Head(string said)
    {
        var head = said.IndexOf(" - ", StringComparison.Ordinal) is var dash and > 0 ? said[..dash] : said;
        head = head.Split('\n')[0].Trim().TrimEnd('.');
        return head.Length == 0 ? null : head.Length <= ReasonChars ? head : head[..ReasonChars] + "…";
    }

    /// <summary>
    /// Why a check did not run, for a result recorded before its payload kept the reason: read from the sentence, where a
    /// step's or the run's check gave it on the line under its verdict (CriterionResult.Describe) and a write check after its
    /// id ("Write-time check 'x': &lt;why&gt; - run after ..."). Only for those: a command with a line break in it moves the
    /// line. Null when the sentence names none - a write check that said only "NOT CHECKED".
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

        // A verdict word, not a reason.
        return Head(said) is { } head && !head.StartsWith("NOT CHECKED", StringComparison.Ordinal) ? head : null;
    }
}
