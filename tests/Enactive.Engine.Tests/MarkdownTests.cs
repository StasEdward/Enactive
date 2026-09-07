namespace Enactive.Engine.Tests;

using Enactive.Core.Text;
using Xunit;

/// <summary>
/// The Markdown a model actually writes, rendered rather than shown as its own source.
///
/// <para>Deliberately a subset: headings, bold, italic, inline code, bullets, numbered items and
/// fenced code is the whole vocabulary an analysis or a review uses. What matters as much as what it
/// renders is what it does with the rest — anything unrecognised stays on screen as the characters
/// the model wrote, because text a renderer silently swallowed is worse than text that looks like
/// markup. The reader can still read the second one.</para>
/// </summary>
public sealed class MarkdownTests
{
    private static MarkdownBlock One(string text) => Assert.Single(Markdown.Parse(text));

    // ── blocks ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("# Title", 1, "Title")]
    [InlineData("### WHAT WAS ASKED FOR", 3, "WHAT WAS ASKED FOR")]
    [InlineData("###### Deep", 6, "Deep")]
    public void A_heading_carries_its_depth(string line, int level, string text)
    {
        var block = One(line);

        Assert.Equal(BlockKind.Heading, block.Kind);
        Assert.Equal(level, block.Level);
        Assert.Equal(text, block.PlainText);
    }

    /// <summary>Seven hashes is not a heading, and a hash with no space is a sentence about C#.</summary>
    [Theory]
    [InlineData("####### too deep")]
    [InlineData("#nospace")]
    [InlineData("#")]
    public void Something_that_only_looks_like_a_heading_is_a_paragraph(string line)
        => Assert.Equal(BlockKind.Paragraph, One(line).Kind);

    [Theory]
    [InlineData("- an item")]
    [InlineData("* an item")]
    [InlineData("• an item")]
    public void A_bullet_is_a_bullet_however_it_is_marked(string line)
    {
        var block = One(line);

        Assert.Equal(BlockKind.Bullet, block.Kind);
        Assert.Equal("an item", block.PlainText);
    }

    [Theory]
    [InlineData("1. first", "1.")]
    [InlineData("12) twelfth", "12)")]
    public void A_numbered_item_keeps_the_number_the_author_wrote(string line, string marker)
    {
        var block = One(line);

        Assert.Equal(BlockKind.Numbered, block.Kind);
        Assert.Equal(marker, block.Marker);
    }

    /// <summary>
    /// A model writes "1786 in, 15 out" and "2024. The run failed" in ordinary prose. Neither is a
    /// list, and turning them into one rewrites the sentence.
    /// </summary>
    [Theory]
    [InlineData("1786 in, 15 out")]
    [InlineData("-not a bullet")]
    [InlineData("*emphasis at the start of a line*")]
    public void Prose_that_starts_with_a_marker_character_is_still_prose(string line)
        => Assert.Equal(BlockKind.Paragraph, One(line).Kind);

    [Fact]
    public void An_indented_bullet_says_how_deep_it_is()
    {
        var blocks = Markdown.Parse("- top\n  - nested\n    - deeper");

        Assert.Equal(new[] { 0, 1, 2 }, blocks.Select(b => b.Level));
        Assert.All(blocks, b => Assert.Equal(BlockKind.Bullet, b.Kind));
    }

    /// <summary>Wrapped lines are one paragraph, the way every Markdown renderer treats them.</summary>
    [Fact]
    public void Consecutive_lines_are_one_paragraph_and_a_blank_line_ends_it()
    {
        var blocks = Markdown.Parse("one line\nand its continuation\n\na second paragraph");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("one line and its continuation", blocks[0].PlainText);
        Assert.Equal("a second paragraph", blocks[1].PlainText);
    }

    // ── code ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inside a fence nothing is markup. This is the one place a helpful parser does real damage: a
    /// log line full of asterisks and hashes is the exact thing an analysis quotes.
    /// </summary>
    [Fact]
    public void Nothing_inside_a_fence_is_markup()
    {
        var block = One("```\n# not a heading\n- not a bullet **not bold**\n```");

        Assert.Equal(BlockKind.Code, block.Kind);
        Assert.Equal("# not a heading\n- not a bullet **not bold**", block.PlainText);
    }

    [Fact]
    public void A_language_after_the_fence_does_not_become_content()
    {
        var block = One("```csharp\nvar x = 1;\n```");

        Assert.Equal(BlockKind.Code, block.Kind);
        Assert.Equal("var x = 1;", block.PlainText);
    }

    /// <summary>
    /// A model cut off mid-block leaves the fence open. What it did write is still shown - the
    /// alternative is a truncated answer that renders as nothing at all.
    /// </summary>
    [Fact]
    public void An_unclosed_fence_still_shows_what_was_written()
    {
        var block = One("```\nthe model stopped here");

        Assert.Equal(BlockKind.Code, block.Kind);
        Assert.Equal("the model stopped here", block.PlainText);
    }

    // ── spans ───────────────────────────────────────────────────────────────

