namespace Enactive.Engine.Tests;

using Enactive.Core.Text;
using Xunit;

/// <summary>
/// Tables, which this parser did not know about.
///
/// <para>Reported 2026-09-22 from the artifact viewer: a generated report's tables arrived as one
/// run-on paragraph with the pipes and the dashes still in it. Every row fell through to the
/// paragraph branch, and consecutive paragraph lines are joined with a space — so an eleven-row
/// inventory became a single line reading
/// <c>| # | Page | Lines |---|---|---| | 1 | README.md | 54 | …</c>.</para>
///
/// <para><b>The separator row is what makes a table a table</b>, and requiring it is the whole of
/// the safety: prose with a pipe in it, a shell pipeline, a line of a code sample — none of them
/// has <c>|---|---|</c> underneath, so none of them is eaten.</para>
/// </summary>
public sealed class MarkdownTableTests
{
    private const string Inventory = """
        | # | Page | Lines |
        |---|---|---|
        | 1 | README.md | 54 |
        | 2 | Overview.md | 102 |
        """;

    [Fact]
    public void A_pipe_table_is_one_block_of_rows_and_cells()
    {
        var blocks = Markdown.Parse(Inventory);

        var table = Assert.Single(blocks);
        Assert.Equal(BlockKind.Table, table.Kind);

        // Three rows: the header and two of data. The separator is punctuation, not content.
        Assert.Equal(3, table.Rows!.Count);
        Assert.Equal(new[] { "#", "Page", "Lines" }, table.Rows[0].Cells.Select(Text));
        Assert.Equal(new[] { "1", "README.md", "54" }, table.Rows[1].Cells.Select(Text));
    }

    /// <summary>The bug as it was seen: the dashes must not survive into anything readable.</summary>
    [Fact]
    public void The_separator_row_is_nowhere_in_the_output()
        => Assert.DoesNotContain("---", Markdown.Parse(Inventory).Single().PlainText, StringComparison.Ordinal);

    /// <summary>
    /// A line with a pipe and nothing under it is a sentence. This is the test that keeps the
    /// feature from costing more than it gives.
    /// </summary>
    [Theory]
    [InlineData("Run `git log | head -3` to see the last three.")]
    [InlineData("| this looks like a row but nothing says it is one")]
    public void Prose_with_a_pipe_in_it_stays_prose(string line)
    {
        var block = Assert.Single(Markdown.Parse(line));

        Assert.Equal(BlockKind.Paragraph, block.Kind);
        Assert.Null(block.Rows);
    }

    /// <summary>Alignment colons are a table's punctuation too, and are not content either.</summary>
    [Fact]
    public void An_aligned_separator_is_still_a_separator()
    {
        var blocks = Markdown.Parse("| a | b |\n|:--|--:|\n| 1 | 2 |");

        Assert.Equal(BlockKind.Table, Assert.Single(blocks).Kind);
        Assert.Equal(2, blocks[0].Rows!.Count);
    }

    /// <summary>A cell is styled text like any other: the report leans on code spans in every row.</summary>
    [Fact]
    public void A_cell_keeps_its_styling()
    {
        var table = Markdown.Parse("| file | note |\n|---|---|\n| `Program.cs` | **changed** |").Single();

        var code = table.Rows![1].Cells[0].Single();
        Assert.Equal(SpanStyle.Code, code.Style);
        Assert.Equal("Program.cs", code.Text);

        Assert.Equal(SpanStyle.Bold, table.Rows[1].Cells[1].Single().Style);
    }

    /// <summary>
    /// A ragged table is a real thing in a hand-written document. It must not throw and must not
    /// lose the cells it does have.
    /// </summary>
    [Fact]
    public void A_row_with_the_wrong_number_of_cells_is_kept_as_it_is()
    {
        var table = Markdown.Parse("| a | b | c |\n|---|---|---|\n| 1 |\n| 1 | 2 | 3 | 4 |").Single();

        Assert.Single(table.Rows![1].Cells);
        Assert.Equal(4, table.Rows[2].Cells.Count);
    }

    /// <summary>What comes after a table is not swallowed by it.</summary>
    [Fact]
    public void The_document_carries_on_after_the_last_row()
    {
        var blocks = Markdown.Parse(Inventory + "\n\nAnd a sentence after it.");

        Assert.Equal(BlockKind.Table, blocks[0].Kind);
        Assert.Equal("And a sentence after it.", blocks[^1].PlainText);
    }

    /// <summary>Copied out of a plain-text view, a table still reads as rows.</summary>
    [Fact]
    public void Plain_text_of_a_table_is_one_line_per_row()
        => Assert.Equal(
            "# | Page | Lines\n1 | README.md | 54\n2 | Overview.md | 102",
            Markdown.Parse(Inventory).Single().PlainText);

    private static string Text(IReadOnlyList<MarkdownSpan> cell)
        => string.Concat(cell.Select(s => s.Text));
}
