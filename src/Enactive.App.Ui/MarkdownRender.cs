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
