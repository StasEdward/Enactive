namespace Enactive.Server.Components.Ui;

using System.Globalization;
using Enactive.Core.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

/// <summary>
/// What <see cref="Markdown"/> parsed, drawn as the page's own elements - the web's MarkdownRender. A model writes headings,
/// bullets, bold and quoted log lines in Markdown, and reading "### PROBLEMS" as three hashes is reading the punctuation
/// instead of the answer. Every decision about what the text MEANS is the shared parser's, tested once for both hosts;
/// this only decides what it looks like. Text goes in as text, never as markup: a model's answer that quotes HTML shows the
/// HTML it quoted. Anything the parser did not recognise stays as the characters the model wrote, and the viewer's Source
/// shows the whole of it.
/// </summary>
public sealed class MarkdownView : ComponentBase
{
    /// <summary>The Markdown, as the model wrote it.</summary>
    [Parameter] public string? Text { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "markdown");
        foreach (var block in Markdown.Parse(Text))
        {
            // One region per block, so the sequence numbers below stay stable whichever kind each one is.
            builder.OpenRegion(2);
            Block(builder, block);
            builder.CloseRegion();
        }
        builder.CloseElement();
    }

    private static void Block(RenderTreeBuilder builder, MarkdownBlock block)
    {
        switch (block.Kind)
        {
            case BlockKind.Heading:
                // Below the viewer's own title: a model that starts at "#" must not outrank the dialog it is shown in.
                builder.OpenElement(0, "h" + Math.Clamp(block.Level + 2, 3, 6).ToString(CultureInfo.InvariantCulture));
                builder.AddAttribute(1, "class", "md-heading");
                builder.OpenRegion(2);
                Spans(builder, block.Spans);
                builder.CloseRegion();
                builder.CloseElement();
                break;
            case BlockKind.Code:
                builder.OpenElement(10, "pre");
                builder.AddAttribute(11, "class", "md-code");
                builder.AddContent(12, block.PlainText);
                builder.CloseElement();
                break;
            case BlockKind.Rule:
                builder.OpenElement(20, "hr");
                builder.CloseElement();
                break;
            case BlockKind.Bullet:
            case BlockKind.Numbered:
                // A list item on its own line with its marker, as the window draws it: the parser hands items, not lists,
                // and a "3." the model wrote stays "3.".
                builder.OpenElement(30, "p");
                builder.AddAttribute(31, "class", "md-item md-indent-" + Math.Min(block.Level, 4).ToString(CultureInfo.InvariantCulture));
                builder.OpenElement(32, "span");
                builder.AddAttribute(33, "class", "md-marker");
                builder.AddAttribute(34, "aria-hidden", "true");
                builder.AddContent(35, block.Kind == BlockKind.Numbered ? block.Marker ?? "•" : "•");
                builder.CloseElement();
                builder.OpenRegion(36);
                Spans(builder, block.Spans);
                builder.CloseRegion();
                builder.CloseElement();
                break;
            case BlockKind.Table:
                Table(builder, block.Rows ?? []);
                break;
            default:
                builder.OpenElement(40, "p");
                builder.OpenRegion(41);
                Spans(builder, block.Spans);
                builder.CloseRegion();
                builder.CloseElement();
                break;
        }
    }

    private static void Table(RenderTreeBuilder builder, IReadOnlyList<MarkdownRow> rows)
    {
        builder.OpenElement(0, "table");
        builder.AddAttribute(1, "class", "md-table");
        for (var r = 0; r < rows.Count; r++)
        {
            builder.OpenElement(2, "tr");
            foreach (var cell in rows[r].Cells)
            {
                // The first row is the header: the parser required the separator under it to call this a table at all.
                builder.OpenElement(3, r == 0 ? "th" : "td");
                builder.OpenRegion(4);
                Spans(builder, cell);
                builder.CloseRegion();
                builder.CloseElement();
            }
            builder.CloseElement();
        }
        builder.CloseElement();
    }

    /// <summary>Styled runs of text. Called inside a region of its caller's, so its sequence numbers are its own.</summary>
    private static void Spans(RenderTreeBuilder builder, IReadOnlyList<MarkdownSpan> spans)
    {
        foreach (var span in spans)
        {
            var element = span.Style switch
            {
                SpanStyle.Bold => "strong",
                SpanStyle.Italic => "em",
                SpanStyle.Code => "code",
                _ => null
            };
            if (element is null)
            {
                builder.AddContent(0, span.Text);
                continue;
            }
            builder.OpenElement(1, element);
            builder.AddContent(2, span.Text);
            builder.CloseElement();
        }
    }
}
