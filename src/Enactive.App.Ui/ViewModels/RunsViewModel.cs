namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Media;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.Events;
using Enactive.Core.History;

/// <summary>
/// One finished run, as a CARD in the context column: a coloured edge for how it ended, the title
/// the planner gave it, and one line saying when and what came of it. The edge is part of the card,
/// not part of being selected - you have to be able to spot the failed run without clicking it.
/// </summary>
internal sealed class RunListItemViewModel
{
    public RunListItemViewModel(RunRecord record)
    {
        Record = record;

        Title = string.IsNullOrWhiteSpace(record.Title) ? "(untitled run)" : record.Title;
        Meta = $"{Ago(record.StartedAt)} · {Outcome(record)}";
        StatusBrush = BrushFor(record.Status);

        var elapsed = record.FinishedAt - record.StartedAt;
        Tooltip = $"{Title}\n{record.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {record.Status} · {Duration(elapsed)}";
    }

    public RunRecord Record { get; }
    public string Title { get; }
    public string Meta { get; }
    public string Tooltip { get; }
    public IBrush StatusBrush { get; }

    internal static IBrush BrushFor(string status) => status.ToLowerInvariant() switch
    {
        "completed" or "succeeded" or "ok" => Brand.Success,
        "failed" or "error" => Brand.Danger,
        "cancelled" or "canceled" => Brand.TextMuted,
        // Anything else never wrote a final status - blocked, or the app died mid-run. That is a
        // person's problem to look at, which is what amber means everywhere else here.
        _ => Brand.Amber
    };

    /// <summary>
    /// How long ago, the way a person would say it. Days are counted on the CALENDAR, not in
    /// 24-hour blocks: a run at 23:50 was "yesterday" by breakfast, not "9 h ago".
    /// </summary>
    private static string Ago(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var days = (DateTime.Today - local.Date).Days;

        if (days == 1)
            return "yesterday";
        if (days > 1 && days < 7)
            return $"{days} days ago";
        if (days >= 7)
            return local.ToString("MMM d");

        var span = DateTimeOffset.Now - at;
        if (span.TotalMinutes < 1)
            return "just now";
        if (span.TotalMinutes < 60)
            return $"{(int)span.TotalMinutes} min ago";
        return $"{(int)span.TotalHours} h ago";
    }

    /// <summary>
    /// What came of it, in two or three words. A failure says so; otherwise what it produced is
    /// more use than how long it took, and the duration is in the run's own header anyway.
    /// </summary>
    private static string Outcome(RunRecord record)
    {
        switch (record.Status.ToLowerInvariant())
        {
            case "failed":
            case "error":
                return "failed";
            case "cancelled":
            case "canceled":
                return "cancelled";
        }

        if (record.Artifacts.Count > 0)
            return $"{record.Artifacts.Count} artifact{(record.Artifacts.Count == 1 ? "" : "s")}";

        return Duration(record.FinishedAt - record.StartedAt);
    }

    private static string Duration(TimeSpan span)
    {
        var seconds = span.TotalSeconds;
        if (seconds < 0)
            seconds = 0;
        return seconds >= 60 ? $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s" : $"{seconds:0}s";
    }
}

/// <summary>
/// A finished run, opened read-only - in the SAME frame as a live one: a header with what it was
/// called and how it ended, tabs over the run, and the status column beside them. What changes is
/// the content, not the shape, because it is the same thing at a different time.
///
/// The first tab is the timeline rather than step cards. A stored event is (when, kind, summary):
/// the step number the live view sorts cards by is not recorded, and guessing it from the order
/// would put a tool call under the wrong step the moment two steps ran at once - which is a bug
/// this app has already had once.
///
/// There is nothing to click on purpose. This run finished, and history is not re-run by pressing
/// Apply on a diff from last Tuesday.
/// </summary>
internal sealed class PastRunViewModel : ObservableObject
{
    private int _selectedTab;

    public PastRunViewModel(RunRecord record)
    {
        Record = record;
        Title = string.IsNullOrWhiteSpace(record.Title) ? "(untitled run)" : record.Title;
        Status = record.Status;
        StatusBrush = RunListItemViewModel.BrushFor(record.Status);

        var elapsed = record.FinishedAt - record.StartedAt;
        var model = string.IsNullOrWhiteSpace(record.Model) ? "unknown model" : record.Model;
        Meta = $"{record.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {model} · run {record.RunId:N}";

        foreach (var card in RunReplay.Steps(record))
            Steps.Add(card);
        foreach (var row in RunTimeline.Fold(record))
            Events.Add(row);
        foreach (var a in record.Artifacts)
            Artifacts.Add(a);
        foreach (var d in record.Decisions)
            Decisions.Add(d);

        // The same three tiles the live run shows, off what was actually recorded.
        ArtifactCountText = Artifacts.Count.ToString();
        ToolCallsText = record.Events.Count(e => e.Kind == nameof(EventKind.ToolInvoked)).ToString();
        ElapsedText = Duration(elapsed);

        var done = Steps.Count(c => c.StatusWord is "done" or "skipped");
        StepsText = Steps.Count == 0 ? "—" : $"{done} / {Steps.Count} steps";

        ShowExecutionCommand = new RelayCommand(() => SelectedTab = 0);
        ShowArtifactsCommand = new RelayCommand(() => SelectedTab = 1);
        ShowTimelineCommand = new RelayCommand(() => SelectedTab = 2);

        // A run recorded before step numbers existed rebuilds no cards, so Execution would be an
        // empty tab claiming the run did nothing. It opens on the timeline instead, which is what
        // that record actually holds.
        if (Steps.Count == 0)
            SelectedTab = 2;
    }

