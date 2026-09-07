namespace Enactive.App.Ui;

using Avalonia.Controls;
// SetTextAsync is an extension in Avalonia 12, not a member of IClipboard.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Enactive.Agents;

/// <summary>
/// What the model made of the log. Deliberately a plain window: the answer is prose, and the useful
/// things to do with prose are read it, select part of it and copy it.
///
/// <para>The header says which model read it and how much of it the model was given, before the
/// answer rather than after — an analysis of two thirds of a log is a different thing from an
/// analysis of a log, and somebody acting on it has to know which they have.</para>
/// </summary>
public sealed partial class LogAnalysisWindow : Window
{
    internal LogAnalysisWindow(LogAnalysisResult result)
    {
        InitializeComponent();

        Provenance.Text = result.Provenance;
        Answer.Text = result.Answer;
        Cost.Text = result.PromptTokens + result.CompletionTokens > 0
            ? $"{result.PromptTokens + result.CompletionTokens:N0} tokens"
            : "";
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(Answer.Text ?? "");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
