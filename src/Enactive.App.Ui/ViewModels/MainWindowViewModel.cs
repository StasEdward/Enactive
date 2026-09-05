namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media;
using Enactive.Core.History;
using Enactive.Workspace;
using Enactive.App.Ui.Mvvm;

/// <summary>A verb chip above the command bar. Clicking one prefixes the input.</summary>
internal sealed class VerbChipViewModel
{
    public VerbChipViewModel(string verb, Action<string> apply)
    {
        Verb = verb;
        Command = new RelayCommand(() => apply(verb));
    }

    public string Verb { get; }
    public RelayCommand Command { get; }
}

/// <summary>One button on the decision card: an option the engine offered, or one of the
/// remember-this-approval shortcuts the UI adds.</summary>
internal sealed class DecisionOptionViewModel
{
    public DecisionOptionViewModel(string label, Action chosen)
    {
        Label = label;
        Command = new RelayCommand(chosen);
    }

    public string Label { get; }
    public RelayCommand Command { get; }
}

/// <summary>A file the run produced, written straight to disk.</summary>
internal sealed class ArtifactItemViewModel : ObservableObject
{
    private string _status = string.Empty;
    private bool _canAct = true;

    public ArtifactItemViewModel(string relativePath, Action<ArtifactItemViewModel> review, Action<ArtifactItemViewModel> undo)
    {
        RelativePath = relativePath;
        ReviewCommand = new RelayCommand(() => review(this), () => CanAct);
        UndoCommand = new RelayCommand(() => undo(this), () => CanAct);
    }

    public string RelativePath { get; }

    public string Status { get => _status; set => Set(ref _status, value); }

    public bool CanAct
    {
        get => _canAct;
        set
        {
            if (!Set(ref _canAct, value))
                return;
            ReviewCommand.RaiseCanExecuteChanged();
            UndoCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand ReviewCommand { get; }
    public RelayCommand UndoCommand { get; }
}

/// <summary>One line of a unified diff, coloured by what it does to the file.</summary>
internal sealed class DiffLineViewModel
{
    public DiffLineViewModel(string text)
    {
        Text = text;
        Brush = text.StartsWith('+') ? Brand.Success
              : text.StartsWith('-') ? Brand.Danger
              : Brand.TextMuted;
    }

    public string Text { get; }
    public IBrush Brush { get; }
}

/// <summary>
/// A change the run staged rather than wrote: it waits here with its diff until the user applies or
/// rejects it. Amber while it waits - work is blocked on a person, which is caution, not action.
/// </summary>
internal sealed class StagedChangeViewModel : ObservableObject
{
    private string _status = "staged";
    private IBrush _statusBrush = Brand.Amber;
    private bool _isDiffVisible;
    private bool _canAct = true;

    public StagedChangeViewModel(
        string relativePath,
        bool isNew,
        IEnumerable<string> diffLines,
        Action<StagedChangeViewModel> apply,
        Action<StagedChangeViewModel> reject)
    {
        RelativePath = relativePath;
        Tag = isNew ? "new file" : "modified";
        foreach (var line in diffLines)
            Diff.Add(new DiffLineViewModel(line));

        DiffCommand = new RelayCommand(() => IsDiffVisible = !IsDiffVisible);
        ApplyCommand = new RelayCommand(() => apply(this), () => CanAct);
        RejectCommand = new RelayCommand(() => reject(this), () => CanAct);
    }

    public string RelativePath { get; }
    public string Tag { get; }
    public ObservableCollection<DiffLineViewModel> Diff { get; } = new();

    public string Status { get => _status; set => Set(ref _status, value); }
    public IBrush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    public bool IsDiffVisible { get => _isDiffVisible; set => Set(ref _isDiffVisible, value); }

    public bool CanAct
    {
        get => _canAct;
        set
        {
            if (!Set(ref _canAct, value))
                return;
            ApplyCommand.RaiseCanExecuteChanged();
            RejectCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand DiffCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand RejectCommand { get; }
}

/// <summary>
/// Everything the three-panel shell shows: workspace and autonomy on the left, the plan-step feed in
/// the centre, AI status, the decision card and artifacts on the right.
///
/// This is presentation state only. The engine wiring - building the orchestrator, draining its event
/// stream, answering a decision - stays in the window, and reaches the UI exclusively through these
/// properties, so no part of it touches a control any more.
/// </summary>
internal sealed class MainWindowViewModel : ObservableObject
{
    private string _inputText = string.Empty;
    private string _workspacePath = string.Empty;
    private string _modelLabel = string.Empty;
    private string _environmentSummary = string.Empty;
    private double _autonomyLevel = 2;
    private string _autonomyLabel = string.Empty;
    private IBrush _autonomyBrush = Brand.AutonomyExecute;
    private bool _stageChanges;
    private bool _runInBackground;
    private int _selectedWorkerIndex;
    private string? _selectedRecent;
    private string _statusPhase = "Idle";
    private IBrush _statusPillBrush = Brand.TextMuted;
    private string _taskTitle = string.Empty;
    private string _taskIntent = string.Empty;
    private bool _hasTask;
    private int _toolCalls;
    private int _selectedTab;
    private int _selectedPane;
    private bool _isViewingPast;
    private PastRunViewModel? _pastRun;
    private string _statusProgress = "—";
    private string _statusElapsed = "—";
    private string _currentAction = string.Empty;
    private string _agentBadge = string.Empty;
    private IBrush _agentBrush = Brand.Line;
    private bool _isAgentVisible;
    private bool _isDecisionVisible;
    private string _decisionText = string.Empty;
    private string _inboxLabel = "Inbox";
    private int _inboxUnread;
    private bool _isBusy;

    public MainWindowViewModel()
    {
        foreach (var verb in new[] { "Fix", "Build", "Investigate", "Explain", "Refactor" })
            Verbs.Add(new VerbChipViewModel(verb, PrefixInput));

        RunCommand = new RelayCommand(() => RunRequested?.Invoke(), () => !IsBusy);
        StopCommand = new RelayCommand(() => StopRequested?.Invoke(), () => IsBusy);
        TimelineCommand = new RelayCommand(() => TimelineRequested?.Invoke());
        LogCommand = new RelayCommand(() => LogRequested?.Invoke());
        EnvironmentCommand = new RelayCommand(() => EnvironmentRequested?.Invoke());
        InboxCommand = new RelayCommand(() => InboxRequested?.Invoke());
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());

        UpdateAutonomyLabel();

        // The tab badge counts what the run produced, so it follows the list rather than being
        // maintained by whoever happens to add to it.
        Artifacts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ArtifactCount));
            OnPropertyChanged(nameof(ArtifactCountText));
            OnPropertyChanged(nameof(ArtifactsTabLabel));
        };

        ShowExecutionCommand = new RelayCommand(() => SelectedTab = 0);
        ShowArtifactsCommand = new RelayCommand(() => SelectedTab = 1);
        ShowLogCommand = new RelayCommand(() => SelectedTab = 2);

        ShowWorkspacePaneCommand = new RelayCommand(() => SelectedPane = 0);
        ShowRunsPaneCommand = new RelayCommand(() =>
        {
            SelectedPane = 1;
            // Asking on the way in beats a Refresh button nobody presses: the list is only ever
            // looked at when this pane is open.
            Runs.RefreshCommand.Execute(null);
        });
        BackToLiveCommand = new RelayCommand(() => ShowLiveRun());
    }

