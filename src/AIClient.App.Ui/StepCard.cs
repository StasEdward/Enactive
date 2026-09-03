using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AIClient.App.Ui;

/// <summary>A visual card for one plan step. Mirrors a tool-call log, not a chat transcript: a short
/// "what's happening now" line stays visible, and everything the agent actually DID - commands run,
/// files touched, brief remarks - collapses behind a single "Used N tools, ran M commands, edited K
/// files · J notes" toggle, one click away when something needs checking. The model's raw streamed
/// prose is never shown verbatim; short remarks are logged as one-line notes instead.</summary>
public sealed class StepCard
{
    private static readonly IBrush Pending = new SolidColorBrush(Color.Parse("#7d7d7d"));
    private static readonly IBrush Running = new SolidColorBrush(Color.Parse("#4a9eff"));
    private static readonly IBrush Done = new SolidColorBrush(Color.Parse("#4caf50"));
    private static readonly IBrush Failed = new SolidColorBrush(Color.Parse("#e05555"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#e0a83f"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#8a8a8a"));
    private static readonly IBrush MutedTextHover = new SolidColorBrush(Color.Parse("#c0c0c0"));
    private static readonly IBrush CommandIconBrush = new SolidColorBrush(Color.Parse("#4a9eff"));
    private static readonly IBrush FileIconBrush = new SolidColorBrush(Color.Parse("#6fae6f"));
    private static readonly IBrush ToolIconBrush = new SolidColorBrush(Color.Parse("#9a9a9a"));
    private static readonly IBrush NoteDotBrush = new SolidColorBrush(Color.Parse("#8a8a8a"));
    private static readonly IBrush EntryText = new SolidColorBrush(Color.Parse("#c8c8c8"));
    private static readonly IBrush NoteText = new SolidColorBrush(Color.Parse("#dcdcdc"));
    private static readonly IBrush DetailText = new SolidColorBrush(Color.Parse("#7f7f7f"));

    private const string CommandIcon = "$";
    private const string FileIcon = "▤";
    private const string ToolIcon = "▸";
    private const string NoteIcon = "•";

    private readonly Border _root;
    private readonly TextBlock _statusWord;
    private readonly TextBlock _activity;
    private readonly TextBlock _chevron;
    private readonly TextBlock _toggleLabel;
    private readonly Border _toggleRow;
    private readonly StackPanel _entriesPanel;
    private readonly Border _entriesWrap;
    private readonly StringBuilder _noteBuffer = new();

    private TextBlock? _lastEntryDetail;
    private bool _expanded;
    private bool _userExpanded; // once the user toggles it manually, stop overriding their choice
    private int _toolCount;
    private int _commandCount;
    private int _fileCount;
    private int _noteCount;

    public Control Root => _root;

    public StepCard(string title)
    {
        var header = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        _statusWord = new TextBlock { Text = "pending", Foreground = Pending, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        _activity = new TextBlock
        {
            Text = "Waiting…",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        };

        // Lightweight disclosure toggle - a plain clickable row (chevron + auto-computed summary),
        // no button chrome. Hidden until the step has logged at least one entry.
        _chevron = new TextBlock
        {
            Text = "›",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = MutedText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        _toggleLabel = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11,
            Foreground = MutedText,
            VerticalAlignment = VerticalAlignment.Center
        };
        _toggleRow = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(0, 2, 0, 2),
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { _chevron, _toggleLabel }
            }
        };
        _toggleRow.PointerEntered += (_, _) => { _chevron.Foreground = MutedTextHover; _toggleLabel.Foreground = MutedTextHover; };
        _toggleRow.PointerExited += (_, _) => { _chevron.Foreground = MutedText; _toggleLabel.Foreground = MutedText; };
        _toggleRow.PointerPressed += (_, e) =>
        {
            _userExpanded = true;
            SetExpanded(!_expanded);
            e.Handled = true;
        };

        // The flat action log: one row per tool call / note, in the order they happened - icon,
        // short label, and (for tool calls) an optional muted one-line result underneath.
        _entriesPanel = new StackPanel();
        _entriesWrap = new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(10, 4, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#2f2f2f")),
            BorderThickness = new Thickness(2, 0, 0, 0),
            IsVisible = false,
            Child = _entriesPanel
        };

        _root = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1affffff")),
            BorderBrush = Pending,
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 10, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new StackPanel { Children = { header, _statusWord, _activity, _toggleRow, _entriesWrap } }
        };
    }

    public void SetRunning() => SetStatus("running", Running);

    public void SetDone()
    {
        FlushPendingNote();
        SetStatus("done", Done);
    }

    public void SetFailed()
    {
        FlushPendingNote();
        SetStatus("failed", Failed);
    }

    /// <summary>The short "what's happening now" line shown above the log - e.g. "Thinking…",
    /// "Writing WSFC_Workgroup_Cluster_Setup.md…", "Waiting for your approval…".</summary>
    public void SetActivity(string text) => _activity.Text = text;

