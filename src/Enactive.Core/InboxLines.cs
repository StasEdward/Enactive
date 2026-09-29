namespace Enactive.Core.Inbox;

/// <summary>
/// The one line the Inbox shows for a run nobody watched: its KIND and its summary.
///
/// <para>Here rather than in the runner because there is now more than one way a run can end up
/// unwatched — a background task inside the app, and a schedule that fired while the app was shut.
/// A rule written twice is enforced twice today and once after the next refactor, and the half that
/// stops being enforced is always the one nobody has a test for.</para>
/// </summary>
public readonly record struct InboxLine(string Kind, string Summary);

/// <summary>How a finished unwatched run is described to a person.</summary>
public static class InboxLines
{
    /// <param name="status">The outcome's name: Completed, Failed, Incomplete, Cancelled.</param>
    /// <param name="reason">What the engine recorded about the ending, if anything.</param>
    public static InboxLine For(string status, int artifacts, int decisions, string? reason)
    {
        // Not an error and not a result: a question, waiting for whoever opens this. It reads as
        // one, and it is the kind the inbox shows a question under - where it can be answered.
        if (status == "NeedsUser")
            return new InboxLine("decision", "Waiting for your decision"
                + (artifacts > 0 ? $" · {artifacts} artifact(s) so far" : "")
                + (reason is { Length: > 0 } ? " · " + reason : ""));

        // Anything that is not a completed run is flagged as such. CANCELLED is in this list and
        // used to be missing from it, so a run that was stopped part-way filed itself as a "result"
        // — the same lie as a green status pill over an abandoned job. The runner's own comment
        // said "anything that is not a completed run"; the code listed two of the three.
        // BLOCKED with them: it waits for something only a person can put right, and says what (Phase 7).
        var kind = status is "Failed" or "Incomplete" or "Cancelled" or "Blocked" ? "error"
                 : decisions > 0 ? "decision"
                 : "result";

        var summary = $"{status} · {artifacts} artifact(s)"
            + (decisions > 0 ? $" · {decisions} decision(s) needed your approval" : "")
            + (reason is { Length: > 0 } ? " · " + reason : "");

        return new InboxLine(kind, summary);
    }
}
