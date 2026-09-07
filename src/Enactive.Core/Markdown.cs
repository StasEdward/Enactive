namespace Enactive.Core.Text;

using System.Text;

/// <summary>A run of text inside a line, and how it should read.</summary>
public enum SpanStyle { Plain, Bold, Italic, Code }

/// <summary>One styled run of text.</summary>
public sealed record MarkdownSpan(string Text, SpanStyle Style = SpanStyle.Plain);

/// <summary>What kind of block a line belongs to.</summary>
public enum BlockKind { Paragraph, Heading, Bullet, Numbered, Code, Rule }

/// <summary>
/// One block of a document.
/// </summary>
/// <param name="Level">Heading depth (1-6), or the indent depth of a list item. Zero otherwise.</param>
/// <param name="Marker">What a numbered item is numbered with, kept verbatim so "3." stays "3.".</param>
public sealed record MarkdownBlock(
    BlockKind Kind,
    IReadOnlyList<MarkdownSpan> Spans,
    int Level = 0,
    string? Marker = null)
{
    /// <summary>The block's text with no styling, for a plain-text fallback and for tests.</summary>
    public string PlainText => string.Concat(Spans.Select(s => s.Text));
}

/// <summary>
/// Enough Markdown to render what a model writes, and no more.
///
/// <para><b>Why not a library.</b> The whole vocabulary in play here is headings, bold, inline code,
/// bullets, numbered items and fenced code — that is what an analysis or a review actually contains.
/// A parser for that is small enough to read in one sitting and to test exhaustively, where a
/// dependency would bring a full CommonMark implementation, its own Avalonia version constraints and
/// a rendering pipeline nobody here controls, to display six constructs.</para>
///
/// <para><b>What it does with the rest.</b> Nothing clever: anything it does not recognise stays on
/// screen as the characters the model wrote. A table renders as its pipes, a link as
/// <c>[text](url)</c>. That is deliberate — text a renderer silently swallowed is worse than text
/// that looks like markup, because the reader can still read the second one.</para>
/// </summary>
public static class Markdown
{
    public static IReadOnlyList<MarkdownBlock> Parse(string? text)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrWhiteSpace(text))
            return blocks;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var code = new List<string>();
        var inCode = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;
            blocks.Add(new MarkdownBlock(BlockKind.Paragraph, Spans(string.Join(" ", paragraph))));
            paragraph.Clear();
        }

        void FlushCode()
        {
            blocks.Add(new MarkdownBlock(
                BlockKind.Code, new[] { new MarkdownSpan(string.Join("\n", code), SpanStyle.Code) }));
            code.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // A fence opens and closes verbatim territory: nothing inside it is markup, which is the
            // whole point of a code block and the one place a "helpful" parser does real damage.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode) { FlushCode(); inCode = false; }
                else { FlushParagraph(); inCode = true; }
                continue;
            }

            if (inCode)
            {
                code.Add(raw);
                continue;
            }

            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(BlockKind.Rule, Array.Empty<MarkdownSpan>()));
                continue;
            }

            if (HeadingLevel(trimmed) is { } level)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(
                    BlockKind.Heading, Spans(trimmed[(level + 1)..].Trim()), level));
                continue;
            }

            if (trimmed.Length > 1 && trimmed[0] is '-' or '*' or '•' && trimmed[1] == ' ')
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(
                    BlockKind.Bullet, Spans(trimmed[2..].Trim()), indent / 2));
                continue;
            }

            if (NumberedMarker(trimmed) is { } marker)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(
                    BlockKind.Numbered, Spans(trimmed[marker.Length..].Trim()), indent / 2, marker.Trim()));
                continue;
            }

            paragraph.Add(trimmed);
        }

        // An unclosed fence still renders as code rather than vanishing: the model was cut off, and
        // showing what it did write beats showing nothing.
        if (inCode && code.Count > 0)
            FlushCode();
        FlushParagraph();

        return blocks;
    }

    private static int? HeadingLevel(string line)
    {
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
            hashes++;

        return hashes is > 0 and <= 6 && hashes < line.Length && line[hashes] == ' ' ? hashes : null;
    }

    /// <summary>"1." or "12)" at the start of a line, including the space after it.</summary>
    private static string? NumberedMarker(string line)
    {
        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
            digits++;

        if (digits == 0 || digits + 1 >= line.Length)
            return null;

        return line[digits] is '.' or ')' && line[digits + 1] == ' '
            ? line[..(digits + 2)]
            : null;
    }

    /// <summary>
    /// Splits a line into styled runs. Code first, because a backtick suspends every other rule -
    /// <c>`**not bold**`</c> is four asterisks and a phrase, and a parser that bolds it has changed
    /// what the text says.
    /// </summary>
    public static IReadOnlyList<MarkdownSpan> Spans(string line)
    {
        var spans = new List<MarkdownSpan>();
        var text = new StringBuilder();

        void Flush()
        {
            if (text.Length == 0)
                return;
            spans.Add(new MarkdownSpan(text.ToString()));
            text.Clear();
        }

        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '`' && Closing(line, i + 1, "`") is { } code)
            {
                Flush();
                spans.Add(new MarkdownSpan(line[(i + 1)..code], SpanStyle.Code));
                i = code;
                continue;
            }

            if (Starts(line, i, "**") && Closing(line, i + 2, "**") is { } bold)
            {
                Flush();
                spans.Add(new MarkdownSpan(line[(i + 2)..bold], SpanStyle.Bold));
                i = bold + 1;
                continue;
            }

            // Single asterisk, and only when it closes: a lone '*' in prose is a lone '*'.
            if (line[i] == '*' && !Starts(line, i, "**") && Closing(line, i + 1, "*") is { } italic)
            {
                Flush();
                spans.Add(new MarkdownSpan(line[(i + 1)..italic], SpanStyle.Italic));
                i = italic;
                continue;
            }

            text.Append(line[i]);
        }

        Flush();
        return spans.Count > 0 ? spans : new[] { new MarkdownSpan("") };
    }

    private static bool Starts(string line, int at, string token)
        => at + token.Length <= line.Length && line.AsSpan(at, token.Length).SequenceEqual(token);

    /// <summary>Where the closing token starts, or null when there is none and the marker is literal.</summary>
    private static int? Closing(string line, int from, string token)
    {
        if (from >= line.Length)
            return null;

        var at = line.IndexOf(token, from, StringComparison.Ordinal);
        // An empty span (`` or ****) is not emphasis, it is punctuation the author typed.
        return at > from ? at : null;
    }
}
