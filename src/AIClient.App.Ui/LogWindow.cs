namespace AIClient.App.Ui;

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AIClient.Core.Diagnostics;
using AIClient.Workspace;

/// <summary>
/// The global log window. One per app, fed live by the <see cref="LogHub"/> and pre-filled from its
/// ring buffer, so it shows the whole message flow across every run: prompts sent to the model, the
/// model's responses, orchestrator/task/tool activity, permissions and reviews. It only observes —
/// filtering here never changes what the engine does. Raising the capture level to Trace turns on the
/// raw byte-level HTTP request/response dumps.
/// </summary>
public sealed class LogWindow : Window
{
    private const int MaxRetained = 20000;

    private readonly LogHub _hub;
    private readonly List<LogEntry> _all = new();
    private readonly ObservableCollection<Row> _visible = new();
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly DispatcherTimer _drain;

    private readonly ListBox _list;
    private readonly TextBox _detail;
    private readonly TextBox _search;
    private readonly CheckBox _autoScroll;
    private readonly CheckBox _pause;
    private readonly ComboBox _levelBox;
    private readonly TextBlock _status;
    private readonly HashSet<LogSource> _sources = new(Enum.GetValues<LogSource>());

    private LogLevel _displayMin = LogLevel.Trace;

    public LogWindow(LogHub hub)
    {
        _hub = hub;
        Title = "AIClient — Global Log";
        Width = 1040;
        Height = 680;

        // ── Toolbar ───────────────────────────────────────────────────────────
        _levelBox = new ComboBox { ItemsSource = Enum.GetNames<LogLevel>(), SelectedIndex = 0, Width = 110 };
        _levelBox.SelectionChanged += (_, _) =>
        {
            if (Enum.TryParse<LogLevel>((string?)_levelBox.SelectedItem, out var lv)) _displayMin = lv;
            Rebuild();
        };

        var rawBox = new CheckBox { Content = "Capture raw wire (Trace)", IsChecked = false };
        rawBox.Click += (_, _) =>
        {
            var on = rawBox.IsChecked == true;
            _hub.MinLevel = on ? LogLevel.Trace : LogLevel.Debug;
            if (on) { _levelBox.SelectedIndex = 0; _displayMin = LogLevel.Trace; Rebuild(); }
        };

        _search = new TextBox { Watermark = "search…", Width = 200 };
        _search.TextChanged += (_, _) => Rebuild();

        _autoScroll = new CheckBox { Content = "Auto-scroll", IsChecked = true };
        _pause = new CheckBox { Content = "Pause", IsChecked = false };

        var clearButton = new Button { Content = "Clear" };
        clearButton.Click += (_, _) => { _all.Clear(); _hub.Clear(); Rebuild(); };

        var exportButton = new Button { Content = "Export" };
        exportButton.Click += (_, _) => Export();

        var toolbar = new WrapPanel { Margin = new Avalonia.Thickness(8, 6, 8, 4) };
        toolbar.Children.Add(new TextBlock { Text = "Level ≥", VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 6, 0) });
        toolbar.Children.Add(_levelBox);
        toolbar.Children.Add(Spacer());
        toolbar.Children.Add(rawBox);
        toolbar.Children.Add(Spacer());
        toolbar.Children.Add(_search);
        toolbar.Children.Add(Spacer());
        toolbar.Children.Add(_autoScroll);
        toolbar.Children.Add(_pause);
        toolbar.Children.Add(clearButton);
        toolbar.Children.Add(exportButton);