    /// <summary>
    /// Gives the run's log tab the hub to read. Done after construction because the hub belongs to
    /// the window, which owns the engine; the view model only shows what it produces.
    /// </summary>
    public void AttachLog(LogHub hub)
    {
        RunLog = new RunLogViewModel(hub);
        OnPropertyChanged(nameof(RunLog));
    }

    /// <summary>The current run's log, for the tab beside its steps. Null until AttachLog.</summary>
    public RunLogViewModel? RunLog { get; private set; }

    public event Action? RunRequested;
    public event Action? StopRequested;
    public event Action? TimelineRequested;
    public event Action? LogRequested;
    public event Action? EnvironmentRequested;
    public event Action? InboxRequested;
    public event Action? SettingsRequested;

    /// <summary>Raised after the input text was changed from here, so the view can restore focus.</summary>
    public event Action? InputFocusRequested;

    // ── Command bar ───────────────────────────────────────────────────────────
    public ObservableCollection<VerbChipViewModel> Verbs { get; } = new();

    public string InputText { get => _inputText; set => Set(ref _inputText, value); }

    public RelayCommand RunCommand { get; }
    public RelayCommand StopCommand { get; }

    // ── Left panel ────────────────────────────────────────────────────────────
    /// <summary>
    /// The folder everything is scoped to. Changing it invalidates the run list, which belongs to
    /// the workspace that was open when it was loaded.
    /// </summary>
    public string WorkspacePath
    {
        get => _workspacePath;
        set
        {
            if (!Set(ref _workspacePath, value))
                return;
            Runs.Reset();
            if (IsRunsPane)
                Runs.RefreshCommand.Execute(null);
        }
    }
    public string ModelLabel { get => _modelLabel; set => Set(ref _modelLabel, value); }
    public string EnvironmentSummary { get => _environmentSummary; set => Set(ref _environmentSummary, value); }

    public ObservableCollection<string> RecentWorkspaces { get; } = new();

