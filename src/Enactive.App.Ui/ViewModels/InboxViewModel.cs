namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Media;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.History;
using Enactive.Core.Inbox;

/// <summary>One item in the inbox list.</summary>
internal sealed class InboxItemViewModel : ObservableObject
{
    private bool _isUnread;

    public InboxItemViewModel(InboxItem item)
    {
        Item = item;
        _isUnread = string.Equals(item.Status, "unread", StringComparison.OrdinalIgnoreCase);
    }

    public InboxItem Item { get; }

    public string Title => Item.Title;
    public string Summary => Item.Summary;
    public string Kind => Item.Kind;
    public string When => Item.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>What the item is: a finished result, a decision nobody was there to answer, an error.</summary>
    public IBrush KindBrush => Item.Kind.ToLowerInvariant() switch
    {
        "result" => Brand.Success,
        "decision" => Brand.Amber,
        "error" => Brand.Danger,
        _ => Brand.TextMuted
    };

    public bool IsUnread
    {
        get => _isUnread;
        set
        {
            if (Set(ref _isUnread, value))
                OnPropertyChanged(nameof(TitleWeight));
        }
    }

    /// <summary>Unread items carry their weight, the way an unread mail does.</summary>
    public FontWeight TitleWeight => IsUnread ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>One line of the run's timeline.</summary>
internal sealed class RunEventViewModel
{
    public RunEventViewModel(DateTimeOffset at, string kind, string summary)
    {
        When = at.ToLocalTime().ToString("HH:mm:ss");
        Kind = kind;
        Summary = summary;
        Brush = kind switch
        {
            "ErrorObserved" or "TaskFailed" => Brand.Danger,
            "ReviewFailed" => Brand.Warning,
            "ReviewPassed" or "TaskCompleted" => Brand.Success,
            "ToolInvoked" or "ToolResult" => Brand.TextMuted,
            "assistant" => Brand.TextMuted,
            _ => Brand.TextBody
        };
    }

    public string When { get; }
    public string Kind { get; }
    public string Summary { get; }
    public IBrush Brush { get; }
}

/// <summary>
/// The AI Inbox: what headless runs reported back while nobody was watching. Selecting an item opens
/// the run behind it - its timeline, artifacts and decisions - which is the whole reason this is a
/// window and not the block of text it used to be, and marks the item read on the way.
/// </summary>
internal sealed class InboxViewModel : ObservableObject
{
    private readonly IInboxStore _inbox;
    private readonly IRunStore _runs;
    private readonly List<InboxItemViewModel> _all = new();
    /// <summary>The headers of every run in the workspace: enough to know whether the run behind an
    /// item is still there, and to name it. The transcript is read when an item is opened.</summary>
    private IReadOnlyList<RunSummary> _runSummaries = Array.Empty<RunSummary>();

    private InboxItemViewModel? _selected;
    private bool _unreadOnly;
    private string _status = string.Empty;
    private string _detailTitle = string.Empty;
    private string _detailMeta = string.Empty;
    private string _detailSummary = string.Empty;
    private string _runHeader = string.Empty;
    private bool _hasSelection;
    private bool _hasRun;

    public InboxViewModel(IInboxStore inbox, IRunStore runs, string workspaceRoot)
    {
        _inbox = inbox;
        _runs = runs;
        WorkspaceRoot = workspaceRoot;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        MarkAllReadCommand = new AsyncRelayCommand(MarkAllReadAsync);
    }

    /// <summary>Raised whenever the unread count changes, so the caller can update its own badge.</summary>
    public event Action<int>? UnreadChanged;

    public string WorkspaceRoot { get; }

    public ObservableCollection<InboxItemViewModel> Items { get; } = new();
    public ObservableCollection<RunEventViewModel> RunEvents { get; } = new();
    public ObservableCollection<string> RunArtifacts { get; } = new();
    public ObservableCollection<string> RunDecisions { get; } = new();

