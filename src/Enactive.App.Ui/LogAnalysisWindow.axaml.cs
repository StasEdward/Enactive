namespace Enactive.App.Ui;

using Avalonia.Controls;
// SetTextAsync is an extension in Avalonia 12, not a member of IClipboard.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Enactive.Agents;

/// <summary>
/// What the model made of the log, rendered rather than shown as its own source: a model writes
/// headings, bullets, bold and quoted log lines in Markdown, and reading "### PROBLEMS" as three
/// hashes is reading the punctuation instead of the answer.
///
/// <para>The Source toggle is not a convenience. The renderer handles the subset a model actually
/// writes and leaves everything else as literal characters; a table or a link is therefore readable
/// but not pretty, and anything it got wrong has to be visible somewhere. Copy always copies the
/// Markdown, whichever view is showing — the source is what somebody pastes into an issue.</para>
///
/// <para>The header says which model read it and how much of the log it was given, before the answer
/// rather than after: an analysis of two thirds of a log is a different thing from an analysis of a
/// log, and somebody acting on it has to know which they have.</para>
/// </summary>
public sealed partial class LogAnalysisWindow : Window
{
    private readonly string _markdown;

    internal LogAnalysisWindow(LogAnalysisResult result)
    {
        InitializeComponent();

        _markdown = result.Answer;

        Provenance.Text = result.Provenance;
        Body.Content = MarkdownRender.Build(_markdown);
        Source.Text = _markdown;
        Cost.Text = result.PromptTokens + result.CompletionTokens > 0
            ? $"{result.PromptTokens + result.CompletionTokens:N0} tokens"
            : "";

        ShowSource.IsCheckedChanged += (_, _) =>
        {
            var source = ShowSource.IsChecked == true;
            Rendered.IsVisible = !source;
            Source.IsVisible = source;
        };
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(_markdown);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