    /// <summary>Picking a recent workspace just fills the workspace box; it starts nothing.</summary>
    public string? SelectedRecent
    {
        get => _selectedRecent;
        set
        {
            if (Set(ref _selectedRecent, value) && !string.IsNullOrEmpty(value))
                WorkspacePath = value;
        }
    }

    public double AutonomyLevel
    {
        get => _autonomyLevel;
        set
        {
            if (Set(ref _autonomyLevel, value))
                UpdateAutonomyLabel();
        }
    }

    public string AutonomyLabel { get => _autonomyLabel; set => Set(ref _autonomyLabel, value); }
    public IBrush AutonomyBrush { get => _autonomyBrush; set => Set(ref _autonomyBrush, value); }

    /// <summary>The autonomy slider as the engine wants it: 0 Observe … 3 Autonomous.</summary>
    public int AutonomyTier => (int)Math.Round(AutonomyLevel);

    public bool StageChanges { get => _stageChanges; set => Set(ref _stageChanges, value); }
    public bool RunInBackground { get => _runInBackground; set => Set(ref _runInBackground, value); }

    public ObservableCollection<string> WorkerRoles { get; } = new();
    public int SelectedWorkerIndex { get => _selectedWorkerIndex; set => Set(ref _selectedWorkerIndex, value); }

    public string InboxLabel { get => _inboxLabel; set => Set(ref _inboxLabel, value); }