    public InboxItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            ShowDetail(value);
            if (value is { IsUnread: true })
                _ = MarkReadAsync(value);
        }
    }

    public bool UnreadOnly
    {
        get => _unreadOnly;
        set
        {
            if (Set(ref _unreadOnly, value))
                ApplyFilter();
        }
    }

    public string Status { get => _status; set => Set(ref _status, value); }
    public bool HasSelection { get => _hasSelection; set => Set(ref _hasSelection, value); }
    public string DetailTitle { get => _detailTitle; set => Set(ref _detailTitle, value); }
    public string DetailMeta { get => _detailMeta; set => Set(ref _detailMeta, value); }
    public string DetailSummary { get => _detailSummary; set => Set(ref _detailSummary, value); }
    public string RunHeader { get => _runHeader; set => Set(ref _runHeader, value); }

    /// <summary>False when the run behind the item is no longer in the store - an old item whose run
    /// history was cleared still shows its own summary, just without a timeline.</summary>
    public bool HasRun { get => _hasRun; set => Set(ref _hasRun, value); }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand MarkAllReadCommand { get; }

    public async Task RefreshAsync()
    {
        var keepId = Selected?.Item.Id;

        var items = await _inbox.LoadAllAsync(CancellationToken.None);
        try { _runSummaries = await _runs.LoadSummariesAsync(CancellationToken.None); }
        catch { _runSummaries = Array.Empty<RunSummary>(); }

        _all.Clear();
        foreach (var item in items.OrderByDescending(i => i.At))
            _all.Add(new InboxItemViewModel(item));

        ApplyFilter();

        // Keep the user where they were if that item is still listed.
        if (keepId is { } id)
            Selected = Items.FirstOrDefault(i => i.Item.Id == id);
    }

    private async Task MarkAllReadAsync()
    {
        await _inbox.MarkAllReadAsync(CancellationToken.None);
        foreach (var item in _all)
            item.IsUnread = false;
        ApplyFilter();
    }

    private async Task MarkReadAsync(InboxItemViewModel item)
    {
        item.IsUnread = false;
        UpdateStatus();
        await _inbox.MarkReadAsync(item.Item.Id, CancellationToken.None);
    }

    private void ApplyFilter()
    {
        // Rebuilt rather than filtered in place: the list is small, and this keeps "unread only"
        // from having to reason about where an item that just became read used to sit.
        var keep = Selected;
        Items.Clear();
        foreach (var item in _all)
            if (!UnreadOnly || item.IsUnread)
                Items.Add(item);

        if (keep is not null && Items.Contains(keep))
            Selected = keep;

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var unread = _all.Count(i => i.IsUnread);
        Status = _all.Count == 0
            ? "Inbox is empty"
            : $"{unread} unread · {_all.Count} item{(_all.Count == 1 ? "" : "s")}";
        UnreadChanged?.Invoke(unread);
    }

    private void ShowDetail(InboxItemViewModel? item)
    {
        RunEvents.Clear();
        RunArtifacts.Clear();
        RunDecisions.Clear();

        HasSelection = item is not null;
        if (item is null)
        {
            DetailTitle = DetailMeta = DetailSummary = RunHeader = string.Empty;
            HasRun = false;
            return;
        }

        DetailTitle = item.Title;
        DetailMeta = $"{item.Kind} · {item.When}";
        DetailSummary = item.Summary;

        var summary = _runSummaries.FirstOrDefault(r => r.RunId == item.Item.RunId);
        HasRun = summary is not null;
        if (summary is null)
        {
            RunHeader = "The run behind this item is no longer in the store.";
            return;
        }

        // The header comes off the summary, so it appears at once; the timeline needs the events and
        // those are fetched for THIS run alone. The list used to hold every run whole so that this
        // line could be drawn without waiting.
        var elapsed = summary.FinishedAt - summary.StartedAt;
        RunHeader = $"{summary.Title} — {summary.Status} · {summary.Model} · {elapsed.TotalSeconds:0}s";
        foreach (var a in summary.Artifacts)
            RunArtifacts.Add(a);
        foreach (var d in summary.Decisions)
            RunDecisions.Add(d);

        _ = LoadTimelineAsync(item);
    }

    /// <summary>
    /// Reads one run's events and fills the timeline.
    ///
    /// <para>Guarded by the selection: an earlier item's load can land after a later one's, and
    /// filling the panel with the run somebody has already clicked away from is worse than filling
    /// it late. If the selection has moved on, the answer is dropped.</para>
    /// </summary>
    private async Task LoadTimelineAsync(InboxItemViewModel item)
    {
        RunRecord? run;
        try { run = await _runs.LoadAsync(item.Item.RunId, CancellationToken.None); }
        catch { return; }

        if (!ReferenceEquals(Selected, item) || run is null)
            return;

        AddTimeline(run);
    }

    /// <summary>Renders the run's events. The folding of the streamed reply lives in
    /// <see cref="RunTimeline"/>, because every view that replays a run needs it.</summary>
    private void AddTimeline(RunRecord run)
    {
        foreach (var row in RunTimeline.Fold(run))
            RunEvents.Add(row);
    }
}
