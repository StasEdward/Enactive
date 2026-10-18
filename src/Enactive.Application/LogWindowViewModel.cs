namespace Enactive.Application;

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Enactive.Application.Mvvm;
using Enactive.Agents;
using Enactive.Core;
using Enactive.Core.Diagnostics;
using Enactive.Workspace;

/// <summary>One rendered log line: the single-line summary, its level colour, and the entry behind it.</summary>
internal sealed class LogRow
{
    public LogRow(LogEntry entry)
    {
        Entry = entry;
        // The line as every host writes it (LogText) - and as the analysis and the export read it.
        Line = LogText.Line(entry);
    }

    public LogEntry Entry { get; }
    public string Line { get; }
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
/// What the log view changes beyond what it shows: the hub every line goes to, which is not the view's own.
///
/// <para>A seam because the two hosts own that hub differently. The window's is the person's at the machine, and its
/// Clear and its raw-wire switch act on it directly (<see cref="HubChanges"/>). The service's hub is everybody's: a
/// person's Clear emptying it would have emptied every other open log page, and the raw-wire switch changes what is
/// recorded for everybody, so the service decides who may (an Admin) and puts it in the audit trail.</para>
/// </summary>
internal interface ILogHubChanges
{
    /// <summary>Whether the hub captures the raw wire (Trace) now - what the switch shows when the view opens.</summary>
    bool Capturing { get; }

    /// <summary>Turns raw-wire capture on or off. Null when done; otherwise why not, to show the person.</summary>
    string? Capture(bool trace);

    /// <summary>The view was cleared: what, if anything, goes with it beyond the view.</summary>
    void Cleared();
}

/// <summary>The window's: the hub is the person's own, so Clear empties its ring and the switch sets its level.</summary>
internal sealed class HubChanges(LogHub hub) : ILogHubChanges
{
    public bool Capturing => hub.MinLevel <= LogLevel.Trace;

    public string? Capture(bool trace)
    {
        hub.MinLevel = trace ? LogLevel.Trace : LogLevel.Debug;
        return null;
    }

    public void Cleared() => hub.Clear();
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
    private readonly ILogHubChanges _changes;
    private readonly List<LogRow> _all = new();
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly IDisposable _drain;
    private CancellationTokenSource? _filterCancellation;
    private bool _detached;
    private int _revision;
    private bool _filtering;

    private LogLevel _displayMin = LogLevel.Trace;
    private string _selectedLevel = LogLevel.Trace.ToString();
    private string _searchText = string.Empty;
    private bool _captureRaw;
    private bool _autoScroll = true;
    private bool _pause;
    private string _status = string.Empty;
    private string _detailText = string.Empty;
    private LogRow? _selectedRow;

    /// <param name="drain">
    /// Starts calling what it is given at the host's own pace, on the thread this view model lives on, until the returned
    /// handle is disposed. The hub raises entries on its own pump thread; they are queued there and moved onto the view's
    /// thread in batches, so a burst of tokens cannot flood it one entry at a time. The host owns the clock because the
    /// thread is the host's: the window's dispatcher timer, a page's circuit.
    /// </param>
    /// <param name="changes">Where Clear and the raw-wire switch reach the hub; null, the hub itself (the window's).</param>
    public LogWindowViewModel(LogHub hub, Func<Action, IDisposable> drain, ILogHubChanges? changes = null)
    {
        _hub = hub;
        _changes = changes ?? new HubChanges(hub);
        // What the switch shows is what the hub does: a view opened while capture is on says so, rather than offering to
        // turn on what is already on.
        _captureRaw = _changes.Capturing;

        foreach (var source in Enum.GetValues<LogSource>())
            Sources.Add(new SourceToggle(source, Rebuild));

        ClearCommand = new RelayCommand(() => { _all.Clear(); _pending.Clear(); _revision++; _changes.Cleared(); Rebuild(); });
        ExportCommand = new RelayCommand(() => _ = ExportAsync());
        AnalyzeCommand = new RelayCommand(() => _ = AnalyzeAsync());

        // Subscribed BEFORE the backlog is read, and the backlog's last Seq kept: the hub stores a line at once but hands it
        // to subscribers later, from its pump. Read first and subscribed after, a line logged just before the view opened
        // was in the backlog AND came again through the pump - shown twice (the service's log page on a slow CI runner,
        // PR #58); subscribed after without the Seq, a line between the two was lost instead.
        _hub.Entry += OnEntry;
        foreach (var entry in _hub.Snapshot().TakeLast(MaxRetained))
        {
            _all.Add(new LogRow(entry));
            _backlogUpTo = Math.Max(_backlogUpTo, entry.Seq);
        }
        Rebuild();

        _drain = drain(DrainPending);
    }

    /// <summary>The last Seq the backlog held: a line the pump hands on that is not past it is already shown.</summary>
    private long _backlogUpTo;

    /// <summary>Raised when rows were appended, so the view can scroll to the end if it wants to.</summary>
    public event Action? RowsAppended;