    /// <summary>
    /// How many inbox items are unread. The rail shows a dot rather than a number - at a glance you
    /// need "something is waiting", and the count itself is one hover away in the tooltip.
    /// </summary>
    public int InboxUnread
    {
        get => _inboxUnread;
        set
        {
            if (!Set(ref _inboxUnread, value))
                return;
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(InboxTooltip));
        }
    }

    public bool HasUnread => InboxUnread > 0;

    public string InboxTooltip => InboxUnread > 0 ? $"Inbox — {InboxUnread} unread" : "Inbox";

    public RelayCommand TimelineCommand { get; }
    public RelayCommand LogCommand { get; }
    public RelayCommand EnvironmentCommand { get; }
    public RelayCommand InboxCommand { get; }
    public RelayCommand SettingsCommand { get; }

    // ── Centre: the plan-step feed ────────────────────────────────────────────
    public ObservableCollection<StepCardViewModel> Steps { get; } = new();

    // ── Right: status, decision, artifacts ────────────────────────────────────
    /// <summary>
    /// The run's phase, and the colour of the pill that shows it. They move together: a phase set
    /// anywhere in the engine wiring repaints the pill without that code knowing a pill exists.
    /// </summary>
    public string StatusPhase
    {
        get => _statusPhase;
        set
        {
            if (!Set(ref _statusPhase, value))
                return;
            StatusPillBrush = value switch
            {
                "Completed" => Brand.Success,
                "Failed" or "Error" => Brand.Danger,
                "Cancelled" => Brand.TextMuted,
                "Idle" => Brand.TextMuted,
                _ => Brand.Accent
            };
        }
    }

    public IBrush StatusPillBrush { get => _statusPillBrush; set => Set(ref _statusPillBrush, value); }

    /// <summary>What the run is called - the plan's title once there is one, the request until then.</summary>
    public string TaskTitle { get => _taskTitle; set => Set(ref _taskTitle, value); }

    /// <summary>
    /// The request, kept verbatim. The command bar clears when a run starts, so without this the
    /// only place to read what you actually asked for was the log.
    /// </summary>
    public string TaskIntent { get => _taskIntent; set => Set(ref _taskIntent, value); }

    /// <summary>False before the first run of the session: an empty header is worse than none.</summary>
    public bool HasTask { get => _hasTask; set => Set(ref _hasTask, value); }

    /// <summary>
    /// Which of the three views of the run is showing. Kept as an index rather than three booleans
    /// because only one can be on, and the tab strip binds each button to its own flag off it.
    /// </summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value))
                return;
            OnPropertyChanged(nameof(IsExecutionTab));
            OnPropertyChanged(nameof(IsArtifactsTab));
            OnPropertyChanged(nameof(IsLogTab));
        }
    }

    public bool IsExecutionTab => _selectedTab == 0;
    public bool IsArtifactsTab => _selectedTab == 1;
    public bool IsLogTab => _selectedTab == 2;

    // ── The context column's two panes ────────────────────────────────────────
    /// <summary>The workspace's run history. Handed its records by whoever owns the stores.</summary>
    public RunsViewModel Runs { get; } = new();

    /// <summary>0 = workspace, 1 = runs. The rail switches it; the column shows one at a time
    /// because 268px cannot hold both and still leave the run setup readable.</summary>
    public int SelectedPane
    {
        get => _selectedPane;
        set
        {
            if (!Set(ref _selectedPane, value))
                return;
            OnPropertyChanged(nameof(IsWorkspacePane));
            OnPropertyChanged(nameof(IsRunsPane));
        }
    }

    public bool IsWorkspacePane => _selectedPane == 0;
    public bool IsRunsPane => _selectedPane == 1;

    public RelayCommand ShowWorkspacePaneCommand { get; }
    public RelayCommand ShowRunsPaneCommand { get; }

    // ── Reading a past run ────────────────────────────────────────────────────
    /// <summary>
    /// A finished run, opened read-only over the live one. The live run keeps running underneath -
    /// reading history never interrupts work - and Back returns to it.
    /// </summary>
    public PastRunViewModel? PastRun { get => _pastRun; private set => Set(ref _pastRun, value); }

    public bool IsViewingPast
    {
        get => _isViewingPast;
        private set
        {
            if (Set(ref _isViewingPast, value))
                OnPropertyChanged(nameof(IsViewingLive));
        }
    }

    public bool IsViewingLive => !_isViewingPast;

    public RelayCommand BackToLiveCommand { get; }

    public void ShowPastRun(RunRecord record)
    {
        PastRun = new PastRunViewModel(record);
        IsViewingPast = true;
    }

    /// <summary>Back to the run in progress. Called by Back, and by a new run starting.</summary>
    public void ShowLiveRun()
    {
        IsViewingPast = false;
        PastRun = null;
        Runs.Selected = null;
    }

    public RelayCommand ShowExecutionCommand { get; }
    public RelayCommand ShowArtifactsCommand { get; }
    public RelayCommand ShowLogCommand { get; }

    public int ArtifactCount => Artifacts.Count;

    /// <summary>The tab carries its own count, so an empty run does not advertise "Artifacts (0)".</summary>
    public string ArtifactsTabLabel => Artifacts.Count == 0 ? "Artifacts" : $"Artifacts ({Artifacts.Count})";

    /// <summary>Tile text. A tile shows a value under a label, so the number arrives ready to draw.</summary>
    public string ArtifactCountText => Artifacts.Count.ToString();

    public int ToolCalls
    {
        get => _toolCalls;
        set
        {
            if (Set(ref _toolCalls, value))
                OnPropertyChanged(nameof(ToolCallsText));
        }
    }

    public string ToolCallsText => _toolCalls.ToString();
    public string StatusProgress { get => _statusProgress; set => Set(ref _statusProgress, value); }
    public string StatusElapsed { get => _statusElapsed; set => Set(ref _statusElapsed, value); }
    public string CurrentAction { get => _currentAction; set => Set(ref _currentAction, value); }

    public string AgentBadge { get => _agentBadge; set => Set(ref _agentBadge, value); }
    public IBrush AgentBrush { get => _agentBrush; set => Set(ref _agentBrush, value); }
    public bool IsAgentVisible { get => _isAgentVisible; set => Set(ref _isAgentVisible, value); }

    public bool IsDecisionVisible { get => _isDecisionVisible; set => Set(ref _isDecisionVisible, value); }
    public string DecisionText { get => _decisionText; set => Set(ref _decisionText, value); }
    public ObservableCollection<DecisionOptionViewModel> DecisionOptions { get; } = new();

    /// <summary>Artifact cards and staged-change cards, in the order they appeared. Two item types,
    /// two templates - the list itself does not care which is which.</summary>
    public ObservableCollection<object> Artifacts { get; } = new();

    // ── Busy ──────────────────────────────────────────────────────────────────
    /// <summary>A run is in flight: the inputs lock, Run greys out and Stop lights up.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!Set(ref _isBusy, value))
                return;
            OnPropertyChanged(nameof(IsIdle));
            RunCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsIdle => !IsBusy;

    /// <summary>Shows the agent pill, unless it already says exactly this.</summary>
    public void SetAgent(string label, IBrush fill)
    {
        if (AgentBadge == label && IsAgentVisible)
            return;
        AgentBadge = label;
        AgentBrush = fill;
        IsAgentVisible = true;
    }

    private void PrefixInput(string verb)
    {
        InputText = string.IsNullOrWhiteSpace(InputText) ? verb + " " : verb + " " + InputText;
        InputFocusRequested?.Invoke();
    }

    private void UpdateAutonomyLabel()
    {
        var level = AutonomyTier;
        AutonomyLabel = LevelName(level);
        AutonomyBrush = Brand.Autonomy(level);
    }

    private static string LevelName(int level) => level switch
    {
        0 => "Observe — read only, asks before changes",
        1 => "Suggest — prepares changes, asks to apply",
        2 => "Execute — edits freely, asks before run_command",
        _ => "Autonomous — runs everything without asking"
    };
}
