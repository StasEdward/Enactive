namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Media;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.History;

/// <summary>One finished run, as a row in the context column.</summary>
internal sealed class RunListItemViewModel
{
    public RunListItemViewModel(RunRecord record)
    {
        Record = record;

        Title = string.IsNullOrWhiteSpace(record.Title) ? "(untitled run)" : record.Title;

        var elapsed = record.FinishedAt - record.StartedAt;
        var seconds = elapsed.TotalSeconds;
        var duration = seconds >= 60 ? $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s" : $"{seconds:0}s";

        // The date is only worth the width when it is not today's.
        var started = record.StartedAt.ToLocalTime();
        var when = started.Date == DateTime.Today ? started.ToString("HH:mm") : started.ToString("MMM d HH:mm");
        Meta = $"{when} · {duration}";

        StatusBrush = record.Status.ToLowerInvariant() switch
        {
            "completed" or "succeeded" or "ok" => Brand.Success,
            "failed" or "error" => Brand.Danger,
            "cancelled" or "canceled" => Brand.TextMuted,
            _ => Brand.Amber
        };
    }

    public RunRecord Record { get; }
    public string Title { get; }
    public string Meta { get; }
    public IBrush StatusBrush { get; }
}

/// <summary>
/// A finished run, opened read-only: its timeline, what it wrote, and what it asked a person. There
/// is nothing to click here on purpose - a past run is history, and history is not re-run by
/// clicking Apply on a diff that was applied last Tuesday.
/// </summary>
internal sealed class PastRunViewModel
{
    public PastRunViewModel(RunRecord record)
    {
        Record = record;
        Title = string.IsNullOrWhiteSpace(record.Title) ? "(untitled run)" : record.Title;
        Status = record.Status;

        var elapsed = record.FinishedAt - record.StartedAt;
        var model = string.IsNullOrWhiteSpace(record.Model) ? "unknown model" : record.Model;
        Meta = $"{record.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {model} · {elapsed.TotalSeconds:0}s · run {record.RunId:N}";

        StatusBrush = record.Status.ToLowerInvariant() switch
        {
            "completed" or "succeeded" or "ok" => Brand.Success,
            "failed" or "error" => Brand.Danger,
            "cancelled" or "canceled" => Brand.TextMuted,
            _ => Brand.Amber
        };

        foreach (var row in RunTimeline.Fold(record))
            Events.Add(row);
        foreach (var a in record.Artifacts)
            Artifacts.Add(a);
        foreach (var d in record.Decisions)
            Decisions.Add(d);
    }

    public RunRecord Record { get; }
    public string Title { get; }
    public string Meta { get; }
    public string Status { get; }
    public IBrush StatusBrush { get; }

    public ObservableCollection<RunEventViewModel> Events { get; } = new();
    public ObservableCollection<string> Artifacts { get; } = new();
    public ObservableCollection<string> Decisions { get; } = new();

    public bool HasArtifacts => Artifacts.Count > 0;
    public bool HasDecisions => Decisions.Count > 0;
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
