using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Core.Diagnostics;
using Enactive.App.Ui.ViewModels;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using Enactive.Providers;
using Enactive.Tools;
using Enactive.Workspace;

namespace Enactive.App.Ui;

/// <summary>Three-panel Enactive shell: Workspace + autonomy (left) · plan-step feed (center) ·
/// AI status + decision card + artifacts (right). Reuses the engine unchanged.</summary>
public sealed partial class MainWindow : Window, IDecisionHandler
{
    private const string DeveloperInstructions =
        "You are a developer agent working inside the user's workspace. You have these tools: "
        + "write_file (create/overwrite a file), read_file (read a file), list_dir (list a directory), "
        + "run_command (run a shell command in the workspace). All paths are relative to the workspace root. "
        + "Use the tools to accomplish the request, then reply with a short confirmation of what you did.";

    // ── Reusable singletons ──────────────────────────────────────────────────
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private string _model = "qwen2.5-coder";
    private string _globalInstructions = string.Empty;
    private ChatProviderFactory _providerFactory = null!;
    private readonly IToolRegistry _toolRegistry;
    // Global, app-wide log hub. Default Debug (readable); the log window can drop it to Trace for raw wire.
    private readonly LogHub _log = new(minLevel: LogLevel.Debug, downstream: new ILogSink[] { new FileLogSink() });
    private LogWindow? _logWindow;
    private InboxWindow? _inboxWindow;
    private readonly EnvironmentProbe _envProbe = new();
    private readonly Planner _planner = new();
    private readonly ModelResolver _modelResolver = new();
    private StaticWorkerProvider _workerProvider = null!;
    private readonly PermissionEngine _permissionEngine = new();
    private AppSettings _settings = new();

    /// <summary>Everything the window shows. Nothing below touches a control - it sets a property here.</summary>
    private readonly MainWindowViewModel _vm = new();
    private readonly HashSet<string> _shownArtifacts = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Workspaces already told about their ignored legacy approvals file — say it once.</summary>
    private readonly HashSet<string> _legacyApprovalsNoted = new(StringComparer.OrdinalIgnoreCase);

    // ── Run state ────────────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<DecisionOutcome>? _pendingDecision;
    private readonly HashSet<string> _sessionApprovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StepCardViewModel> _cards = new();
    private readonly List<StepCardViewModel> _running = new();
    private StepCardViewModel? _currentCard;
    private int _stepIndex;
    private int _doneSteps;
    private int _totalSteps;
    private readonly Stopwatch _runStopwatch = new();
    private DispatcherTimer? _elapsedTimer;
    private string _currentWorkspaceRoot = string.Empty;
    private readonly WorkspaceRegistry _registry = WorkspaceRegistry.Load();
    private int _backgroundRuns;
    private bool _forceClose;
    private StagingArtifactStore? _staging;
    /// <summary>The current run's disk store, when it is writing straight to the workspace. Kept so the
    /// artifact cards can ask whether a file was CREATED by this run or only overwritten.</summary>
    private DiskArtifactStore? _disk;
    private int _stagedShown;

    public MainWindow()
    {
        _settings = AppSettings.Load();
        _toolRegistry = new LoggingToolRegistry(new ToolRegistry(new ITool[]
        {
            new WriteFileTool(), new ReadFileTool(), new ListDirectoryTool(), new RunCommandTool(), new RunPowerShellTool(), new GitTool(), new DockerTool()
        }), _log);
        ApplySettings();
        _log.Info(LogSource.System, $"Enactive UI started — logs at {FileLogSink.DefaultDirectory()}");

        // Where to start: the environment wins, then the workspace last opened. The old default -
        // the current directory - made the folder the .exe happens to sit in a workspace, complete
        // with a .enactive folder nobody asked for.
        var env = Environment.GetEnvironmentVariable("ENACTIVE_WORKSPACE");
        _vm.WorkspacePath = !string.IsNullOrWhiteSpace(env)
            ? env
            : _registry.LastOpened?.RootPath ?? Directory.GetCurrentDirectory();
        _vm.AttachLog(_log);

        // The view model asks; the window is what can actually open a child window or move focus.
        _vm.RunRequested += () => _ = RunAsync(background: false);
        _vm.BackgroundRunRequested += () => _ = RunAsync(background: true);
        _vm.StopRequested += () => _cts?.Cancel();
        _vm.TimelineRequested += () => _ = ShowTimelineAsync();
        _vm.LogRequested += ShowLogWindow;
        _vm.EnvironmentRequested += () => _ = ShowEnvironmentAsync();
        _vm.InboxRequested += ShowInbox;
        _vm.Runs.RefreshRequested += () => _ = LoadRunsAsync();
        _vm.Runs.OpenRequested += record => _vm.ShowPastRun(BuildPastRun(record));
        _vm.WorkspacePathChanged += RefreshWorkspaces;
        _vm.WorkspaceSwitchRequested += SwitchWorkspace;
        _vm.WorkspaceRenameRequested += path => _ = RenameWorkspaceAsync(path);
        _vm.WorkspaceForgetRequested += path => _ = ForgetWorkspaceAsync(path);
        _vm.RunSettingsChanged += SaveRunSettings;
        _vm.AddWorkspaceRequested += () => _ = AddWorkspaceAsync();
        _vm.SettingsRequested += () =>
            // SettingsWindow reads the live settings and mutates them only when Save is clicked
            // (Cancel/close leave them untouched), so it gets _settings directly, not a partial copy.
            new SettingsWindow(_settings, saved => { _settings = saved; saved.Save(); ApplySettings(); }).Show(this);
        _vm.InputFocusRequested += () =>
        {
            InputBox.Focus();
            InputBox.CaretIndex = _vm.InputText.Length;
        };

        DataContext = _vm;
        InitializeComponent();

        // After InitializeComponent, or the XAML's own Width/Height would overwrite the saved bounds.
        TrySetIcon();
        if (_settings.WindowWidth > 300 && _settings.WindowHeight > 300)
        {
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(_settings.WindowX, _settings.WindowY);
        }
        Closing += (_, e) =>
        {
            // By default closing the window is not quitting: it goes to the tray, and a run it
            // started keeps going - which is the whole reason a background run exists. Quitting is
            // then Exit, on the tray menu, and that is where the question about unfinished work
            // lives. Settings · General can make the X quit instead, and with no tray on this
            // desktop there is nowhere to hide, so it quits either way.
            if (!_forceClose)
            {
                e.Cancel = true;
                if (HasTray && _settings.CloseToTray)
                {
                    SaveWindowBounds();
                    Hide();
                }
                else
                {
                    // Shutdown is explicit now, so the X has to do the whole job: ask about
                    // unfinished work, then actually end the process.
                    _ = RequestExitAsync();
                }
                return;
            }

            SaveWindowBounds();
            _vm.RunLog?.Detach();
            _log.Dispose();
        };

        // Tunnel so we see the keys before the TextBox consumes Enter.
        AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        RefreshWorkspaces();
        ApplyWorkspaceDefaults();
        // The history is always on screen now, so it is always loaded - including for the workspace
        // restored at startup.
        _ = LoadRunsAsync();
    }

