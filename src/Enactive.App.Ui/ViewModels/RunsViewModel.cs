namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Media;
using Enactive.Core.Artifacts;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Templates;

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

    /// <summary>Which attempt of its task this is, or empty when it is the only one.</summary>
    public string AttemptLabel { get; } = string.Empty;
    public bool HasAttempts { get; }

    /// <summary>The specification this run was started from, when it came from a template.</summary>
    public ResolvedTaskSpec? Spec { get; }

    /// <summary>What was originally asked for, when the run is new enough to have recorded it.</summary>
    public string? Request { get; }

    public bool CanRetry { get; }
    public bool CanRunAgain { get; }
    public string RunAgainTooltip { get; } = string.Empty;

    public RelayCommand RetryCommand { get; }
    public RelayCommand RunAgainCommand { get; }

    /// <summary>Run this again, exactly as it was.</summary>
    public event Action<PastRunViewModel>? RetryRequested;

    /// <summary>Run it again from the template as it stands now.</summary>
    public event Action<PastRunViewModel>? RunAgainRequested;

    public PastRunViewModel(
        RunRecord record,
        Action<ArtifactItemViewModel> open,
        Action<ArtifactItemViewModel> remove,
        // Passed in rather than looked up here: only the window knows the provider list, and a past
        // run must be classified by the same rule as a live one.
        Func<string?, ModelWorkSplit.Reach>? reachOf = null,
        // Every attempt at the same task, newest first. Passed in because only the window has the
        // whole history; a run cannot know its siblings from inside itself.
        IReadOnlyList<RunRecord>? attempts = null,
        Func<TaskTemplate?>? currentTemplate = null)
    {
        Record = record;
        Title = string.IsNullOrWhiteSpace(record.Title) ? "(untitled run)" : record.Title;

        var siblings = attempts is { Count: > 0 } ? attempts : new[] { record };
        var attemptNo = RunHistory.AttemptNumber(siblings, record.RunId);
        AttemptLabel = siblings.Count > 1
            ? $"attempt {attemptNo} of {siblings.Count}"
            : string.Empty;
        HasAttempts = AttemptLabel.Length > 0;

        Spec = ResolvedTaskSpec.Parse(record.Spec);
        Request = RunHistory.RequestOf(record);

        // Retry re-runs THIS specification, exactly as it was. For a run that was typed rather than
        // started from a template there is no specification, so it re-runs the request instead -
        // which is why the request is recorded as a value.
        CanRetry = Spec is not null || !string.IsNullOrWhiteSpace(Request);

        // Run again re-resolves the template as it stands NOW. Only offered when the template is
        // still there: a template that has been deleted or renamed has no current version, and a
        // button that fails when pressed is worse than one that is not there.
        var live = Spec is null ? null : currentTemplate?.Invoke();
        CanRunAgain = live is not null;
        RunAgainTooltip = Spec is null
            ? "This run did not come from a template."
            : live is null
                ? $"Template '{Spec.TemplateId}' is no longer in this library."
                : $"Re-resolve '{live.Name}' v{live.Version} — this run used v{Spec.TemplateVersion}.";

        RetryCommand = new RelayCommand(() => RetryRequested?.Invoke(this), () => CanRetry);
        RunAgainCommand = new RelayCommand(() => RunAgainRequested?.Invoke(this), () => CanRunAgain);
        Status = record.Status;
        // The pill is a tint with the colour in the word - see Brand. The 3px edge on a run
        // CARD stays saturated: an edge that thin has nowhere to put a tint.
        StatusBrush = Brand.PhaseFill(record.Status);
        StatusTextBrush = Brand.PhaseText(record.Status);

        var elapsed = record.FinishedAt - record.StartedAt;
        var model = string.IsNullOrWhiteSpace(record.Model) ? "unknown model" : record.Model;
        Meta = $"{record.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {model} · run {record.RunId:N}";

        foreach (var card in RunReplay.Steps(record))
            Steps.Add(card);
        foreach (var row in RunTimeline.Fold(record))
            Events.Add(row);
        foreach (var a in record.Artifacts)
            Artifacts.Add(new ArtifactItemViewModel(PathOf(a), open, remove));
        foreach (var d in record.Decisions)
            Decisions.Add(d);

        // The same three tiles the live run shows, off what was actually recorded.
        Routing = RunRouting.From(record);
        ModelWork = ModelWorkSplit.From(record, reachOf ?? (_ => ModelWorkSplit.Reach.Unknown));

        // Null usage means the run predates the counting, or the provider never reported - which is
        // not the same as zero and does not get to look like it.
        TokensText = record.Usage is { } u ? MainWindowViewModel.Compact(u.Total) : "—";
        TokensDetail = record.Usage is { } u2
            ? $"{MainWindowViewModel.Compact(u2.PromptTokens)} in · {MainWindowViewModel.Compact(u2.CompletionTokens)} out"
            : "nothing reported";

        ArtifactCountText = Artifacts.Count.ToString();
        ToolCallsText = record.Events.Count(e => e.Kind == nameof(EventKind.ToolInvoked)).ToString();
        ElapsedText = Duration(elapsed);

        var done = Steps.Count(c => c.StatusWord is "done" or "skipped");
        StepsText = Steps.Count == 0 ? "—" : $"{done} / {Steps.Count} steps";

        // What it was ALLOWED to do, as recorded. Runs from before this was kept say so rather
        // than showing today's slider position and passing it off as history.
        if (record.Settings is { } set)
        {
            HasSettings = true;
            AutonomyText = set.AutonomyName;
            AutonomyBrush = Brand.Autonomy(set.Autonomy);
            WorkerText = string.IsNullOrWhiteSpace(set.Worker) ? "default worker" : set.Worker;
            StagingText = set.Staged ? "changes were staged for review" : "changes were applied directly";
        }
        else
        {
            AutonomyText = WorkerText = StagingText = string.Empty;
            AutonomyBrush = Brand.TextMuted;
        }

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
    public IBrush StatusTextBrush { get; }

    /// <summary>The plan's steps, rebuilt from the record. Empty for a run stored before the step
    /// number was, in which case the timeline is the whole story.</summary>
    public ObservableCollection<StepCardViewModel> Steps { get; } = new();

    public bool HasSteps => Steps.Count > 0;
    public bool HasNoSteps => Steps.Count == 0;
    public string StepsText { get; }

    public ObservableCollection<RunEventViewModel> Events { get; } = new();
    public ObservableCollection<ArtifactItemViewModel> Artifacts { get; } = new();
    public ObservableCollection<string> Decisions { get; } = new();

    /// <summary>False for a run recorded before its setup was.</summary>
    public bool HasSettings { get; }
    public string AutonomyText { get; } = string.Empty;
    public IBrush AutonomyBrush { get; } = Brand.TextMuted;
    public string WorkerText { get; } = string.Empty;
    public string StagingText { get; } = string.Empty;

    public RunRouting Routing { get; }
    public ModelWorkSplit ModelWork { get; }
    public string TokensText { get; }
    public string TokensDetail { get; }

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

    /// <summary>
    /// A recorded artifact is the path. Older records kept the whole sentence — "FileSet: path" —
    /// and only a KNOWN artifact kind is stripped off the front of one, because splitting on the
    /// first ": " would eat the start of any path that happens to contain one.
    /// </summary>
    private static string PathOf(string artifact)
    {
        var colon = artifact.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0)
            return artifact;

        return Enum.TryParse<ArtifactKind>(artifact[..colon], ignoreCase: true, out _)
            ? artifact[(colon + 2)..]
            : artifact;
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

    /// <summary>
    /// Every record behind the list. Kept because a run's SIBLINGS - the other attempts at the same
    /// task - are not visible from the run itself, and the window is the only thing that has them.
    /// </summary>
    public IReadOnlyList<RunRecord> All { get; private set; } = Array.Empty<RunRecord>();

    public void Show(IReadOnlyList<RunRecord> records)
    {
        All = records;
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
