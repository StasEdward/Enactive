namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Enactive.Application.Projections;
using Enactive.Application;
using Enactive.Application.Mvvm;
using Enactive.Core.History;
using Enactive.Core.Inbox;

// The list's row (InboxItemViewModel) and the inbox's words (InboxRows) are Enactive.Application's: the service draws the
// same inbox.

/// <summary>One answer a waiting question offers, as a button, labelled as InboxRows.Options says it.</summary>
internal sealed class WaitingAnswerViewModel(InboxOption option, Func<string, Task> answer)
{
    public string Id { get; } = option.Id;
    public string Label { get; } = option.Label;
    public AsyncRelayCommand Command { get; } = new(() => answer(option.Id));
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

    // The questions background runs stopped at, and who carries a run on once one is answered.
    private readonly InboxDecisions _decisions;
    private Enactive.Agents.ParkedDecision? _waiting;
    private string _decisionStatus = string.Empty;

    public InboxViewModel(IInboxStore inbox, IRunStore runs, string workspaceRoot,
        Func<Enactive.Agents.ParkedDecision, InboxItem, Task>? carryOn = null)
    {
        _inbox = inbox;
        _runs = runs;
        WorkspaceRoot = workspaceRoot;
        _decisions = new InboxDecisions(workspaceRoot, carryOn);

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

    // ── a question the run behind the item stopped at ──

    /// <summary>True while the selected item's run is stopped at a question nobody has answered yet.</summary>
    public bool HasWaitingDecision => _waiting is not null;
    public string DecisionTopic => _waiting?.Topic ?? string.Empty;

    /// <summary>
    /// Everything the answer authorises, in full - not the one-line summary. A person cannot consent
    /// to what they were not shown (see DecisionRequest.FullText).
    /// </summary>
    public string DecisionText => _waiting?.FullText ?? string.Empty;
    public string DecisionAsked => _waiting is null ? string.Empty : InboxRows.Asked(_waiting);
    public ObservableCollection<WaitingAnswerViewModel> DecisionOptions { get; } = new();

    /// <summary>What happened to the answer: sent on, or refused because the question is no longer waiting.</summary>
    public string DecisionStatus { get => _decisionStatus; set => Set(ref _decisionStatus, value); }

    private void ShowWaiting(Enactive.Agents.ParkedDecision? waiting)
    {
        _waiting = waiting;
        DecisionOptions.Clear();
        foreach (var option in waiting is null ? Array.Empty<InboxOption>() : InboxRows.Options(waiting))
            DecisionOptions.Add(new WaitingAnswerViewModel(option, AnswerAsync));
        OnPropertyChanged(nameof(HasWaitingDecision));
        OnPropertyChanged(nameof(DecisionTopic));
        OnPropertyChanged(nameof(DecisionText));
        OnPropertyChanged(nameof(DecisionAsked));
    }

    /// <summary>Answers the question shown, once; see <see cref="InboxDecisions.AnswerAsync"/>.</summary>
    private async Task AnswerAsync(string optionId)
    {
        if (_waiting is not { } waiting || Selected is not { } item) return;
        ShowWaiting(null);   // one answer: the buttons go before anything can be clicked twice
        (DecisionStatus, _) = await _decisions.AnswerAsync(waiting, item.Item, optionId);
    }

    public async Task RefreshAsync()
    {
        var keepId = Selected?.Item.Id;

        var items = await _inbox.LoadAllAsync(CancellationToken.None);
        try { _runSummaries = await _runs.LoadSummariesAsync(CancellationToken.None); }
        catch { _runSummaries = Array.Empty<RunSummary>(); }

        _all.Clear();
        _all.AddRange(InboxRows.Rows(items));

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
        // Rebuilt rather than filtered in place (InboxRows.Shown).
        var keep = Selected;
        Items.Clear();
        foreach (var item in InboxRows.Shown(_all, UnreadOnly))
            Items.Add(item);

        if (keep is not null && Items.Contains(keep))
            Selected = keep;

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var unread = _all.Count(i => i.IsUnread);
        Status = InboxRows.Status(unread, _all.Count);
        UnreadChanged?.Invoke(unread);
    }

    private void ShowDetail(InboxItemViewModel? item)
    {
        RunEvents.Clear();
        RunArtifacts.Clear();
        RunDecisions.Clear();

        HasSelection = item is not null;
        DecisionStatus = string.Empty;
        ShowWaiting(null);
        if (item is null)
        {
            DetailTitle = DetailMeta = DetailSummary = RunHeader = string.Empty;
            HasRun = false;
            return;
        }

        DetailTitle = item.Title;
        DetailMeta = InboxRows.Meta(item);
        DetailSummary = item.Summary;

        var summary = _runSummaries.FirstOrDefault(r => r.RunId == item.Item.RunId);
        HasRun = summary is not null;
        RunHeader = InboxRows.RunHeader(summary);
        if (summary is null)
            return;

        // The header comes off the summary, so it appears at once; the timeline needs the events and
        // those are fetched for THIS run alone. The list used to hold every run whole so that this
        // line could be drawn without waiting.
        // The question this item's run stopped at, if it is still waiting. By the run's TASK: the
        // answer is to the task, and a run carried on is a new run under the same one.
        ShowWaiting(_decisions.WaitingFor(summary.TaskId));

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