    // ── Closing ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Work that dies with the window: the run in the foreground, a decision it is waiting on, and
    /// any background run - those live in a Task owned by this process, so closing kills them
    /// halfway through whatever they were writing.
    /// </summary>
    private bool HasWorkInFlight()
        => _vm.IsBusy || _pendingDecision is not null || Volatile.Read(ref _backgroundRuns) > 0;

    private string DescribeWorkInFlight()
    {
        var background = Volatile.Read(ref _backgroundRuns);
        if (_pendingDecision is not null)
            return "A run is waiting for your decision. Closing now cancels it.";
        if (_vm.IsBusy && background > 0)
            return $"A run is going, and {background} more in the background. Closing now stops all of them where they are.";
        if (_vm.IsBusy)
            return "A run is going. Closing now stops it where it is - a step that was half written stays half written.";
        return background == 1
            ? "A background run is still going. Closing now stops it where it is."
            : $"{background} background runs are still going. Closing now stops them where they are.";
    }

    /// <summary>
    /// False when this desktop has no system tray. Then the window has nowhere to hide and closing
    /// it means what it used to mean - so it asks, and quits.
    /// </summary>
    public bool HasTray { get; set; } = true;

    /// <summary>Brings the window back from the tray, wherever it was left.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Exit, from the tray menu. The only thing in this app that ends the process.</summary>
    public void RequestExit() => _ = RequestExitAsync();

    private async Task RequestExitAsync()
    {
        if (HasWorkInFlight())
        {
            // The question needs a window to sit on, and the window is probably in the tray - which
            // is also the honest thing to do: show what is running before asking about killing it.
            ShowFromTray();

            var quit = await ConfirmWindow.AskAsync(
                this,
                "Enactive is still working",
                DescribeWorkInFlight(),
                "Quit anyway",
                "Keep working");

            if (!quit)
                return;

            _cts?.Cancel();
        }

        _forceClose = true;
        Close();

        // Shutdown is explicit now: the lifetime no longer ends with the main window, because the
        // main window comes and goes from the tray.
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }


    // ── Run an intent ────────────────────────────────────────────────────────
    /// <summary>
    /// Starts the request in the box. <paramref name="background"/> comes from WHICH BUTTON was
    /// pressed, not from a setting: the same words typed into the same box should not do two
    /// different things depending on state the user cannot see from here.
    /// </summary>
    private async Task RunAsync(bool background)
    {
        if (_pendingDecision is not null)
            return;

        var text = _vm.InputText.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        var workspacePath = _vm.WorkspacePath.Trim();
        if (string.IsNullOrEmpty(workspacePath))
            return;

        // The app used to create whatever path was in the box. That is how an empty workspace
        // appeared beside the .exe on first start: a typo, or a default nobody chose, became a
        // folder. A run happens in a folder that already exists, or it does not happen.
        if (!Directory.Exists(workspacePath))
        {
            _vm.StatusPhase = "Error";
            _vm.CurrentAction = $"No such folder: {workspacePath}. Pick another workspace, or create the folder yourself first.";
            return;
        }

        // Background: fire the run headless (results land in the Inbox) and keep the UI free.
        if (background)
        {
            StartBackground(text, Path.GetFullPath(workspacePath));
            return;
        }

        // The command bar clears, so the request moves into the header - otherwise what you asked
        // for survives only in the log.
        _vm.TaskIntent = text;
        _vm.TaskTitle = Summarise(text);
        _vm.HasTask = true;
        _vm.ToolCalls = 0;
        _vm.ResetUsage();
        _vm.Routing.Clear();
        _vm.SelectedTab = 0;
        // Reading history is fine; watching it while a new run of your own starts is not.
        _vm.ShowLiveRun();

        // reset run state / panels
        _vm.InputText = string.Empty;
        _vm.Steps.Clear();
        _vm.Artifacts.Clear();
        _shownArtifacts.Clear();
        _cards.Clear();
        _currentCard = null;
        _running.Clear();
        _stepIndex = 0;
        _doneSteps = 0;
        _totalSteps = 0;
        _vm.IsDecisionVisible = false;
        _vm.IsAgentVisible = false;
        _vm.StatusPhase = "Running";
        _vm.StatusProgress = "—";
        _vm.StatusElapsed = "0s";
        _vm.CurrentAction = string.Empty;
        _vm.IsBusy = true;
        _elapsedTimer?.Stop();
        _runStopwatch.Restart();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => _vm.StatusElapsed = FormatElapsed(_runStopwatch.Elapsed);
        _elapsedTimer.Start();

        _cts = new CancellationTokenSource();

        var runSettings = CurrentRunSettings();

        var fullPath = Path.GetFullPath(workspacePath);
        _currentWorkspaceRoot = fullPath;
        NoteLegacyApprovalsIfAny(fullPath);
        _registry.Touch(fullPath);
        RefreshWorkspaces();
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        if (string.IsNullOrEmpty(name))
            name = "workspace";

        var workspace = new WorkspaceInfo(WorkspaceInfo.IdFor(fullPath), name, fullPath);
        var policy = PolicyFor(_vm.AutonomyTier);

        IArtifactStore artifactStore;
        if (_vm.StageChanges)
        {
            var staging = new StagingArtifactStore(fullPath);
            _staging = staging;
            _disk = null;
            artifactStore = staging;
        }
        else
        {
            _staging = null;
            var disk = new DiskArtifactStore(workspace);
            _disk = disk;
            artifactStore = disk;
        }
        _stagedShown = 0;

        try
        {
            var runStore = RunStoreFactory.Create(workspace);
            var contextProvider = new ContextProvider(workspace, new EnvironmentProbe());
            var orchestrator = new Orchestrator(
                _providerFactory, _modelResolver, _workerProvider, _toolRegistry, artifactStore,
                workspace, _planner, _permissionEngine, this, policy, new EmptyProvider(),
                BuildRouter(), 1, _settings.NumCtx, _settings.DisableThinking, _settings.MaxParallelSteps,
                _settings.AllowImplicitToolCalls);
            var recorder = new RunRecorder(runStore, MemoryStoreFactory.Create(workspace), workspace.Id, runSettings);

            var context = await contextProvider.BuildAsync(new IntentFocus(workspace.Id), _cts.Token);
            var workerId = _workerProvider.All.Count > 0
                && _vm.SelectedWorkerIndex >= 0 && _vm.SelectedWorkerIndex < _workerProvider.All.Count
                ? _workerProvider.All[_vm.SelectedWorkerIndex].Id : null;
            var intent = new Intent(Guid.NewGuid(), text, IntentSource.CommandBar, context, DateTimeOffset.UtcNow, workerId);
            var envLine = context.Environment?.OneLine();
            Dispatcher.UIThread.Post(() => _vm.EnvironmentSummary = envLine ?? "(no environment data)");

            await Task.Run(async () =>
            {
                await foreach (var ev in recorder.RecordAsync(orchestrator.SubmitIntentAsync(intent, _cts.Token).TeeToLog(_log, _cts.Token), _cts.Token))
                    RenderEvent(ev);
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => { _vm.StatusPhase = "Cancelled"; _currentCard?.SetFailed(); });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => { _vm.StatusPhase = "Error"; _vm.CurrentAction = ex.Message; _currentCard?.SetFailed(); });
        }
        finally
        {
            _elapsedTimer?.Stop();
            _elapsedTimer = null;
            _runStopwatch.Stop();
            var finalElapsed = FormatElapsed(_runStopwatch.Elapsed);
            Dispatcher.UIThread.Post(() =>
            {
                _vm.StatusElapsed = finalElapsed;
                // The run that just ended belongs at the top of the history under the workspace.
                _ = LoadRunsAsync();
            });
            _vm.IsBusy = false;
            _cts = null;
        }
    }

