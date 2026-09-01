using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AIClient.App.Ui;

/// <summary>A visual card for one plan step (or an implicit working segment). Shows status via a
/// coloured left accent and accumulates tool activity + streamed assistant text inside.</summary>
public sealed class StepCard
{
    private static readonly IBrush Pending = new SolidColorBrush(Color.Parse("#7d7d7d"));
    private static readonly IBrush Running = new SolidColorBrush(Color.Parse("#4a9eff"));
    private static readonly IBrush Done = new SolidColorBrush(Color.Parse("#4caf50"));
    private static readonly IBrush Failed = new SolidColorBrush(Color.Parse("#e05555"));

    private readonly Border _root;
    private readonly TextBlock _status;
    private readonly TextBlock _details;
    private bool _streaming;

    public Control Root => _root;

    public StepCard(string title)
    {
        var header = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        _status = new TextBlock { Text = "pending", Foreground = Pending, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        _details = new TextBlock
        {
            Text = string.Empty,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#b0b0b0")),
            Margin = new Thickness(0, 6, 0, 0),
            IsVisible = false
        };

        _root = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1affffff")),
            BorderBrush = Pending,
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 10, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new StackPanel { Children = { header, _status, _details } }
        };
    }

    public void SetRunning() => SetStatus("running", Running);
    public void SetDone() => SetStatus("done", Done);
    public void SetFailed() => SetStatus("failed", Failed);

    public void AppendLine(string line)
    {
        _streaming = false;
        _details.IsVisible = true;
        _details.Text = _details.Text!.Length == 0 ? line : _details.Text + "\n" + line;
    }

    public void AppendStreaming(string text)
    {
        _details.IsVisible = true;
        if (!_streaming)
        {
            _details.Text = _details.Text!.Length == 0 ? "assistant> " : _details.Text + "\nassistant> ";
            _streaming = true;
        }
        _details.Text += text;
    }

    private void SetStatus(string word, IBrush brush)
    {
        _status.Text = word;
        _status.Foreground = brush;
        _root.BorderBrush = brush;
    }
}