    /// <summary>Pops the card open so something that needs the user's attention (a recovered
    /// implicit tool call, an error) isn't hidden behind the collapsed default. Only auto-expands
    /// once - if the user has already manually collapsed it back, that choice is respected.</summary>
    public void ExpandForAttention()
    {
        if (!_userExpanded)
            SetExpanded(true);
        _statusWord.Foreground = Warning;
        _root.BorderBrush = Warning;
    }

    /// <summary>Buffers a chunk of the assistant's streamed reply text. It is never shown verbatim -
    /// it is folded into a single short note the next time a tool runs or the step ends, matching how
    /// Claude Desktop logs a brief remark between actions instead of dumping raw reasoning.</summary>
    public void AppendAssistantText(string delta) => _noteBuffer.Append(delta);

    /// <summary>Logs a shell command that was run.</summary>
    public void AddCommand(string commandText)
    {
        FlushPendingNote();
        _toolCount++;
        _commandCount++;
        _lastEntryDetail = AddEntryRow(CommandIcon, CommandIconBrush, "Ran: " + Truncate(commandText, 90), EntryText);
        UpdateSummary();
    }

    /// <summary>Logs a file operation - verb is a short past-tense word like "Wrote", "Read", "Listed".</summary>
    public void AddFileOp(string verb, string path)
    {
        FlushPendingNote();
        _toolCount++;
        if (string.Equals(verb, "Wrote", StringComparison.Ordinal))
            _fileCount++;
        _lastEntryDetail = AddEntryRow(FileIcon, FileIconBrush, $"{verb} {path}", EntryText);
        UpdateSummary();
    }

    /// <summary>Logs any other tool call that doesn't fit the command/file shapes above.</summary>
    public void AddGenericTool(string label)
    {
        FlushPendingNote();
        _toolCount++;
        _lastEntryDetail = AddEntryRow(ToolIcon, ToolIconBrush, label, EntryText);
        UpdateSummary();
    }

    /// <summary>Attaches a short one-line result under the most recently logged tool entry.</summary>
    public void AppendEntryDetail(string text)
    {
        if (_lastEntryDetail is null)
            return;
        var flat = Truncate(text.Replace('\n', ' ').Replace('\r', ' ').Trim(), 160);
        if (flat.Length == 0)
            return;
        _lastEntryDetail.Text = flat;
        _lastEntryDetail.IsVisible = true;
    }

    /// <summary>Logs an explicit short note (a warning, a review/decision remark, an artifact
    /// mention) - always in order relative to any buffered streamed text.</summary>
    public void AddNote(string text)
    {
        FlushPendingNote();
        AddNoteEntry(text);
        UpdateSummary();
    }

    /// <summary>Commits any buffered streamed text as a single note entry, if there is any.</summary>
    public void FlushPendingNote()
    {
        if (_noteBuffer.Length == 0)
            return;
        var text = _noteBuffer.ToString();
        _noteBuffer.Clear();
        AddNoteEntry(text);
        UpdateSummary();
    }

    private void AddNoteEntry(string text)
    {
        var flat = Truncate(text.Replace('\n', ' ').Replace('\r', ' ').Trim(), 220);
        if (flat.Length == 0)
            return;
        _noteCount++;
        AddEntryRow(NoteIcon, NoteDotBrush, flat, NoteText, bold: true);
    }

    private TextBlock AddEntryRow(string icon, IBrush iconBrush, string label, IBrush textBrush, bool bold = false)
    {
        var iconBlock = new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 12,
            Foreground = iconBrush,
            Width = 16,
            TextAlignment = TextAlignment.Center
        };
        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = textBrush
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 3, 0, 0),
            Children = { iconBlock, labelBlock }
        };
        var detail = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11,
            Foreground = DetailText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 0, 0, 0),
            IsVisible = false
        };

        _entriesPanel.Children.Add(row);
        _entriesPanel.Children.Add(detail);
        return detail;
    }

    private void UpdateSummary()
    {
        var summary = BuildSummary();
        _toggleRow.IsVisible = summary.Length > 0;
        _toggleLabel.Text = summary;
    }

    private string BuildSummary()
    {
        var parts = new List<string>();
        if (_toolCount > 0)
            parts.Add($"Used {_toolCount} tool{(_toolCount == 1 ? "" : "s")}");
        if (_commandCount > 0)
            parts.Add($"ran {_commandCount} command{(_commandCount == 1 ? "" : "s")}");
        if (_fileCount > 0)
            parts.Add($"edited {_fileCount} file{(_fileCount == 1 ? "" : "s")}");

        var head = string.Join(", ", parts);
        if (_noteCount == 0)
            return head;

        var noteText = $"{_noteCount} note{(_noteCount == 1 ? "" : "s")}";
        return head.Length > 0 ? $"{head} · {noteText}" : noteText;
    }

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _entriesWrap.IsVisible = expanded;
        // "›" collapsed, "⌄" open - a plain glyph swap avoids depending on Avalonia's transform API,
        // which keeps this control simple to reason about and easy to compile confidently.
        _chevron.Text = expanded ? "⌄" : "›";
    }

    private void SetStatus(string word, IBrush brush)
    {
        _statusWord.Text = word;
        _statusWord.Foreground = brush;
        _root.BorderBrush = brush;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
