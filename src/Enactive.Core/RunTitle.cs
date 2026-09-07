namespace Enactive.Core.History;

using Enactive.Core.Templates;

/// <summary>
/// What a run is called in a list.
///
/// <para>The title used to be the request itself, taken from the IntentReceived summary with its
/// line breaks intact. That worked for as long as a request was a sentence somebody typed. A
/// template's goal is four paragraphs, so every card in the history rendered the whole thing over
/// four lines — and every run of the same template looked identical, which is the opposite of what a
/// list is for. It read as duplicated entries.</para>
///
/// <para>So: a templated run is called by its TEMPLATE, which is the honest name for it and is what
/// somebody scanning a list is looking for. Everything else is called by its request, on one line.
/// The full request is not lost — the detail view shows it, and shows it as what it is.</para>
/// </summary>
public static class RunTitle
{
    /// <summary>Long enough to tell two requests apart, short enough to stay on one line in a card.</summary>
    public const int MaxChars = 70;

    /// <summary>
    /// Any text as a single line: newlines and tabs become spaces, runs of space collapse, and what
    /// is left is cut at <see cref="MaxChars"/>.
    /// </summary>
    public static string OneLine(string? text, int max = MaxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var flat = new System.Text.StringBuilder(text!.Length);
        var space = false;

        foreach (var c in text)
        {
            if (c is '\r' or '\n' or '\t' or ' ')
            {
                space = true;
                continue;
            }

            if (space && flat.Length > 0)
                flat.Append(' ');
            space = false;
            flat.Append(c);
        }

        var line = flat.ToString();
        return line.Length <= max ? line : line[..max].TrimEnd() + "…";
    }

    /// <summary>
    /// The name to show for a record, whatever it was stored with.
    ///
    /// <para>Applied when a record is READ, not only when it is written, so history recorded before
    /// this still reads properly. A stored title is data from an older build; repairing it on the way
    /// out is cheaper and more honest than rewriting somebody's run history in place.</para>
    /// </summary>
    public static string For(RunRecord record)
    {
        // A template names the run. "Code Review" is what somebody scanning a list is looking for,
        // and it is the truth about where the run came from.
        if (ResolvedTaskSpec.Parse(record.Spec) is { TemplateName: { Length: > 0 } name })
            return OneLine(name);

        var title = OneLine(record.Title);
        return title.Length > 0 ? title : "(untitled run)";
    }
}
