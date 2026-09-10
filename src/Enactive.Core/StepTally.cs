namespace Enactive.Core.History;

/// <summary>
/// What a step did, counted, and the one line that says so.
///
/// <para><b>Why this is in Core.</b> It was a private method on a view model in
/// <c>Enactive.App.Ui</c> — a WinExe no test project references — so the sentence a person reads to
/// decide whether a step is worth opening was the one thing about a step nothing could check.</para>
///
/// <para><b>What it got wrong.</b> The vocabulary had three words for things that HAPPENED (used,
/// ran, edited) and one bucket, "notes", for prose. A refused tool call is neither: the call never
/// becomes an invocation, so no counter moved, and the six refusals of a scheduled run on
/// 2026-09-11 arrived as six remarks. The collapsed line read "14 notes" — arithmetically right,
/// and it described a step that talked a lot rather than a step that was stopped six times.</para>
///
/// <para>A refusal is not counted as a tool used. It did not run, and saying it did would be the
/// opposite lie. It gets its own word.</para>
/// </summary>
/// <param name="Tools">Calls that actually reached a tool — commands and file operations included.</param>
/// <param name="Commands">Of those, ones that handed a command line to the machine.</param>
/// <param name="Files">Of those, ones that WROTE a file. Reading a file is not changing it.</param>
/// <param name="Refused">Calls stopped by the policy or by whoever answers for it.</param>
/// <param name="Notes">Everything else the step said: remarks, review verdicts, warnings.</param>
public readonly record struct StepTally(int Tools, int Commands, int Files, int Refused, int Notes)
{
    /// <summary>
    /// The collapsed summary line, or empty when the step has nothing to summarise.
    ///
    /// <para>Refusals come FIRST when there is nothing else, because then they are the whole story
    /// of the step and burying them behind a note count is what this fixes. Otherwise they follow
    /// what did happen, which is the order a person reads: what was done, then what was stopped.
    /// </para>
    /// </summary>
    public string Words()
    {
        var parts = new List<string>(4);

        if (Tools > 0)
            parts.Add($"Used {Tools} tool{S(Tools)}");
        if (Commands > 0)
            parts.Add($"ran {Commands} command{S(Commands)}");
        if (Files > 0)
            parts.Add($"edited {Files} file{S(Files)}");
        if (Refused > 0)
            // "refused", not "denied": the word has to work for a policy that never asked anybody
            // as well as for a person clicking Deny, and only one of those is a denial.
            parts.Add(parts.Count == 0
                ? $"{Refused} call{S(Refused)} refused"
                : $"{Refused} refused");

        var head = string.Join(", ", parts);

        if (Notes == 0)
            return head;

        var notes = $"{Notes} note{S(Notes)}";
        return head.Length > 0 ? $"{head} · {notes}" : notes;
    }

    private static string S(int n) => n == 1 ? "" : "s";
}