        // ── Source filter chips ────────────────────────────────────────────────
        var chips = new WrapPanel { Margin = new Avalonia.Thickness(8, 0, 8, 4) };
        chips.Children.Add(new TextBlock { Text = "Sources:", VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 6, 0) });
        foreach (var src in Enum.GetValues<LogSource>())
        {
            var toggle = new ToggleButton { Content = src.ToString(), IsChecked = true, FontSize = 11, Padding = new Avalonia.Thickness(6, 2, 6, 2), Margin = new Avalonia.Thickness(2, 0, 2, 0) };
            var captured = src;
            void Sync()
            {
                if (toggle.IsChecked == true) _sources.Add(captured); else _sources.Remove(captured);
                Rebuild();
            }
            toggle.Click += (_, _) => Sync();
            chips.Children.Add(toggle);
        }

        // ── List ───────────────────────────────────────────────────────────────
        // Avalonia may call the build func with a null item while recycling virtualized containers,
        // so guard against it rather than dereferencing a null Row.
        var template = new FuncDataTemplate<Row>((row, _) => new TextBlock
        {
            Text = row?.Line ?? string.Empty,
            Foreground = row?.Brush ?? Brushes.Gray,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap
        });
        _list = new ListBox { ItemsSource = _visible, ItemTemplate = template };
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is Row r) ShowDetail(r.Entry);
        };

        // ── Detail ───────────────────────────────────────────────────────────��─
        _detail = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 12,
            Watermark = "Select a row to see its full payload (prompt / response / arguments)…"
        };

        _status = new TextBlock { Margin = new Avalonia.Thickness(8, 2, 8, 4), FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")) };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,190,Auto") };
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(chips, 1);
        Grid.SetRow(_list, 2);
        var detailBorder = new Border { BorderBrush = new SolidColorBrush(Color.Parse("#333333")), BorderThickness = new Avalonia.Thickness(0, 1, 0, 0), Child = _detail };
        Grid.SetRow(detailBorder, 3);
        Grid.SetRow(_status, 4);
        grid.Children.Add(toolbar);
        grid.Children.Add(chips);
        grid.Children.Add(_list);
        grid.Children.Add(detailBorder);
        grid.Children.Add(_status);
        Content = grid;

        // ── Backlog + live feed ─────────────────────────────────────────────────
        foreach (var e in _hub.Snapshot())
            _all.Add(e);
        Rebuild();

        _drain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _drain.Tick += (_, _) => DrainPending();
        _drain.Start();
        _hub.Entry += OnEntry;
        Closed += (_, _) => { _hub.Entry -= OnEntry; _drain.Stop(); };
    }

    // Called on the hub's pump thread — just queue; the UI timer drains.
    private void OnEntry(LogEntry entry) => _pending.Enqueue(entry);

    private void DrainPending()
    {
        if (_pending.IsEmpty) return;

        var appended = false;
        while (_pending.TryDequeue(out var e))
        {
            _all.Add(e);
            if (_all.Count > MaxRetained)
                _all.RemoveRange(0, _all.Count - MaxRetained);

            if (_pause.IsChecked != true && Passes(e))
            {
                _visible.Add(new Row(e));
                appended = true;
            }
        }

        if (appended && _autoScroll.IsChecked == true && _visible.Count > 0)
            _list.ScrollIntoView(_visible[^1]);

        _status.Text = $"{_visible.Count} shown · {_all.Count} captured · hub level ≥ {_hub.MinLevel}";
    }

    private bool Passes(LogEntry e)
    {
        if (e.Level < _displayMin) return false;
        if (!_sources.Contains(e.Source)) return false;
        var q = _search.Text;
        if (!string.IsNullOrWhiteSpace(q))
        {
            var hay = $"{e.Message} {e.Category} {e.Detail}";
            if (hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }
        return true;
    }

    private void Rebuild()
    {
        _visible.Clear();
        foreach (var e in _all)
            if (Passes(e))
                _visible.Add(new Row(e));
        if (_autoScroll.IsChecked == true && _visible.Count > 0)
            _list.ScrollIntoView(_visible[^1]);
        _status.Text = $"{_visible.Count} shown · {_all.Count} captured · hub level ≥ {_hub.MinLevel}";
    }

    private void ShowDetail(LogEntry e)
    {
        var sb = new StringBuilder();
        var run = e.RunId is { } r ? r.ToString("N")[..8] : "(none)";
        sb.Append(e.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append("  ").Append(e.Level)
          .Append("  ").Append(e.Source)
          .Append("  run=").Append(run);
        if (!string.IsNullOrEmpty(e.Category)) sb.Append("  [").Append(e.Category).Append(']');
        sb.AppendLine().AppendLine(e.Message);
        if (!string.IsNullOrEmpty(e.Detail))
            sb.AppendLine(new string('-', 60)).Append(e.Detail);
        _detail.Text = sb.ToString();
    }

    private void Export()
    {
        try
        {
            var dir = FileLogSink.DefaultDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"export-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            using var w = new StreamWriter(path);
            foreach (var r in _visible)
            {
                w.WriteLine(r.Line);
                if (!string.IsNullOrEmpty(r.Entry.Detail))
                    foreach (var line in r.Entry.Detail.Replace("\r\n", "\n").Split('\n'))
                        w.WriteLine("    | " + line);
            }
            _status.Text = $"Exported {_visible.Count} rows → {path}";
        }
        catch (Exception ex)
        {
            _status.Text = "Export failed: " + ex.Message;
        }
    }

    private static Control Spacer() => new Border { Width = 14 };

    /// <summary>A view-model row: the one-line rendering plus its level colour.</summary>
    private sealed class Row
    {
        public LogEntry Entry { get; }
        public string Line { get; }
        public IBrush Brush { get; }

        public Row(LogEntry e)
        {
            Entry = e;
            var run = e.RunId is { } r ? r.ToString("N")[..6] : "------";
            var cat = string.IsNullOrEmpty(e.Category) ? "" : $" [{e.Category}]";
            var msg = e.Message.Replace("\r", " ").Replace("\n", " ");
            if (msg.Length > 200) msg = msg[..200] + "…";
            Line = $"{e.At.ToLocalTime():HH:mm:ss.fff}  {Short(e.Level)}  {e.Source,-12} {run}{cat}  {msg}";
            Brush = new SolidColorBrush(ColorFor(e.Level));
        }

        private static string Short(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warn => "WRN",
            LogLevel.Error => "ERR",
            _ => "?"
        };

        private static Color ColorFor(LogLevel l) => l switch
        {
            LogLevel.Trace => Color.Parse("#6f6f6f"),
            LogLevel.Debug => Color.Parse("#9a9a9a"),
            LogLevel.Info => Color.Parse("#d3d3d3"),
            LogLevel.Warn => Color.Parse("#e0b400"),
            LogLevel.Error => Color.Parse("#e86f6f"),
            _ => Color.Parse("#d3d3d3")
        };
    }
}
