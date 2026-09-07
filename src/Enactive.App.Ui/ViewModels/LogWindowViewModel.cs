namespace Enactive.App.Ui.ViewModels;

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using Enactive.App.Ui.Mvvm;
using Enactive.Agents;
using Enactive.Core.Diagnostics;
using Enactive.Workspace;

/// <summary>One rendered log line: the single-line summary, its level colour, and the entry behind it.</summary>
internal sealed class LogRow
{
    public LogRow(LogEntry entry)
    {
        Entry = entry;
        // Padded so the category and message columns stay aligned whether or not a line has a step.
        var run = LogWindowViewModel.RunToken(entry).PadRight(9);
        var cat = string.IsNullOrEmpty(entry.Category) ? "" : $" [{entry.Category}]";
        var msg = entry.Message.Replace("\r", " ").Replace("\n", " ");
        if (msg.Length > 200)
            msg = msg[..200] + "…";
        Line = $"{entry.At.ToLocalTime():HH:mm:ss.fff}  {Short(entry.Level)}  {entry.Source,-12} {run}{cat}  {msg}";
        Brush = new SolidColorBrush(ColorFor(entry.Level));
    }

    public LogEntry Entry { get; }
    public string Line { get; }
    public IBrush Brush { get; }

    private static string Short(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "?"
    };

    private static Color ColorFor(LogLevel level) => level switch
    {
        LogLevel.Trace => Brand.Ink400,
        LogLevel.Debug => Brand.Ink300,
        LogLevel.Info => Brand.Ink100,
        LogLevel.Warn => Brand.WarningColor,
        LogLevel.Error => Brand.DangerColor,
        _ => Brand.Ink100
    };
}

/// <summary>One source chip. Unticking it hides that plane of the log.</summary>
internal sealed class SourceToggle : ObservableObject
{
    private readonly Action _changed;
    private bool _isEnabled = true;

    public SourceToggle(LogSource source, Action changed)
    {
        Source = source;
        _changed = changed;
    }

    public LogSource Source { get; }

    public string Name => Source.ToString();

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value))
                _changed();
        }
    }
}

/// <summary>
/// The global log: everything the hub has seen, filtered for display. It only observes — no filter
/// here changes what the engine does. The one exception is the raw-wire switch, which raises the
/// hub's own capture level and so does change what gets recorded from that moment on.
/// </summary>
internal sealed class LogWindowViewModel : ObservableObject
{
    private const int MaxRetained = 20000;

    private readonly LogHub _hub;
    private readonly List<LogEntry> _all = new();
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly DispatcherTimer _drain;

    private LogLevel _displayMin = LogLevel.Trace;
    private string _selectedLevel = LogLevel.Trace.ToString();
    private string _searchText = string.Empty;
    private bool _captureRaw;
    private bool _autoScroll = true;
    private bool _pause;
    private string _status = string.Empty;
    private string _detailText = string.Empty;
    private LogRow? _selectedRow;

    public LogWindowViewModel(LogHub hub)
    {
        _hub = hub;

        foreach (var source in Enum.GetValues<LogSource>())
            Sources.Add(new SourceToggle(source, Rebuild));

        ClearCommand = new RelayCommand(() => { _all.Clear(); _hub.Clear(); Rebuild(); });
        ExportCommand = new RelayCommand(Export);
        AnalyzeCommand = new RelayCommand(() => _ = AnalyzeAsync());

        foreach (var entry in _hub.Snapshot())
            _all.Add(entry);
        Rebuild();

        // The hub raises entries on its own pump thread; queue there, and move them onto the UI
        // thread in batches, so a burst of tokens cannot flood the dispatcher one entry at a time.
        _drain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _drain.Tick += (_, _) => DrainPending();
        _drain.Start();
        _hub.Entry += OnEntry;
    }

    /// <summary>Raised when rows were appended, so the view can scroll to the end if it wants to.</summary>
    public event Action? RowsAppended;

    public ObservableCollection<LogRow> Rows { get; } = new();
    public ObservableCollection<SourceToggle> Sources { get; } = new();

    public IReadOnlyList<string> Levels { get; } = Enum.GetNames<LogLevel>();

    public string SelectedLevel
    {
        get => _selectedLevel;
        set
        {
            if (!Set(ref _selectedLevel, value))
                return;
            if (Enum.TryParse<LogLevel>(value, out var level))
                _displayMin = level;
            Rebuild();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
                Rebuild();
        }
    }

    /// <summary>
    /// Raises the HUB's capture level to Trace, which is what turns on the byte-level HTTP dumps.
    /// Unlike every other control here it changes what is recorded, not just what is shown.
    /// </summary>
    public bool CaptureRaw
    {
        get => _captureRaw;
        set
        {
            if (!Set(ref _captureRaw, value))
                return;
            _hub.MinLevel = value ? LogLevel.Trace : LogLevel.Debug;
            if (value)
                SelectedLevel = LogLevel.Trace.ToString();
            Rebuild();
        }
    }