    public RunRecord Record { get; }
    public string Title { get; }
    public string Meta { get; }
    public string Status { get; }
    public IBrush StatusBrush { get; }

    /// <summary>The plan's steps, rebuilt from the record. Empty for a run stored before the step
    /// number was, in which case the timeline is the whole story.</summary>
    public ObservableCollection<StepCardViewModel> Steps { get; } = new();

    public bool HasSteps => Steps.Count > 0;
    public bool HasNoSteps => Steps.Count == 0;
    public string StepsText { get; }

    public ObservableCollection<RunEventViewModel> Events { get; } = new();
    public ObservableCollection<string> Artifacts { get; } = new();
    public ObservableCollection<string> Decisions { get; } = new();

    public string ArtifactCountText { get; }
    public string ToolCallsText { get; }
    public string ElapsedText { get; }

    /// <summary>A tab carries its own count, so a run that produced nothing does not advertise "(0)".</summary>
    public string ArtifactsTabLabel => Artifacts.Count == 0 ? "Artifacts" : $"Artifacts ({Artifacts.Count})";

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value))
                return;
            OnPropertyChanged(nameof(IsExecutionTab));
            OnPropertyChanged(nameof(IsArtifactsTab));
            OnPropertyChanged(nameof(IsTimelineTab));
        }
    }

    public bool IsExecutionTab => _selectedTab == 0;
    public bool IsArtifactsTab => _selectedTab == 1;
    public bool IsTimelineTab => _selectedTab == 2;

    public RelayCommand ShowExecutionCommand { get; }
    public RelayCommand ShowArtifactsCommand { get; }
    public RelayCommand ShowTimelineCommand { get; }

    /// <summary>Empty is a fact worth stating, not a blank panel.</summary>
    public bool HasArtifacts => Artifacts.Count > 0;
    public bool HasNoArtifacts => Artifacts.Count == 0;
    public bool HasDecisions => Decisions.Count > 0;
    public bool HasNoDecisions => Decisions.Count == 0;

    private static string Duration(TimeSpan span)
    {
        var seconds = span.TotalSeconds;
        if (seconds < 0)
            seconds = 0;
        return seconds >= 60 ? $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s" : $"{seconds:0}s";
    }
}

/// <summary>
/// The workspace's run history in the context column. It does not own a store: which store a
/// workspace has is the window's business (SQLite, MySQL or files, per the environment), so this
/// asks for a refresh and is handed the records.
/// </summary>
internal sealed class RunsViewModel : ObservableObject
{
    private RunListItemViewModel? _selected;
    private string _status = "Not loaded yet.";

    public RunsViewModel()
    {
        RefreshCommand = new RelayCommand(() => RefreshRequested?.Invoke());
    }

    /// <summary>Asks whoever owns the stores to load this workspace's runs.</summary>
    public event Action? RefreshRequested;

    /// <summary>A row was picked. The window decides what "open" means; the list only reports it.</summary>
    public event Action<RunRecord>? OpenRequested;

    public ObservableCollection<RunListItemViewModel> Items { get; } = new();

    public string Status { get => _status; set => Set(ref _status, value); }

    public RunListItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            if (value is not null)
                OpenRequested?.Invoke(value.Record);
        }
    }

    public RelayCommand RefreshCommand { get; }

    public void Show(IReadOnlyList<RunRecord> records)
    {
        // Rebuilding drops the selection, so the row the user is reading is re-selected by run id
        // rather than by position - a finished run pushes everything down by one.
        var keep = _selected?.Record.RunId;

        Items.Clear();
        foreach (var record in records.OrderByDescending(r => r.StartedAt))
            Items.Add(new RunListItemViewModel(record));

        Status = Items.Count == 0
            ? "No runs recorded in this workspace."
            : $"{Items.Count} run{(Items.Count == 1 ? "" : "s")}";

        if (keep is { } id)
            _selected = Items.FirstOrDefault(i => i.Record.RunId == id);
        else
            _selected = null;
        OnPropertyChanged(nameof(Selected));
    }

    /// <summary>The workspace changed: what is listed belongs to the old one.</summary>
    public void Reset()
    {
        Items.Clear();
        _selected = null;
        OnPropertyChanged(nameof(Selected));
        Status = "Not loaded yet.";
    }

    public void Fail(string message) => Status = "Could not read the run store: " + message;
}
