namespace Enactive.App.Ui;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Enactive.Core.Text;

/// <summary>
/// Draws what <see cref="Markdown"/> parsed.
///
/// <para>Deliberately thin: every decision about what the text MEANS was made by the parser, which
/// is testable without an Avalonia application; this only decides what it looks like. That split is
/// why a subset renderer was worth writing rather than taking a dependency — the part that can be
/// wrong is under test, and the part that cannot be tested has nothing in it to get wrong.</para>
///
/// <para>Everything is a <see cref="SelectableTextBlock"/>, so a rendered answer can still be
/// selected and copied piece by piece. A reader who cannot copy the one line they came for is worse
/// off than one reading raw markup.</para>
/// </summary>
internal static class MarkdownRender
{
    private static readonly FontFamily Mono = new("Consolas, Cascadia Mono, Menlo, monospace");

    public static Control Build(string? markdown)
    {
        var panel = new StackPanel { Spacing = 2 };

        foreach (var block in Markdown.Parse(markdown))
            panel.Children.Add(Render(block));

        return panel;
    }

    private static Control Render(MarkdownBlock block) => block.Kind switch
    {
        BlockKind.Heading => Heading(block),
        BlockKind.Code => Code(block),
        BlockKind.Rule => new Border
        {
            Height = 1,
            Margin = new Thickness(0, 10, 0, 10),
            Background = Brand.Line
        },
        BlockKind.Table => Table(block),
        BlockKind.Bullet => Item(block, "•"),
        BlockKind.Numbered => Item(block, block.Marker ?? "•"),
        _ => Paragraph(block)
    };

    private static Control Heading(MarkdownBlock block)
    {
        var text = Selectable(block);
        text.FontWeight = FontWeight.SemiBold;
        text.Foreground = Brand.Text;
        // A model's own structure is usually ### for a section; the sizes stay close together so a
        // document whose author started at level three does not read as a footnote.
        text.FontSize = block.Level switch { 1 => 20, 2 => 17, 3 => 15, _ => 14 };
        text.Margin = new Thickness(0, block.Level <= 2 ? 16 : 12, 0, 4);
        return text;
    }

    private static Control Paragraph(MarkdownBlock block)
    {
        var text = Selectable(block);
        text.Foreground = Brand.TextBody;
        text.Margin = new Thickness(0, 0, 0, 6);
        return text;
    }

    private static Control Item(MarkdownBlock block, string marker)
    {
        var text = Selectable(block);
        text.Foreground = Brand.TextBody;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(14 + block.Level * 16, 0, 0, 4)
        };

        var bullet = new SelectableTextBlock
        {
            Text = marker,
            Foreground = Brand.TextMuted,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top
        };

        grid.Children.Add(bullet);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>
    /// A table, as a grid of selectable cells.
    ///
    /// <para><b>Star columns, not Auto.</b> A report's table holds sentences - "the tool table omits
    /// four registered tools" - and Auto columns size to the longest one, which pushes the rest off
    /// the pane and gives the reader a horizontal scrollbar to fight. Equal shares wrap instead,
    /// which is the behaviour of every document this is likely to show.</para>
    ///
    /// <para>The header is told apart by weight and one rule beneath it rather than by a fill: a
    /// block of colour at the top of a narrow pane reads as a title bar for whatever is under it.
    /// Rows are separated by hairlines so a wrapped cell cannot be misread as belonging to the row
    /// below.</para>
    /// </summary>
    private static Control Table(MarkdownBlock block)
    {
        var rows = block.Rows ?? Array.Empty<MarkdownRow>();
        if (rows.Count == 0)
            return new StackPanel();

        // A ragged row is a real thing in a hand-written table, and it is not worth refusing to
        // draw one over: the widest row decides, and short rows simply leave space.
        var columns = rows.Max(r => r.Cells.Count);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns))),
            Margin = new Thickness(0, 4, 0, 10)
        };

        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            for (var c = 0; c < rows[r].Cells.Count; c++)
            {
                var cell = Selectable(new MarkdownBlock(BlockKind.Paragraph, rows[r].Cells[c]));
                cell.Foreground = r == 0 ? Brand.Text : Brand.TextBody;
                cell.FontWeight = r == 0 ? FontWeight.SemiBold : FontWeight.Normal;
                cell.FontSize = 12;
                cell.Margin = new Thickness(0, 5, 12, 5);

                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }

            // Under the header, and between rows. Not after the last one: a line with nothing under
            // it reads as a row that failed to render.
            if (r < rows.Count - 1)
            {
                var line = new Border
                {
                    Height = 1,
                    Background = r == 0 ? Brand.LineStrong : Brand.Line,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 0)
                };
                Grid.SetRow(line, r);
                Grid.SetColumn(line, 0);
                Grid.SetColumnSpan(line, columns);
                grid.Children.Add(line);
            }
        }

        return grid;
    }

    private static Control Code(MarkdownBlock block)
        => new Border
        {
            Background = Brand.InputFill,
            BorderBrush = Brand.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 4, 0, 8),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new SelectableTextBlock
                {
                    Text = block.PlainText,
                    FontFamily = Mono,
                    FontSize = 12,
                    Foreground = Brand.Text,
                    TextWrapping = TextWrapping.NoWrap
                }
            }
        };

    private static SelectableTextBlock Selectable(MarkdownBlock block)
    {
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };

        foreach (var span in block.Spans)
        {
            var run = new Run(span.Text);
            switch (span.Style)
            {
                case SpanStyle.Bold:
                    run.FontWeight = FontWeight.SemiBold;
                    run.Foreground = Brand.Text;
                    break;
                case SpanStyle.Italic:
                    run.FontStyle = FontStyle.Italic;
                    break;
                case SpanStyle.Code:
                    run.FontFamily = Mono;
                    run.Foreground = Brand.AccentSoft;
                    break;
            }
            text.Inlines!.Add(run);
        }

        return text;
    }
}