    public bool AutoScroll { get => _autoScroll; set => Set(ref _autoScroll, value); }
    public bool Pause { get => _pause; set => Set(ref _pause, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string DetailText { get => _detailText; set => Set(ref _detailText, value); }

    public LogRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value))
                DetailText = value is null ? string.Empty : Describe(value.Entry);
        }
    }

    public RelayCommand ClearCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand AnalyzeCommand { get; }

    /// <summary>
    /// How the visible log is analysed. Supplied by the window that owns the providers - the log
    /// view model knows what is on screen and nothing about models, and keeping it that way is what
    /// makes the excerpt logic testable without an Avalonia application.
    /// </summary>
    public Func<string, CancellationToken, Task<LogAnalysisResult>>? Analyse { get; set; }

    /// <summary>Raised with the finished analysis; the window opens it.</summary>
    public event Action<LogAnalysisResult>? AnalysisReady;

    private bool _isAnalyzing;

    /// <summary>True while a model is reading. The button is disabled, so one click is one request.</summary>
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set { Set(ref _isAnalyzing, value); OnPropertyChanged(nameof(CanAnalyze)); }
    }

    public bool CanAnalyze => !_isAnalyzing;

    /// <summary>Stops following the hub. The window calls this when it closes.</summary>
    public void Detach()
    {
        _hub.Entry -= OnEntry;
        _drain.Stop();
    }

    /// <summary>
    /// The correlation token in the run column: the short run id plus the plan step that produced the
    /// line, e.g. "101e6d#2". Typing that into the search box is how you isolate one branch of a
    /// parallel run, so the filter matches against this same string.
    /// </summary>
    public static string RunToken(LogEntry entry, int idChars = 6)
        => (entry.RunId is { } r ? r.ToString("N")[..idChars] : new string('-', idChars))
           + (entry.Step is { } step ? "#" + step : "");

    private void OnEntry(LogEntry entry) => _pending.Enqueue(entry);

    private void DrainPending()
    {
        if (_pending.IsEmpty)
            return;

        var appended = false;
        while (_pending.TryDequeue(out var entry))
        {
            _all.Add(entry);
            if (_all.Count > MaxRetained)
                _all.RemoveRange(0, _all.Count - MaxRetained);

            if (!Pause && Passes(entry))
            {
                Rows.Add(new LogRow(entry));
                appended = true;
            }
        }

        UpdateStatus();
        if (appended)
            RowsAppended?.Invoke();
    }

    private void Rebuild()
    {
        Rows.Clear();
        foreach (var entry in _all)
            if (Passes(entry))
                Rows.Add(new LogRow(entry));
        UpdateStatus();
        RowsAppended?.Invoke();
    }

    private bool Passes(LogEntry entry)
    {
        if (entry.Level < _displayMin)
            return false;
        if (Sources.FirstOrDefault(s => s.Source == entry.Source) is { IsEnabled: false })
            return false;
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var hay = $"{entry.Message} {entry.Category} {entry.Detail} {RunToken(entry)}";
        return hay.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateStatus()
        => Status = $"{Rows.Count} shown · {_all.Count} captured · hub level ≥ {_hub.MinLevel}";

    private static string Describe(LogEntry entry)
    {
        var sb = new StringBuilder();
        var run = entry.RunId is null ? "(none)" : RunToken(entry, 8);
        sb.Append(entry.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append("  ").Append(entry.Level)
          .Append("  ").Append(entry.Source)
          .Append("  run=").Append(run);
        if (!string.IsNullOrEmpty(entry.Category))
            sb.Append("  [").Append(entry.Category).Append(']');
        sb.AppendLine().AppendLine(entry.Message);
        if (!string.IsNullOrEmpty(entry.Detail))
            sb.AppendLine(new string('-', 60)).AppendLine(entry.Detail);
        return sb.ToString();
    }

    private void Export()
    {
        try
        {
            var dir = FileLogSink.DefaultDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"export-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, Render());
            Status = $"Exported {Rows.Count} rows → {path}";
        }
        catch (Exception ex)
        {
            Status = "Export failed: " + ex.Message;
        }
    }

    /// <summary>
    /// The visible log as text, exactly as Export writes it.
    ///
    /// <para>One renderer for both on purpose: what the analysis is given and what the exported file
    /// contains have to be the same thing, or a person comparing an answer against the file they
    /// were sent is comparing it against something else.</para>
    ///
    /// <para>The live list is in ARRIVAL order - domain events reach the log through the run's event
    /// channel and the UI, so they land after wire lines they actually preceded. That is fine while
    /// tailing, but read later as a timeline it is wrong, so it is sorted by time (Seq breaks ties,
    /// being monotonic in the hub).</para>
    /// </summary>
    private string Render()
    {
        var text = new StringBuilder();
        foreach (var row in Rows.OrderBy(r => r.Entry.At).ThenBy(r => r.Entry.Seq))
        {
            text.AppendLine(row.Line);
            if (!string.IsNullOrEmpty(row.Entry.Detail))
                foreach (var line in row.Entry.Detail.Replace("\r\n", "\n").Split('\n'))
                    text.Append("    | ").AppendLine(line);
        }
        return text.ToString();
    }

    /// <summary>
    /// Hands the visible log to a model and asks what went wrong with it.
    ///
    /// <para>The FILTERED log, not the whole buffer: the level, the sources and the search box are
    /// how a person narrows down what they are looking at, and an analysis of something other than
    /// what is on screen would answer a question nobody asked. Narrowing first is also the way to
    /// analyse a run that does not fit whole.</para>
    /// </summary>
    private async Task AnalyzeAsync()
    {
        if (Analyse is null)
        {
            Status = "No model is configured to read the log. Set one under Settings · AI.";
            return;
        }

        if (IsAnalyzing)
            return;

        var text = Render();
        if (string.IsNullOrWhiteSpace(text))
        {
            Status = "There is nothing on screen to analyse.";
            return;
        }

        IsAnalyzing = true;
        Status = $"Reading {Rows.Count:N0} rows…";
        try
        {
            AnalysisReady?.Invoke(await Analyse(text, CancellationToken.None));
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            // Said plainly and left on screen. A failed analysis that clears itself looks exactly
            // like an analysis that found nothing.
            Status = "The analysis did not run: " + ex.Message;
        }
        finally
        {
            IsAnalyzing = false;
        }
    }
}
