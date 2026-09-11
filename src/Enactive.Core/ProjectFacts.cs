namespace Enactive.Core.Memory;

/// <summary>
/// What a finished run is allowed to tell the project about itself.
///
/// <para><b>Why this exists.</b> Project memory is read into the PROMPT of every step of every
/// later run (<c>ContextProvider.MemoryLimit</c> bounds it at twenty entries for exactly that
/// reason). It is therefore not a log: an entry that is not a fact about the project is a
/// permanent tax on the context window and on the bill.</para>
///
/// <para>On 2026-09-11 Stas read the assembled prompt in his own exported log and found, under
/// "What this project has already decided and done" in a workspace rooted at
/// <c>ArcMapConditions.App</c>: <i>"Запусти калькулятор" — Completed</i>, <i>"запусти Total
/// Commander" — Incomplete</i>, and <c>git: denied</c> six times. He had been using the chat window
/// as a console. The chat window belongs to whichever workspace is open, so the app filed his
/// errands as things ArcMapConditions had decided and done.</para>
///
/// <para><b>The test NOT used:</b> "is this related to the project". Nothing can answer that
/// without guessing at intent, and a guess wrong in the cautious direction silently throws away
/// real project knowledge.</para>
///
/// <para><b>The test used:</b> did the run TOUCH the workspace — read or write a file under the
/// root. Mechanical, decided from what the run actually did, no judgement about meaning.</para>
/// </summary>
public static class ProjectFacts
{
    /// <summary>
    /// Tools whose use is evidence that the run reached into the workspace.
    ///
    /// <para>The file tools speak for themselves. <c>git</c> and <c>docker</c> are here because
    /// they are scoped to the workspace by construction — this engine gives them no way to address
    /// anything else — so invoking one IS reaching into it.</para>
    ///
    /// <para><b>The shells are deliberately absent</b>, and this is the one judgement in the file.
    /// <c>run_command</c> and <c>run_powershell</c> always start in the workspace root, so counting
    /// them would make every run "touch the workspace" and the test would decide nothing. That has
    /// a price, stated plainly rather than hidden: a run that did its whole job through the shell —
    /// <c>dotnet test</c> and nothing else — writes no memory. Naming the price is the point; the
    /// alternative was a rule that filtered nothing, which is how this got here.</para>
    /// </summary>
    private static readonly HashSet<string> Reaches = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "write_file", "list_dir", "git", "docker"
    };

    /// <summary>Whether using this tool is evidence of reaching into the workspace.</summary>
    public static bool Reaching(string? toolName)
        => toolName is { Length: > 0 } name && Reaches.Contains(name.Trim());

    /// <summary>
    /// Whether a run that used these tools and produced these artifacts learned anything about the
    /// project worth keeping.
    /// </summary>
    /// <param name="toolNames">
    /// Every tool the run invoked, by name. Nulls and blanks are ignored rather than rejected: a
    /// record written before tool names were carried as values says nothing here, and saying
    /// nothing must not be read as saying no.
    /// </param>
    /// <param name="artifacts">
    /// Files the run wrote into the workspace. A write counts on its own even when the request was
    /// an errand — <i>"сходи на сайт … — Changed: news2.md"</i> left a file in the project, and the
    /// memory entry is then the only surviving explanation of why that file is there.
    /// </param>
    public static bool WorthRemembering(IEnumerable<string?> toolNames, IReadOnlyCollection<string> artifacts)
        => artifacts.Count > 0 || toolNames.Any(Reaching);

    /// <summary>
    /// Whether this decision is already in the project's memory.
    ///
    /// <para>A standing answer re-recorded by every run that asks is not a second decision. In the
    /// log above, <c>git: denied</c> occupied six of the twenty places a run is given — six
    /// copies of one fact, crowding out five others and teaching a model nothing the first copy
    /// had not.</para>
    ///
    /// <para>Compared on CONTENT and only within <see cref="MemoryKind.Decision"/>. Two outcomes
    /// with identical text are two real events — the same task run twice — and are both kept; two
    /// decisions with identical text are one standing answer, because that is what a decision is.
    /// </para>
    /// </summary>
    public static bool AlreadyDecided(IEnumerable<MemoryEntry> existing, string decision)
        => existing.Any(e => string.Equals(e.Kind, MemoryKind.Decision, StringComparison.Ordinal)
                          && string.Equals(e.Content?.Trim(), decision.Trim(), StringComparison.OrdinalIgnoreCase));
}