    public BatchObservableCollection<LogRow> Rows { get; } = new();
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
            if (_captureRaw == value)
                return;
            if (_changes.Capture(value) is { } refused)
            {
                // Said, and the switch stays where the hub is: a switch that moved over a refusal says capture is on
                // while nothing is captured.
                Status = refused;
                OnPropertyChanged();
                return;
            }
            Set(ref _captureRaw, value);
            if (value)
                SelectedLevel = LogLevel.Trace.ToString();
            Rebuild();
        }
    }

    public bool AutoScroll { get => _autoScroll; set => Set(ref _autoScroll, value); }
    public bool Pause { get => _pause; set { if (Set(ref _pause, value) && !value) Rebuild(); } }
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

    /// <summary>
    /// Where Export hands the visible log: a file's name and its text. Null, a file in the log folder of this machine -
    /// the window's, whose reader sits at it. A host whose reader is elsewhere (a browser) hands it on itself: written into
    /// the service's own log folder, it was a file on a machine the person reading may never see.
    /// </summary>
    public Func<string, string, Task>? ExportTo { get; set; }

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
        _detached = true;
        _filterCancellation?.Cancel();
        _hub.Entry -= OnEntry;
        _drain.Dispose();
    }

    /// <summary>
    /// The correlation token in the run column: the short run id plus the plan step that produced the
    /// line, e.g. "101e6d#2". Typing that into the search box is how you isolate one branch of a
    /// parallel run, so the filter matches against this same string.
    /// </summary>
    public static string RunToken(LogEntry entry, int idChars = 6)
        => LogFilter.RunToken(entry, idChars);

    private void OnEntry(LogEntry entry) => _pending.Enqueue(entry);

    private void DrainPending()
    {
        if (_pending.IsEmpty) return;
        var added = new List<LogRow>();
        // Bound each dispatcher tick; a busy producer must not monopolise the UI.
        while (added.Count < 1000 && _pending.TryDequeue(out var entry))
            if (entry.Seq > _backlogUpTo)
                added.Add(new LogRow(entry));
        if (added.Count == 0) return;
        _all.AddRange(added);
        var trimmed = _all.Count > MaxRetained;
        if (trimmed) _all.RemoveRange(0, _all.Count - MaxRetained);
        _revision++;
        if (!_filtering && !Pause)
        {
            var sources = Sources.Where(s => s.IsEnabled).Select(s => s.Source).ToHashSet();
            Rows.AppendTail(added.Where(r => LogFilter.Matches(r.Entry, _displayMin, sources, SearchText)), MaxRetained);
            if (trimmed)
            {
                var retained = _all.ToHashSet();
                Rows.ReplaceWith(Rows.Where(retained.Contains));
            }
            RowsAppended?.Invoke();
        }
        UpdateStatus();
    }

    private void Rebuild() => _ = RebuildAsync();

    private async Task RebuildAsync(bool debounce = true)
    {
        _filterCancellation?.Cancel();
        var cancellation = _filterCancellation = new CancellationTokenSource();
        _filtering = true;
        try
        {
            if (debounce) await Task.Delay(200, cancellation.Token);
            // Publish a completed snapshot even during continuous logging; never starve the view.
            var revision = _revision;
            var snapshot = _all.ToArray();
            var sources = Sources.Where(s => s.IsEnabled).Select(s => s.Source).ToHashSet();
            var minimum = _displayMin;
            var search = SearchText;
            var rows = await Task.Run(() =>
            {
                var result = new List<LogRow>();
                foreach (var row in snapshot)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (LogFilter.Matches(row.Entry, minimum, sources, search)) result.Add(row);
                }
                return result;
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_detached) return;
            var retained = _all.ToHashSet();
            Rows.ReplaceWith(rows.Where(retained.Contains));
            if (SelectedRow is { } selected && !Rows.Contains(selected)) SelectedRow = null;
            UpdateStatus();
            RowsAppended?.Invoke();
            if (revision != _revision) _ = RebuildAsync(debounce: false);
            return;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_filterCancellation, cancellation))
            {
                _filterCancellation = null;
                _filtering = false;
            }
            cancellation.Dispose();
        }
    }

    private void UpdateStatus()
        => Status = $"{Rows.Count} shown · {_all.Count} captured · hub level ≥ {_hub.MinLevel}";

    private static string Describe(LogEntry entry)
    {
        var sb = new StringBuilder();
        var run = entry.RunId is null ? "(none)" : RunToken(entry, 8);
        // Invariant: under a culture whose time separator is not a colon the same instant read differently per machine.
        sb.Append(entry.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
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

    /// <summary>
    /// The name Export gives the file: when it was made, in the same digits on every machine - a regional calendar wrote
    /// another year into it.
    /// </summary>
    public static string ExportName(DateTime now)
        => $"export-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log";

    private async Task ExportAsync()
    {
        try
        {
            var name = ExportName(DateTime.Now);
            if (ExportTo is { } handOn)
            {
                await handOn(name, Render());
                Status = $"Exported {Rows.Count} rows → {name}";
                return;
            }
            var dir = FileLogSink.DefaultDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
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
    public string Render()
    {
        return LogText.Render(Rows.Select(r => r.Entry));
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
        Status = $"Reading {Invariant.N0(Rows.Count)} rows…";
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