    // ── Map events to the three panels (always on the UI thread) ─────────────
    private void RenderEvent(WorkEvent ev)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // The run id is the orchestrator's to mint, so the log tab learns it from the first
            // event rather than being told in advance.
            _vm.RunLog?.SetRun(ev.RunId);

            switch (ev.Kind)
            {
                case EventKind.IntentReceived:
                    _vm.StatusPhase = "Understanding";
                    break;
                case EventKind.Routed:
                    _vm.Routing.Apply(ev.Summary);
                    if (ev.Summary.Contains("-> model"))
                        _vm.StatusPhase = "Planning";
                    if (ev.Summary.StartsWith("Quick action: ", StringComparison.Ordinal))
                        _vm.TaskTitle = ev.Summary["Quick action: ".Length..];
                    if (ev.Summary.StartsWith("Reasoner", StringComparison.Ordinal))
                        _vm.SetAgent("Reasoner · planning", Brand.PillReasoner);
                    break;
                case EventKind.PlanCreated:
                    _vm.StatusPhase = "Executing";
                    // "<title> — N steps: a | b" — the planner's title is a better header than the
                    // raw request, which is often a paragraph.
                    var dash = ev.Summary.IndexOf(" — ", StringComparison.Ordinal);
                    if (dash > 0)
                        _vm.TaskTitle = ev.Summary[..dash];
                    CreateStepCards(ev.Summary);
                    break;
                case EventKind.StepStarted:
                    _vm.SetAgent("Coder", Brand.PillCoder);
                    BeginStep(ev);
                    (CardFor(ev) ?? EnsureCurrentCard()).SetActivity("Thinking…");
                    break;
                case EventKind.StepCompleted:
                    var doneCard = CardFor(ev) ?? _currentCard;
                    // A failed step and a dependency-skipped step arrive as StepCompleted too, so the
                    // card must not go green for either of them.
                    var wasSkipped = ev.Summary.Contains("skipped (dependency failed)", StringComparison.Ordinal);
                    var wasFailed = wasSkipped || ev.Summary.Contains("FAILED:", StringComparison.Ordinal);
                    if (wasSkipped)
                    {
                        // Skipped is not failed: nothing went wrong in THIS step, and painting it red
                        // sends you looking for a fault that is in another card.
                        doneCard?.SetSkipped();
                        doneCard?.SetActivity("Skipped — a dependency failed");
                    }
                    else if (wasFailed)
                    {
                        doneCard?.SetFailed();
                        doneCard?.SetActivity("Failed");
                        doneCard?.ExpandForAttention();
                    }
                    else
                    {
                        doneCard?.SetDone();
                        doneCard?.SetActivity("Done");
                    }
                    EndStep(doneCard);
                    _doneSteps++;
                    UpdateProgress();
                    break;
                case EventKind.AssistantDelta:
                    _vm.SetAgent("Coder", Brand.PillCoder);
                    var streamCard = CardFor(ev) ?? EnsureCurrentCard();
                    // Buffered, not shown live - the raw streamed reply isn't interesting on its own;
                    // it gets folded into one short note the next time a tool runs or the step ends.
                    streamCard.AppendAssistantText(ev.Summary);
                    streamCard.SetActivity("Thinking…");
                    break;
                case EventKind.ToolInvoked:
                    _vm.SetAgent("Coder", Brand.PillCoder);
                    _vm.ToolCalls++;
                    _vm.CurrentAction = ev.Summary;
                    var toolCard = CardFor(ev) ?? EnsureCurrentCard();
                    StepCardWriter.LogInvocation(toolCard, ev.Summary);
                    toolCard.SetActivity(StepCardWriter.DescribeActivity(ev.Summary));
                    break;
                case EventKind.ToolResult:
                    (CardFor(ev) ?? EnsureCurrentCard()).AppendEntryDetail(ev.Summary);
                    break;
                case EventKind.ErrorObserved:
                    // Something needs the user's eyes - a recovered implicit tool call, a stalled
                    // segment, ... - so this card does not stay collapsed like routine progress does.
                    var warnCard = CardFor(ev) ?? EnsureCurrentCard();
                    warnCard.AddNote("⚠ " + ev.Summary);
                    warnCard.SetActivity("⚠ " + ev.Summary);
                    warnCard.ExpandForAttention();
                    break;
                case EventKind.ReviewRequested:
                case EventKind.ReviewPassed:
                case EventKind.ReviewFailed:
                    _vm.SetAgent("Reasoner · review", Brand.PillReasoner);
                    _vm.CurrentAction = ev.Summary;
                    var reviewCard = CardFor(ev) ?? EnsureCurrentCard();
                    reviewCard.AddNote(ev.Summary);
                    reviewCard.SetActivity(
                        ev.Kind == EventKind.ReviewRequested ? "Reviewing…" :
                        ev.Kind == EventKind.ReviewPassed ? "Review passed" : "Review flagged an issue…");
                    break;
                case EventKind.DecisionRequested:
                case EventKind.DecisionResolved:
                    _vm.CurrentAction = ev.Summary;
                    var decisionCard = CardFor(ev) ?? EnsureCurrentCard();
                    decisionCard.AddNote(ev.Summary);
                    if (ev.Kind == EventKind.DecisionRequested)
                    {
                        decisionCard.SetActivity("Waiting for your approval…");
                        decisionCard.ExpandForAttention();
                    }
                    break;
                case EventKind.UsageReported:
                    if (ev.Usage() is { } used)
                        _vm.AddUsage(used.In, used.Out);
                    break;
                case EventKind.ArtifactProduced:
                    (CardFor(ev) ?? EnsureCurrentCard()).AddNote("Artifact: " + ev.Summary);
                    if (_staging is not null)
                        AddStagedArtifact();
                    else
                        AddArtifact(ev.Summary);
                    break;
                case EventKind.TaskCompleted:
                    _vm.StatusPhase = "Completed";
                    _vm.CurrentAction = string.Empty;
                    _currentCard?.SetDone();
                    _currentCard?.SetActivity("Done");
                    _vm.IsAgentVisible = false;
                    break;
                case EventKind.TaskFailed:
                    _vm.StatusPhase = "Failed";
                    _currentCard?.SetFailed();
                    _currentCard?.SetActivity("Failed");
                    break;
            }
        });
    }

    private void CreateStepCards(string planSummary)
    {
        var marker = " steps: ";
        var index = planSummary.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return;

        var titles = planSummary[(index + marker.Length)..].Split(" | ", StringSplitOptions.RemoveEmptyEntries);
        _totalSteps = titles.Length;
        foreach (var title in titles)
        {
            var card = new StepCardViewModel(title.Trim());
            _cards.Add(card);
            _vm.Steps.Add(card);
        }
        UpdateProgress();
    }

    private void BeginStep(WorkEvent ev)
    {
        // Prefer the step number the orchestrator stamped on the event; steps can start out of order
        // (and several at once) once MaxParallelSteps > 1, so a running counter is not enough.
        var index = ev.StepNo() ?? ++_stepIndex;
        _stepIndex = Math.Max(_stepIndex, index);

        StepCardViewModel card;
        if (index - 1 < _cards.Count)
        {
            card = _cards[index - 1];
        }
        else
        {
            card = new StepCardViewModel(ev.Summary);
            _cards.Add(card);
            _vm.Steps.Add(card);
            _totalSteps = _cards.Count;
        }
        card.SetRunning();
        _running.Add(card);
        // With one step in flight this is that step; with several, events without a step number have
        // no single owner, so nothing claims to be "current".
        _currentCard = _running.Count == 1 ? card : null;
        _vm.CurrentAction = ev.Summary;
    }

    private void EndStep(StepCardViewModel? card)
    {
        if (card is not null)
            _running.Remove(card);
        _currentCard = _running.Count == 1 ? _running[0] : null;
    }

    /// <summary>The card this event belongs to, or null when it carries no step number.</summary>
    private StepCardViewModel? CardFor(WorkEvent ev)
    {
        var n = ev.StepNo();
        return n is { } i && i - 1 >= 0 && i - 1 < _cards.Count ? _cards[i - 1] : null;
    }

    private StepCardViewModel EnsureCurrentCard()
    {
        if (_currentCard is null)
        {
            var card = new StepCardViewModel("Working");
            card.SetRunning();
            _cards.Add(card);
            _vm.Steps.Add(card);
            _currentCard = card;
            if (_totalSteps == 0)
                _totalSteps = 1;
        }
        return _currentCard;
    }

    private void UpdateProgress()
        => _vm.StatusProgress = _totalSteps > 0 ? $"{_doneSteps} / {_totalSteps} steps" : "—";

    /// <summary>A one-line title from a request that may be a paragraph. Held until the planner
    /// produces a real title, which it almost always does.</summary>
    private static string Summarise(string text)
    {
        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 70 ? line : line[..70] + "…";
    }

    private static string FormatElapsed(TimeSpan span)
    {
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s";
        if (span.TotalMinutes >= 1)
            return $"{span.Minutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }

    private void AddArtifact(string summary)
    {
        var relative = summary.Contains(": ", StringComparison.Ordinal)
            ? summary[(summary.IndexOf(": ", StringComparison.Ordinal) + 2)..]
            : summary;

        // One card per file: repeated writes to the same path say nothing new.
        if (!_shownArtifacts.Add(relative))
            return;

        var root = _currentWorkspaceRoot;

        // "Undo" used to be an unconditional File.Delete: if the agent EDITED an existing source file,
        // pressing it deleted the source outright and reported "undone". Nothing here can restore a
        // previous version yet, so the button no longer claims to. A file this run created can be
        // removed in one click; anything that was already on disk gets a plain delete confirmation
        // that says the old content was not kept.
        var createdByThisRun = _disk?.CreatedHere(relative) == true;

        _vm.Artifacts.Add(new ArtifactItemViewModel(
            relative,
            item => ShowFile(root, item),
            item => _ = RemoveLiveArtifactAsync(root, item, createdByThisRun),
            createdByThisRun ? "Delete" : "Delete…"));
    }

    private void AddStagedArtifact()
    {
        if (_staging is null || _stagedShown >= _staging.Changes.Count)
            return;

        var change = _staging.Changes[_stagedShown++];
        var staging = _staging;
        var diff = TextDiff.Unified(change.OldContent, change.NewContent, change.RelativePath)
            .Replace("\r\n", "\n")
            .Split('\n');

        _vm.Artifacts.Add(new StagedChangeViewModel(
            change.RelativePath,
            change.IsNew,
            diff,
            item =>
            {
                staging.Apply(change.Id);
                item.Status = "applied";
                item.StatusBrush = Brand.Success;
                item.CanAct = false;
            },
            item =>
            {
                staging.Reject(change.Id);
                item.Status = "rejected";
                item.StatusBrush = Brand.Danger;
                item.CanAct = false;
            }));
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            InputBox.Focus();
            InputBox.SelectAll();
            return;
        }

        if (e.Key == Key.Enter && InputBox.IsFocused)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                // Ctrl+Enter → newline
                e.Handled = true;
                InsertNewlineIntoInput();
            }
            else
            {
                // Enter → run
                e.Handled = true;
                _ = RunAsync(background: false);
            }
        }
    }

    private void InsertNewlineIntoInput()
    {
        var text = _vm.InputText;
        var caret = Math.Clamp(InputBox.CaretIndex, 0, text.Length);
        _vm.InputText = text[..caret] + "\n" + text[caret..];
        InputBox.CaretIndex = caret + 1;
    }

    // ── The workspace registry ───────────────────────────────────────────────
    /// <summary>
    /// Rebuilds the switcher from the registry and re-reads the current folder. Called at startup
    /// and whenever the workspace changes - which is the only moment the disk needs asking.
    /// </summary>
    private void RefreshWorkspaces()
    {
        var current = WorkspaceRegistry.Normalise(_vm.WorkspacePath);

        _vm.WorkspaceName = WorkspaceRegistry.NameFor(current);
        _vm.WorkspaceMissing = current.Length > 0 && !Directory.Exists(current);

        _vm.Workspaces.Clear();
        foreach (var entry in _registry.Entries)
            _vm.Workspaces.Add(new WorkspaceItemViewModel(
                entry,
                Directory.Exists(entry.RootPath),
                string.Equals(entry.RootPath, current, StringComparison.OrdinalIgnoreCase),
                _vm.RequestSwitch,
                _vm.RequestRename,
                _vm.RequestForget));
    }

    /// <summary>
    /// Switching a workspace re-scopes everything the window shows - runs, memory, the inbox badge,
    /// artifacts - because every store is built from the workspace it is asked about. It does not
    /// touch the folder, and it does not start anything.
    /// </summary>
    private void SwitchWorkspace(string path)
    {
        if (_vm.IsBusy)
        {
            _vm.CurrentAction = "Finish or stop the run before switching workspace.";
            return;
        }

        _vm.WorkspacePath = WorkspaceRegistry.Normalise(path);
        _registry.Touch(_vm.WorkspacePath);
        RefreshWorkspaces();
        ApplyWorkspaceDefaults();
        RefreshInboxButton();
    }

    /// <summary>
    /// Adds a folder to the list. Only a folder that EXISTS can be added - the app used to create
    /// whatever path was in the box, which is how an empty workspace appeared beside the .exe.
    /// </summary>
    private async Task AddWorkspaceAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a workspace folder",
            AllowMultiple = false
        });

        var picked = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(picked))
            return;

        _registry.Touch(picked);
        SwitchWorkspace(picked);
    }

    /// <summary>
    /// Gives a workspace a name of its own. Two checkouts both called "src" are told apart by their
    /// paths today, which is exactly what the old panel got wrong.
    /// </summary>
    private async Task RenameWorkspaceAsync(string path)
    {
        var entry = _registry.Find(path);
        if (entry is null)
            return;

        var name = await PromptWindow.AskAsync(
            this,
            "Name this workspace",
            "What Enactive calls it. The folder is not touched.",
            entry.Name);

        if (name is null)
            return;

        _registry.Rename(path, name);
        RefreshWorkspaces();
    }

    /// <summary>
    /// Keeps autonomy, the worker role and staging on the workspace they were set for. They are
    /// properties of the place, not of the person: a scratch folder wants Autonomous and the repo
    /// you ship from does not, and nobody remembers to move the slider back.
    /// </summary>
    private void SaveRunSettings()
    {
        var path = _vm.WorkspacePath.Trim();
        if (path.Length == 0)
            return;

        _registry.SaveSettings(path, _vm.AutonomyTier, CurrentWorkerRole(), _vm.StageChanges);
    }

    /// <summary>
    /// Loads a workspace's saved setup into the window. Guarded, or every load would look like an
    /// edit and write itself back onto whichever workspace was current a moment ago.
    /// </summary>
    private void ApplyWorkspaceDefaults()
    {
        var entry = _registry.Find(_vm.WorkspacePath.Trim());
        if (entry is null)
            return;

        _vm.IsLoadingWorkspaceDefaults = true;
        try
        {
            _vm.AutonomyLevel = Math.Clamp(entry.Autonomy, 0, 3);
            _vm.StageChanges = entry.StageChanges;

            // By role name, not by position: the list changes when the roles are edited, and an
            // index would then quietly select a different worker.
            var index = entry.WorkerId is null ? -1 : _vm.WorkerRoles.IndexOf(entry.WorkerId);
            if (index >= 0)
                _vm.SelectedWorkerIndex = index;
        }
        finally
        {
            _vm.IsLoadingWorkspaceDefaults = false;
        }
    }

    /// <summary>
    /// Forgetting a workspace used to be harmless - the entry was a path and nothing else. It now
    /// holds the name you gave it and how a run behaves in it, and neither comes back with the
    /// folder. So it asks, and says what is actually lost.
    /// </summary>
    private async Task ForgetWorkspaceAsync(string path)
    {
        var entry = _registry.Find(path);
        if (entry is null)
            return;

        var forget = await ConfirmWindow.AskAsync(
            this,
            $"Forget \u201c{entry.Name}\u201d?",
            "The folder and everything in it, including its run history, are left exactly where they are. "
            + "What goes is the name you gave it here and how runs are set up in it.",
            "Forget",
            "Keep");

        if (!forget)
            return;

        _registry.Remove(path);
        RefreshWorkspaces();
    }


    private void ShowLogWindow()
    {
        if (_logWindow is null)
        {
            _logWindow = new LogWindow(_log);
            _logWindow.Closed += (_, _) => _logWindow = null;
        }
        _logWindow.Show();
        _logWindow.Activate();
    }

    private async Task ShowEnvironmentAsync()
    {
        var root = _vm.WorkspacePath;
        if (string.IsNullOrWhiteSpace(root))
        {
            ShowViewer("Environment", "Set a workspace first.");
            return;
        }
        try
        {
            var full = Path.GetFullPath(root);
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
            if (string.IsNullOrEmpty(name)) name = "workspace";
            var ws = new WorkspaceInfo(WorkspaceInfo.IdFor(full), name, full);
            var (info, snapshot) = await _envProbe.ProbeFullAsync(ws, CancellationToken.None);
            _vm.EnvironmentSummary = info.OneLine();
            ShowViewer("Environment", info.Summary() + "\n\n" + snapshot.Describe());
        }
        catch (Exception ex)
        {
            ShowViewer("Environment", "Probe failed: " + ex.Message);
        }
    }

    // Builds the phase->model router. Today it mirrors the MultiAgent setting (Plan+Review -> Anthropic);
    // the team/provider editors will populate richer bindings later (Docs/MODELS.md).
    private IModelRouter BuildRouter()
    {
        var bindings = new Dictionary<ModelPurpose, ModelRef>();
        if (AppSettings.ParseRef(_settings.Bindings.Plan) is { } plan)
            bindings[ModelPurpose.Plan] = plan;
        if (AppSettings.ParseRef(_settings.Bindings.Review) is { } review)
            bindings[ModelPurpose.Review] = review;
        return new ModelRouter(
            _modelResolver,
            bindings,
            AppSettings.ParseRef(_settings.Bindings.ExecuteLight),
            AppSettings.ParseRef(_settings.Bindings.ExecuteHeavy));
    }

    /// <summary>
    /// The run's setup as it is RIGHT NOW, for the record. Read once at the start, because by the
    /// time anyone opens the run to ask what it was allowed to do, the slider will have moved.
    /// </summary>
    private RunSettings CurrentRunSettings()
        => new(_vm.AutonomyTier, MainWindowViewModel.LevelName(_vm.AutonomyTier), CurrentWorkerRole(), _vm.StageChanges);

    /// <summary>The role the worker box is on, by name - null when there are no roles to pick from.</summary>
    private string? CurrentWorkerRole()
        => _vm.SelectedWorkerIndex >= 0 && _vm.SelectedWorkerIndex < _vm.WorkerRoles.Count
            ? _vm.WorkerRoles[_vm.SelectedWorkerIndex]
            : null;

    private static WorkspaceInfo WorkspaceFrom(string path)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
        return new WorkspaceInfo(WorkspaceInfo.IdFor(full), string.IsNullOrEmpty(name) ? "workspace" : name, full);
    }

    private void StartBackground(string text, string fullPath)
    {
        // Background runs always wrote straight to disk while the run settings — and the history —
        // said "staged". Rather than lie about it, refuse the combination: staging that survives a
        // background run needs a store that persists its proposals, which does not exist yet.
        if (_vm.StageChanges)
        {
            _vm.StatusPhase = "Not started";
            _vm.CurrentAction =
                "Stage changes is on, and a background run cannot stage: it would write to your files "
                + "directly while the history claimed the changes were staged. Turn Stage changes off "
                + "to run in the background, or run this in the foreground.";
            return;
        }

        var workspace = WorkspaceFrom(fullPath);
        var policy = PolicyFor(_vm.AutonomyTier);
        var runSettings = CurrentRunSettings();
        var workerId = _workerProvider.All.Count > 0
            && _vm.SelectedWorkerIndex >= 0 && _vm.SelectedWorkerIndex < _workerProvider.All.Count
            ? _workerProvider.All[_vm.SelectedWorkerIndex].Id : null;
        var inbox = InboxStoreFactory.Create(workspace);
        _registry.Touch(fullPath);
        RefreshWorkspaces();

        _vm.StatusPhase = "Background task started";
        _vm.CurrentAction = text;
        _vm.TaskIntent = text;
        _vm.TaskTitle = Summarise(text);
        _vm.HasTask = true;
        _vm.InputText = string.Empty;
        _vm.ShowLiveRun();

        // Counted so the window knows there is something to lose on close. A background run lives
        // in a Task owned by this process - closing kills it wherever it happens to be.
        Interlocked.Increment(ref _backgroundRuns);

        _ = Task.Run(async () =>
        {
            try
            {
                var runStore = RunStoreFactory.Create(workspace);
                var contextProvider = new ContextProvider(workspace, new EnvironmentProbe());
                var decisions = new BackgroundDecisionHandler(inbox, workspace);
                var orchestrator = new Orchestrator(
                    _providerFactory, _modelResolver, _workerProvider, _toolRegistry, new DiskArtifactStore(workspace),
                    workspace, _planner, _permissionEngine, decisions, policy, new EmptyProvider(),
                    BuildRouter(), 1, _settings.NumCtx, _settings.DisableThinking, _settings.MaxParallelSteps,
                    _settings.AllowImplicitToolCalls);
                var recorder = new RunRecorder(runStore, MemoryStoreFactory.Create(workspace), workspace.Id, runSettings);
                var context = await contextProvider.BuildAsync(new IntentFocus(workspace.Id), CancellationToken.None);
                var intent = new Intent(Guid.NewGuid(), text, IntentSource.Inbox, context, DateTimeOffset.UtcNow, workerId);
                var recorded = recorder.RecordAsync(
                    orchestrator.SubmitIntentAsync(intent, CancellationToken.None).TeeToLog(_log, CancellationToken.None),
                    CancellationToken.None);
                await BackgroundRunner.RunAsync(recorded, inbox, workspace, text, CancellationToken.None);
            }
            catch (Exception ex)
            {
                await inbox.AppendAsync(new InboxItem(Guid.NewGuid(), workspace.Id, "error", text,
                    "Failed to start: " + ex.Message, Guid.Empty, "unread", DateTimeOffset.UtcNow), CancellationToken.None);
            }
            finally
            {
                Interlocked.Decrement(ref _backgroundRuns);
            }
            Dispatcher.UIThread.Post(RefreshInboxButton);
        });
    }

    private async void RefreshInboxButton()
    {
        try
        {
            var path = _vm.WorkspacePath.Trim();
            if (string.IsNullOrEmpty(path)) { _vm.InboxLabel = "Inbox"; _vm.InboxUnread = 0; return; }
            var items = await InboxStoreFactory.Create(WorkspaceFrom(path)).LoadAllAsync(CancellationToken.None);
            var unread = items.Count(i => string.Equals(i.Status, "unread", StringComparison.OrdinalIgnoreCase));
            _vm.InboxLabel = unread > 0 ? $"Inbox ({unread})" : "Inbox";
            _vm.InboxUnread = unread;
        }
        catch { /* ignore */ }
    }

    private void ShowInbox()
    {
        var path = _vm.WorkspacePath.Trim();
        if (string.IsNullOrEmpty(path))
        {
            ShowViewer("Inbox", "Set a workspace first.");
            return;
        }

        if (_inboxWindow is null)
        {
            var workspace = WorkspaceFrom(path);
            _inboxWindow = new InboxWindow(
                InboxStoreFactory.Create(workspace), RunStoreFactory.Create(workspace), workspace.RootPath);
            // The badge follows the window: reading an item there updates the button here.
            _inboxWindow.UnreadChanged += unread =>
            {
                _vm.InboxLabel = unread > 0 ? $"Inbox ({unread})" : "Inbox";
                _vm.InboxUnread = unread;
            };
            _inboxWindow.Closed += (_, _) => { _inboxWindow = null; RefreshInboxButton(); };
            _inboxWindow.Show(this);
        }

        _inboxWindow.Activate();
    }

    /// <summary>
    /// Builds the read-only view of a stored run. Its artifacts get the same two actions the live
    /// run's do, because they are the same files - what changes is what the second one MEANS. On a
    /// run that just finished, deleting what it wrote is undoing it; on one from last Tuesday it is
    /// deleting a file that has had a week to be edited since, so it is labelled Delete and it
    /// asks.
    /// </summary>
    private PastRunViewModel BuildPastRun(RunRecord record)
    {
        var root = WorkspaceRegistry.Normalise(_vm.WorkspacePath);

        return new PastRunViewModel(
            record,
            item => ShowFile(root, item),
            item => _ = DeletePastArtifactAsync(root, item));
    }

    /// <summary>Opens an artifact in the viewer. Reading is safe at any age.</summary>
    private void ShowFile(string root, ArtifactItemViewModel item)
    {
        try
        {
            var full = Path.Combine(root, item.RelativePath);
            ShowViewer(item.RelativePath, File.Exists(full) ? File.ReadAllText(full) : "(file not found)");
        }
        catch (Exception ex)
        {
            ShowViewer(item.RelativePath, "Error: " + ex.Message);
        }
    }

    /// <summary>
    /// Removes a file the CURRENT run produced. A file the run created is deleted straight away —
    /// that really does undo the write. A file that already existed is only overwritten, and the old
    /// bytes are gone, so deleting it destroys the user's content: that path asks first and says so.
    /// </summary>
    private async Task RemoveLiveArtifactAsync(string root, ArtifactItemViewModel item, bool createdByThisRun)
    {
        var full = Path.Combine(root, item.RelativePath);
        if (!File.Exists(full))
        {
            item.Status = "already gone";
            item.CanAct = false;
            return;
        }

        if (!createdByThisRun)
        {
            var go = await ConfirmWindow.AskAsync(
                this,
                $"Delete “{item.RelativePath}”?",
                "This file existed before the run and was overwritten, so deleting it does NOT restore "
                + "the previous version - that content was not kept. The file will simply be gone.",
                "Delete",
                "Keep");

            if (!go)
                return;
        }

        try
        {
            File.Delete(full);
            item.Status = createdByThisRun ? "deleted (was created by this run)" : "deleted";
            item.CanAct = false;
        }
        catch (Exception ex)
        {
            item.Status = "error: " + ex.Message;
        }
    }

    private async Task DeletePastArtifactAsync(string root, ArtifactItemViewModel item)
    {
        var full = Path.Combine(root, item.RelativePath);
        if (!File.Exists(full))
        {
            item.Status = "already gone";
            item.CanAct = false;
            return;
        }

        // The file's own date is the whole point of asking: a week of edits does not show up in a
        // run record, and this deletes what is on disk NOW, not what the run wrote.
        var changed = File.GetLastWriteTime(full).ToString("yyyy-MM-dd HH:mm");
        var go = await ConfirmWindow.AskAsync(
            this,
            $"Delete \u201c{item.RelativePath}\u201d?",
            $"This deletes the file as it is on disk now, last changed {changed} - not the version this run wrote. "
            + "There is no undo.",
            "Delete",
            "Keep");

        if (!go)
            return;

        try
        {
            File.Delete(full);
            item.Status = "deleted";
            item.CanAct = false;
        }
        catch (Exception ex)
        {
            item.Status = "error: " + ex.Message;
        }
    }

    /// <summary>
    /// Loads the workspace's runs for the context column. The store type is the environment's
    /// choice (SQLite, MySQL or files), which is why the list does not create one itself.
    /// </summary>
    private async Task LoadRunsAsync()
    {
        var path = _vm.WorkspacePath.Trim();
        if (string.IsNullOrEmpty(path))
        {
            _vm.Runs.Show(Array.Empty<RunRecord>());
            return;
        }

        try
        {
            var store = RunStoreFactory.Create(WorkspaceFrom(path));
            var records = await store.LoadAllAsync(CancellationToken.None);
            _vm.Runs.Show(records);
        }
        catch (Exception ex)
        {
            // A workspace whose store is unreachable is a fact to show, not a crash: the rest of
            // the window is still perfectly usable.
            _vm.Runs.Fail(ex.Message);
        }
    }

    private async Task ShowTimelineAsync()
    {
        var workspacePath = _vm.WorkspacePath.Trim();
        if (string.IsNullOrEmpty(workspacePath))
            return;

        var workspace = WorkspaceInfo.For(workspacePath);
        var runStore = RunStoreFactory.Create(workspace);
        var memory = MemoryStoreFactory.Create(workspace);
        var runs = await runStore.LoadAllAsync(CancellationToken.None);
        var entries = await memory.LoadAllAsync(CancellationToken.None);
        ShowViewer("Project Memory", ProjectMemory.Render(runs, entries, workspace.RootPath));
    }

    // ── IDecisionHandler: inline approval card ───────────────────────────────
    public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
    {
        // Already approved for this session or this workspace? Allow silently — no click needed.
        if (!string.IsNullOrEmpty(request.Subject)
            && (_sessionApprovals.Contains(request.Subject) || WorkspaceApproves(request.Subject)))
            return Task.FromResult(new DecisionOutcome(AllowOptionId(request)));

        var tcs = new TaskCompletionSource<DecisionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled());

        Dispatcher.UIThread.Post(() =>
        {
            _pendingDecision = tcs;
            _vm.DecisionText = request.Topic;
            // The full action, not the summary: this is what the click authorises.
            _vm.DecisionDetail = request.FullText;

            _vm.DecisionOptions.Clear();
            foreach (var option in request.Options)
            {
                var captured = option;
                _vm.DecisionOptions.Add(new DecisionOptionViewModel(captured.Label, () => ResolveDecision(captured.Id)));
            }

            // Remember-this-approval shortcuts, so the user is not clicking Allow for every command.
            if (!string.IsNullOrEmpty(request.Subject))
            {
                var subject = request.Subject;
                var allowId = AllowOptionId(request);
                _vm.DecisionOptions.Add(new DecisionOptionViewModel(
                    "Allow (session)", () => { _sessionApprovals.Add(subject); ResolveDecision(allowId); }));
                _vm.DecisionOptions.Add(new DecisionOptionViewModel(
                    "Allow (workspace)", () => { SaveWorkspaceApproval(subject); ResolveDecision(allowId); }));
            }

            _vm.IsDecisionVisible = true;
        });

        return tcs.Task;
    }

    private void ResolveDecision(string optionId)
    {
        _vm.IsDecisionVisible = false;
        var tcs = _pendingDecision;
        _pendingDecision = null;
        tcs?.TrySetResult(new DecisionOutcome(optionId));
    }

    private static string AllowOptionId(DecisionRequest request)
        => request.RecommendedOptionId
        ?? request.Options.FirstOrDefault(o => o.Id.Contains("allow", StringComparison.OrdinalIgnoreCase))?.Id
        ?? request.Options.FirstOrDefault()?.Id
        ?? "allow";

    // Workspace-scoped approvals live OUTSIDE the workspace now — see ApprovalStore for why. The
    // old <workspace>/.enactive/permissions.json is ignored, not imported.
    private bool WorkspaceApproves(string tool)
    {
        if (string.IsNullOrEmpty(_currentWorkspaceRoot))
            return false;

        return ApprovalStore.Approves(WorkspaceInfo.IdFor(_currentWorkspaceRoot), tool);
    }

    private void SaveWorkspaceApproval(string tool)
    {
        if (string.IsNullOrEmpty(_currentWorkspaceRoot))
            return;

        ApprovalStore.Approve(WorkspaceInfo.IdFor(_currentWorkspaceRoot), tool);
    }

    /// <summary>
    /// Says once per workspace that an old in-folder approvals file is being ignored, so being asked
    /// again looks like the deliberate change it is rather than a bug.
    /// </summary>
    private void NoteLegacyApprovalsIfAny(string root)
    {
        if (!_legacyApprovalsNoted.Add(root) || !ApprovalStore.HasLegacyFile(root))
            return;

        _log.Info(LogSource.System,
            $"Ignoring {WorkspaceGuard.ReservedFolder}/permissions.json in {root}: remembered approvals now "
            + "live outside the workspace, where a tool cannot write them. Approve again to restore them.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    /// <summary>Shows a report, a file or a diff. Owned by this window, so it does not outlive it
    /// or get lost behind it.</summary>
    private void ShowViewer(string title, string content) => ViewerWindow.Show(this, title, content);

    /// <summary>Window / taskbar icon (the Loop mark on an ember tile). Best-effort: a missing or
    /// unreadable asset must never stop the app from starting.</summary>
    private void TrySetIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://enactive-ui/Assets/icon.png"));
            Icon = new WindowIcon(stream);
        }
        catch
        {
            // no icon; not worth a crash
        }
    }

    private void SaveWindowBounds()
    {
        if (WindowState != WindowState.Normal)
            return;
        _settings.WindowX = Position.X;
        _settings.WindowY = Position.Y;
        _settings.WindowWidth = (int)Width;
        _settings.WindowHeight = (int)Height;
        _settings.Save();
    }

    private void ApplySettings()
    {
        _globalInstructions = _settings.GlobalInstructions;

        // Providers: build the factory from the whole universal list.
        var descriptors = _settings.Providers.Select(p => new ProviderDescriptor(
            p.Id,
            string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName,
            p.Kind,
            p.BaseUrl,
            string.IsNullOrEmpty(p.ApiKey) ? null : p.ApiKey,
            p.Models,
            p.Headers.Count > 0 ? p.Headers : null,
            p.MaxTokens)).ToList();
        _providerFactory = new ChatProviderFactory(descriptors, _http, _log);

        // Workers: the editable team, each with its own model; honesty + global instructions applied at build.
        var fallbackModel = _settings.Providers.Count > 0 && _settings.Providers[0].Models.Count > 0
            ? new ModelRef(_settings.Providers[0].Id, _settings.Providers[0].Models[0])
            : new ModelRef("ollama", "qwen2.5-coder");
        var workers = _settings.Workers.Select(w => new Worker(
            w.Id,
            w.Role,
            DefaultWorkers.Augment(w.Instructions, _globalInstructions, _settings.VerifyWrites),
            w.Tools,
            w.Level,
            new ModelPolicy(AppSettings.ParseRef(w.Model) ?? fallbackModel, AppSettings.ParseRef(w.Fallback)))).ToList();
        if (workers.Count == 0)
            workers = DefaultWorkers.Build(fallbackModel, _globalInstructions, _settings.VerifyWrites).ToList();
        var defaultId = workers.Any(w => w.Id == DefaultWorkers.DefaultId) ? DefaultWorkers.DefaultId : workers[0].Id;
        _workerProvider = new StaticWorkerProvider(workers, defaultId);

        // Refresh the role picker; settings can be re-applied after Save.
        // Rebuilding the role list is not the user choosing a role, so it must not be written back
        // to the workspace as if it were.
        _vm.IsLoadingWorkspaceDefaults = true;
        try
        {
            var keep = _vm.SelectedWorkerIndex;
            _vm.WorkerRoles.Clear();
            foreach (var worker in _workerProvider.All)
                _vm.WorkerRoles.Add(worker.Role);
            _vm.SelectedWorkerIndex = keep >= 0 && keep < _vm.WorkerRoles.Count ? keep : 0;
        }
        finally
        {
            _vm.IsLoadingWorkspaceDefaults = false;
        }

        _model = _workerProvider.Default.ModelPolicy.Preferred.Model;
        _vm.ModelLabel = $"model: {_model}";
    }

    private static PermissionPolicy PolicyFor(int level) => level switch
    {
        0 => new PermissionPolicy(PermissionLevel.Observe, new[] { "*" }, Array.Empty<string>()),
        1 => new PermissionPolicy(PermissionLevel.Suggest, new[] { "*" }, Array.Empty<string>()),
        2 => new PermissionPolicy(PermissionLevel.Execute, new[] { "*" }, new[] { "run_command", "run_powershell", "git", "docker" }),
        _ => new PermissionPolicy(PermissionLevel.Autonomous, new[] { "*" }, Array.Empty<string>())
    };

    private sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