    [Fact]
    public void Bold_italic_and_code_are_recognised_inside_a_line()
    {
        var spans = Markdown.Spans("plain **bold** and *italic* and `code` end");

        Assert.Equal(
            new[] { SpanStyle.Plain, SpanStyle.Bold, SpanStyle.Plain, SpanStyle.Italic,
                    SpanStyle.Plain, SpanStyle.Code, SpanStyle.Plain },
            spans.Select(s => s.Style));

        Assert.Equal("bold", spans[1].Text);
        Assert.Equal("italic", spans[3].Text);
        Assert.Equal("code", spans[5].Text);
    }

    /// <summary>
    /// A backtick suspends every other rule. `**not bold**` is four asterisks and a phrase, and a
    /// parser that bolds it has changed what the text says — which matters most when the text is a
    /// quoted log line, the thing an analysis is required to quote exactly.
    /// </summary>
    [Fact]
    public void Code_suspends_the_other_markers()
    {
        var spans = Markdown.Spans("`response ← (0 chars, **0** tool call(s))`");

        var only = Assert.Single(spans);
        Assert.Equal(SpanStyle.Code, only.Style);
        Assert.Contains("**0**", only.Text);
    }

    /// <summary>An unclosed marker is a character the author typed, not the start of emphasis.</summary>
    [Theory]
    [InlineData("2 * 3 = 6")]
    [InlineData("**unclosed bold")]
    [InlineData("a `backtick that never closes")]
    [InlineData("****")]
    public void A_marker_that_never_closes_stays_literal(string line)
    {
        var spans = Markdown.Spans(line);

        Assert.All(spans, s => Assert.Equal(SpanStyle.Plain, s.Style));
        Assert.Equal(line, string.Concat(spans.Select(s => s.Text)));
    }

    /// <summary>
    /// The property that matters more than any single rule: rendering never loses characters. Every
    /// visible character of the source has to survive into some span, or the reader is looking at an
    /// answer that has quietly had pieces taken out of it.
    /// </summary>
    [Theory]
    [InlineData("### A heading with `code` and **bold**")]
    [InlineData("- a bullet with *emphasis* and a stray * asterisk")]
    [InlineData("1. numbered with **bold** and `code`")]
    [InlineData("plain prose, nothing special")]
    [InlineData("edge ** case `` with empty markers")]
    public void Nothing_visible_is_ever_dropped(string line)
    {
        var rendered = string.Concat(Markdown.Parse(line).SelectMany(b => b.Spans).Select(s => s.Text));

        Assert.Equal(
            Strip(line).Replace(" ", ""),
            rendered.Replace(" ", ""));
    }

    /// <summary>The source with only the markers that were CONSUMED removed.</summary>
    private static string Strip(string line)
    {
        var spans = Markdown.Parse(line).SelectMany(b => b.Spans).ToArray();
        var consumed = line;

        // Headings and list markers are the block's own prefix; everything else has to survive.
        foreach (var prefix in new[] { "### ", "## ", "# ", "- ", "1. " })
            if (consumed.StartsWith(prefix, StringComparison.Ordinal))
            {
                consumed = consumed[prefix.Length..];
                break;
            }

        foreach (var span in spans.Where(s => s.Style != SpanStyle.Plain))
        {
            var marker = span.Style switch
            {
                SpanStyle.Bold => "**",
                SpanStyle.Italic => "*",
                _ => "`"
            };
            consumed = consumed.Replace(marker + span.Text + marker, span.Text, StringComparison.Ordinal);
        }

        return consumed;
    }

    // ── the shape the analyst actually produces ─────────────────────────────

    /// <summary>
    /// The real answer from 2026-09-07, which is what this exists to display. Parsing it is the only
    /// test here that proves the subset was chosen from evidence rather than from taste.
    /// </summary>
    [Fact]
    public void A_real_analysis_parses_into_the_shape_it_looks_like()
    {
        const string answer = """
            ### WHAT WAS ASKED FOR
            The user requested a review of all changes since the last commit.

            ### PROBLEMS
            **Total Inaction**
            The developer agent (`gemma4-12b:latest`) failed to perform any work.
            - **Evidence:** `response ← ollama/gemma4-12b:latest (0 chars, 0 tool call(s))`
            - **Result:** The reviewer failed the task.
            """;

        var blocks = Markdown.Parse(answer);

        Assert.Equal(2, blocks.Count(b => b.Kind == BlockKind.Heading));
        Assert.Equal(2, blocks.Count(b => b.Kind == BlockKind.Bullet));
        Assert.Contains(blocks, b => b.Spans.Any(s => s.Style == SpanStyle.Bold && s.Text == "Total Inaction"));
        Assert.Contains(blocks, b => b.Spans.Any(s => s.Style == SpanStyle.Code
                                                   && s.Text.Contains("0 tool call(s)", StringComparison.Ordinal)));
    }

    [Fact]
    public void Nothing_at_all_parses_to_nothing_rather_than_throwing()
    {
        Assert.Empty(Markdown.Parse(null));
        Assert.Empty(Markdown.Parse(""));
        Assert.Empty(Markdown.Parse("   \n\n  "));
    }
}
