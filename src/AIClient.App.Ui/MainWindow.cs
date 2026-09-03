using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AIClient.Agents;
using AIClient.Core.Artifacts;
using AIClient.Core.Context;
using AIClient.Core.Diagnostics;
using AIClient.Core.Events;
using AIClient.Core.Inbox;
using AIClient.Core.Intents;
using AIClient.Core.Permissions;
using AIClient.Core.Providers;
using AIClient.Core.Tools;
using AIClient.Core.Workers;
using AIClient.Providers;
using AIClient.Tools;
using AIClient.Workspace;

namespace AIClient.App.Ui;

/// <summary>Three-panel AIClient shell: Workspace + autonomy (left) · plan-step feed (center) ·
/// AI status + decision card + artifacts (right). Reuses the engine unchanged.</summary>
public sealed class MainWindow : Window, IDecisionHandler
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
    private readonly EnvironmentProbe _envProbe = new();
    private readonly TextBlock _envSummary = new() { Text = "", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#9a9a9a")) };
    private ComboBox _workerBox = null!;
    private readonly Planner _planner = new();
    private readonly ModelResolver _modelResolver = new();
    private StaticWorkerProvider _workerProvider = null!;
    private readonly PermissionEngine _permissionEngine = new();
    private AppSettings _settings = new();
    private TextBlock? _modelLabel;

    // ── Controls ─────────────────────────────────────────────────────────────
    private readonly TextBox _workspaceBox;
    private readonly Slider _autonomySlider;
    private readonly TextBlock _autonomyLabel;
    private readonly CheckBox _stageBox;
    private readonly CheckBox _backgroundBox;
    private Button _inboxButton = null!;
    private readonly TextBox _inputBox;
    private readonly Button _runButton;
    private readonly Button _stopButton;
    private readonly StackPanel _stepsPanel;
    private readonly ScrollViewer _stepsScroll;
    private readonly TextBlock _statusPhase;
    private readonly TextBlock _statusProgress;
    private readonly TextBlock _statusElapsed;
    private readonly TextBlock _currentAction;
    private readonly Border _agentPill;
    private readonly TextBlock _agentBadge;
    private readonly Border _decisionPanel;
    private readonly TextBlock _decisionText;
    private readonly StackPanel _decisionButtons;
    private readonly StackPanel _artifactsPanel;
    private readonly HashSet<string> _shownArtifacts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListBox _workspacesList;
    private readonly ObservableCollection<string> _recentWorkspaces = new();

    // ── Run state ────────────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<DecisionOutcome>? _pendingDecision;
    private readonly HashSet<string> _sessionApprovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StepCard> _cards = new();
    private StepCard? _currentCard;
    private int _stepIndex;
    private int _doneSteps;
    private int _totalSteps;
    private readonly Stopwatch _runStopwatch = new();
    private DispatcherTimer? _elapsedTimer;
    private string _currentWorkspaceRoot = string.Empty;
    private StagingArtifactStore? _staging;
    private int _stagedShown;

    public MainWindow()
    {
        Title = "AIClient";
        Width = 1080;
        Height = 720;

        _settings = AppSettings.Load();
        if (_settings.WindowWidth > 300 && _settings.WindowHeight > 300)
        {
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(_settings.WindowX, _settings.WindowY);
        }
        Closing += (_, _) => { SaveWindowBounds(); _log.Dispose(); };

        _toolRegistry = new LoggingToolRegistry(new ToolRegistry(new ITool[]
        {
            new WriteFileTool(), new ReadFileTool(), new ListDirectoryTool(), new RunCommandTool(), new RunPowerShellTool(), new GitTool(), new DockerTool()
        }), _log);
        ApplySettings();
        _log.Info(LogSource.System, $"AIClient UI started — logs at {FileLogSink.DefaultDirectory()}");

        // ── Top command bar ──────────────────────────────────────────────────
        _inputBox = new TextBox
        {
            Watermark = "What do you want to accomplish?",
            AcceptsReturn = true,
            MinHeight = 56,
            TextWrapping = TextWrapping.Wrap
        };
        _runButton = new Button { Content = "Run", Width = 72 };
        _stopButton = new Button { Content = "Stop", Width = 72, IsEnabled = false };
        _runButton.Click += (_, _) => _ = RunAsync();
        _stopButton.Click += (_, _) => _cts?.Cancel();

        var topButtons = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _runButton, _stopButton }
        };
        DockPanel.SetDock(topButtons, Dock.Right);
        var inputRow = new DockPanel { LastChildFill = true, Children = { topButtons, _inputBox } };

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var verb in new[] { "Fix", "Build", "Investigate", "Explain", "Refactor" })
            chips.Children.Add(VerbChip(verb));
        chips.Children.Add(new TextBlock
        {
            Text = "Ctrl+K to focus · Enter to run · Ctrl+Enter = newline",
            Foreground = Brushes.Gray,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        });

        var topBar = new StackPanel
        {
            Margin = new Thickness(12, 12, 12, 6),
            Spacing = 6,
            Children = { chips, inputRow }
        };
        DockPanel.SetDock(topBar, Dock.Top);

        // Tunnel so we see the keys before the TextBox consumes Enter.
        AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // ── Left panel: workspace + autonomy + timeline ──────────────────────
        _workspaceBox = new TextBox
        {
            Text = Environment.GetEnvironmentVariable("AICLIENT_WORKSPACE") ?? Directory.GetCurrentDirectory(),
            Watermark = "Workspace folder",
            TextWrapping = TextWrapping.Wrap
        };
        _autonomySlider = new Slider
        {
            Minimum = 0, Maximum = 3, Value = 2,
            TickFrequency = 1, IsSnapToTickEnabled = true
        };
        _autonomySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                UpdateAutonomyLabel();
        };
        _autonomyLabel = new TextBlock { Foreground = Brushes.Gray, FontSize = 12 };
        UpdateAutonomyLabel();

        _stageBox = new CheckBox { Content = "Stage changes (review before apply)", IsChecked = false };
        _backgroundBox = new CheckBox { Content = "Run in background (Inbox)", IsChecked = false };

        var timelineButton = new Button { Content = "Timeline", HorizontalAlignment = HorizontalAlignment.Stretch };
        timelineButton.Click += (_, _) => _ = ShowTimelineAsync();

        var logButton = new Button { Content = "Log", HorizontalAlignment = HorizontalAlignment.Stretch };
        logButton.Click += (_, _) => ShowLogWindow();

        var envButton = new Button { Content = "Env", HorizontalAlignment = HorizontalAlignment.Stretch };
        envButton.Click += (_, _) => _ = ShowEnvironmentAsync();

        _workerBox = new ComboBox
        {
            ItemsSource = _workerProvider.All.Select(w => w.Role).ToArray(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _inboxButton = new Button { Content = "Inbox", HorizontalAlignment = HorizontalAlignment.Stretch };
        _inboxButton.Click += (_, _) => _ = ShowInboxAsync();

        var settingsButton = new Button { Content = "Settings", HorizontalAlignment = HorizontalAlignment.Stretch };
        settingsButton.Click += (_, _) =>
        {
            // SettingsWindow reads the live settings and mutates them only when Save is clicked (Cancel/close
            // leave them untouched), so we can hand it _settings directly instead of a partial copy.
            new SettingsWindow(_settings, saved => { _settings = saved; saved.Save(); ApplySettings(); }).Show(this);
        };

        LoadRecents();
        _workspacesList = new ListBox { ItemsSource = _recentWorkspaces, MaxHeight = 150 };
        _workspacesList.SelectionChanged += (_, _) =>
        {
            if (_workspacesList.SelectedItem is string path && !string.IsNullOrEmpty(path))
                _workspaceBox.Text = path;
        };

        var leftPanel = new StackPanel
        {
            Margin = new Thickness(12, 6, 6, 12),
            Spacing = 8,
            Children =
            {
                Header("WORKSPACE"),
                _workspaceBox,
                (_modelLabel = new TextBlock { Text = $"model: {_model}", Foreground = Brushes.Gray, FontSize = 11 }),
                Header("RECENT"),
                _workspacesList,
                Header("AUTONOMY"),
                _autonomyLabel,
                _autonomySlider,
                _stageBox,
                _backgroundBox,
                new Border { Height = 8 },
                new TextBlock { Text = "Role", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#808080")) },
                _workerBox,
                new Border { Height = 6 },
                timelineButton,
                logButton,
                envButton,
                _inboxButton,
                settingsButton,
                new Border { Height = 10 },
                new TextBlock { Text = "Environment", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#808080")) },
                _envSummary
            }
        };
        Grid.SetColumn(leftPanel, 0);

        // ── Center: plan-step feed ───────────────────────────────────────────
        _stepsPanel = new StackPanel { Margin = new Thickness(6, 6, 6, 12) };
        _stepsScroll = new ScrollViewer
        {
            Content = _stepsPanel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        Grid.SetColumn(_stepsScroll, 1);

        // ── Right panel: AI status + decision + artifacts ────────────────────
        _statusPhase = new TextBlock { Text = "Idle", FontSize = 18, FontWeight = FontWeight.SemiBold };
        _statusProgress = new TextBlock { Text = "—", Foreground = Brushes.Gray, FontSize = 12 };
        _statusElapsed = new TextBlock { Text = string.Empty, Foreground = Brushes.Gray, FontSize = 12 };
        _currentAction = new TextBlock { Text = "", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#b0b0b0")) };

        _agentBadge = new TextBlock { Text = string.Empty, FontSize = 11, Foreground = Brushes.White };
        _agentPill = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#333333")),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 2, 10, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = false,
            Child = _agentBadge
        };

        _decisionText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
        _decisionButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        _decisionPanel = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#3a2f00")),
            BorderBrush = new SolidColorBrush(Color.Parse("#c9a227")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 8, 0, 8),
            IsVisible = false,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "USER DECISION REQUIRED", FontWeight = FontWeight.Bold },
                    _decisionText,
                    _decisionButtons
                }
            }
        };

        _artifactsPanel = new StackPanel();
        var artifactsScroll = new ScrollViewer { Content = _artifactsPanel };

        var rightPanel = new DockPanel { Margin = new Thickness(6, 6, 12, 12) };
        var rightTop = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Header("AI STATUS"),
                _statusPhase,
                _statusProgress,
                _statusElapsed,
                _agentPill,
                new TextBlock { Text = "Current action", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) },
                _currentAction,
                _decisionPanel,
                Header("ARTIFACTS")
            }
        };
        DockPanel.SetDock(rightTop, Dock.Top);
        rightPanel.Children.Add(rightTop);
        rightPanel.Children.Add(artifactsScroll);
        Grid.SetColumn(rightPanel, 2);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*,320") };
        grid.Children.Add(leftPanel);
        grid.Children.Add(_stepsScroll);
        grid.Children.Add(rightPanel);

        Content = new DockPanel { LastChildFill = true, Children = { topBar, grid } };
    }

    // ── Run an intent ────────────────────────────────────────────────────────
    private async Task RunAsync()
    {
        if (_pendingDecision is not null)
            return;

        var text = _inputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        var workspacePath = _workspaceBox.Text?.Trim();
        if (string.IsNullOrEmpty(workspacePath))
            return;

        try { Directory.CreateDirectory(workspacePath); }
        catch (Exception ex) { _statusPhase.Text = "Error"; _currentAction.Text = ex.Message; return; }

        // Background: fire the run headless (results land in the Inbox) and keep the UI free.
        if (_backgroundBox.IsChecked == true)
        {
            StartBackground(text, Path.GetFullPath(workspacePath));
            return;
        }

        // reset run state / panels
        _inputBox.Text = string.Empty;
        _stepsPanel.Children.Clear();
        _artifactsPanel.Children.Clear();
        _shownArtifacts.Clear();
        _cards.Clear();
        _currentCard = null;
        _stepIndex = 0;
        _doneSteps = 0;
        _totalSteps = 0;
        _decisionPanel.IsVisible = false;
        _agentPill.IsVisible = false;
        _statusPhase.Text = "Running";
        _statusProgress.Text = "—";
        _statusElapsed.Text = string.Empty;
        _currentAction.Text = string.Empty;
        SetBusy(true);
        _elapsedTimer?.Stop();
        _runStopwatch.Restart();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => _statusElapsed.Text = $"Elapsed: {FormatElapsed(_runStopwatch.Elapsed)}";
        _elapsedTimer.Start();

        _cts = new CancellationTokenSource();

        var fullPath = Path.GetFullPath(workspacePath);
        _currentWorkspaceRoot = fullPath;
        AddRecent(fullPath);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        if (string.IsNullOrEmpty(name))
            name = "workspace";

        var workspace = new WorkspaceInfo(Guid.NewGuid(), name, fullPath);
        var policy = PolicyFor((int)Math.Round(_autonomySlider.Value));

        IArtifactStore artifactStore;
        if (_stageBox.IsChecked == true)
        {
            var staging = new StagingArtifactStore(fullPath);
            _staging = staging;
            artifactStore = staging;
        }
        else
        {
            _staging = null;
            artifactStore = new DiskArtifactStore(workspace);
        }
        _stagedShown = 0;

        try
        {
            var runStore = RunStoreFactory.Create(workspace);
            var contextProvider = new ContextProvider(workspace, new EnvironmentProbe());
            var orchestrator = new Orchestrator(
                _providerFactory, _modelResolver, _workerProvider, _toolRegistry, artifactStore,
                workspace, _planner, _permissionEngine, this, policy, new EmptyProvider(),
                BuildRouter(), 1, _settings.NumCtx, _settings.DisableThinking);
            var recorder = new RunRecorder(runStore, new JsonMemoryStore(workspace), workspace.Id);

            var context = await contextProvider.BuildAsync(new IntentFocus(workspace.Id), _cts.Token);
            var workerId = _workerProvider.All.Count > 0
                && _workerBox.SelectedIndex >= 0 && _workerBox.SelectedIndex < _workerProvider.All.Count
                ? _workerProvider.All[_workerBox.SelectedIndex].Id : null;
            var intent = new Intent(Guid.NewGuid(), text, IntentSource.CommandBar, context, DateTimeOffset.UtcNow, workerId);
            var envLine = context.Environment?.OneLine();
            Dispatcher.UIThread.Post(() => _envSummary.Text = envLine ?? "(no environment data)");

            await Task.Run(async () =>
            {
                await foreach (var ev in recorder.RecordAsync(orchestrator.SubmitIntentAsync(intent, _cts.Token).TeeToLog(_log, _cts.Token), _cts.Token))
                    RenderEvent(ev);
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => { _statusPhase.Text = "Cancelled"; _currentCard?.SetFailed(); });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => { _statusPhase.Text = "Error"; _currentAction.Text = ex.Message; _currentCard?.SetFailed(); });
        }
        finally
        {
            _elapsedTimer?.Stop();
            _elapsedTimer = null;
            _runStopwatch.Stop();
            var finalElapsed = FormatElapsed(_runStopwatch.Elapsed);
            Dispatcher.UIThread.Post(() => _statusElapsed.Text = $"Elapsed: {finalElapsed}");
            SetBusy(false);
            _cts = null;
        }
    }

    // ── Map events to the three panels (always on the UI thread) ─────────────
    private void RenderEvent(WorkEvent ev)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (ev.Kind)
            {
                case EventKind.IntentReceived:
                    _statusPhase.Text = "Understanding";
                    break;
                case EventKind.Routed:
                    if (ev.Summary.Contains("-> model"))
                        _statusPhase.Text = "Planning";
                    if (ev.Summary.StartsWith("Reasoner", StringComparison.Ordinal))
                        SetAgent("Reasoner · planning", "#3a6ea5");
                    break;
                case EventKind.PlanCreated:
                    _statusPhase.Text = "Executing";
                    CreateStepCards(ev.Summary);
                    break;
                case EventKind.StepStarted:
                    SetAgent("Coder", "#3a7d44");
                    BeginStep(ev.Summary);
                    EnsureCurrentCard().SetActivity("Thinking…");
                    break;
                case EventKind.StepCompleted:
                    _currentCard?.SetDone();
                    _currentCard?.SetActivity("Done");
                    _doneSteps++;
                    UpdateProgress();
                    break;
                case EventKind.AssistantDelta:
                    SetAgent("Coder", "#3a7d44");
                    var streamCard = EnsureCurrentCard();
                    // Buffered, not shown live - the raw streamed reply isn't interesting on its own;
                    // it gets folded into one short note the next time a tool runs or the step ends.
                    streamCard.AppendAssistantText(ev.Summary);
                    streamCard.SetActivity("Thinking…");
                    break;
                case EventKind.ToolInvoked:
                    SetAgent("Coder", "#3a7d44");
                    _currentAction.Text = ev.Summary;
                    var toolCard = EnsureCurrentCard();
                    LogToolInvocation(toolCard, ev.Summary);
                    toolCard.SetActivity(DescribeToolActivity(ev.Summary));
                    break;
                case EventKind.ToolResult:
                    EnsureCurrentCard().AppendEntryDetail(ev.Summary);
                    break;
                case EventKind.ErrorObserved:
                    // Something needs the user's eyes - a recovered implicit tool call, a stalled
                    // segment, ... - so this card does not stay collapsed like routine progress does.
                    var warnCard = EnsureCurrentCard();
                    warnCard.AddNote("⚠ " + ev.Summary);
                    warnCard.SetActivity("⚠ " + ev.Summary);
                    warnCard.ExpandForAttention();
                    break;
                case EventKind.ReviewRequested:
                case EventKind.ReviewPassed:
                case EventKind.ReviewFailed:
                    SetAgent("Reasoner · review", "#3a6ea5");
                    _currentAction.Text = ev.Summary;
                    var reviewCard = EnsureCurrentCard();
                    reviewCard.AddNote(ev.Summary);
                    reviewCard.SetActivity(
                        ev.Kind == EventKind.ReviewRequested ? "Reviewing…" :
                        ev.Kind == EventKind.ReviewPassed ? "Review passed" : "Review flagged an issue…");
                    break;
                case EventKind.DecisionRequested:
                case EventKind.DecisionResolved:
                    _currentAction.Text = ev.Summary;
                    var decisionCard = EnsureCurrentCard();
                    decisionCard.AddNote(ev.Summary);
                    if (ev.Kind == EventKind.DecisionRequested)
                    {
                        decisionCard.SetActivity("Waiting for your approval…");
                        decisionCard.ExpandForAttention();
                    }
                    break;
                case EventKind.ArtifactProduced:
                    EnsureCurrentCard().AddNote("Artifact: " + ev.Summary);
                    if (_staging is not null)
                        AddStagedArtifact();
                    else
                        AddArtifact(ev.Summary);
                    break;
                case EventKind.TaskCompleted:
                    _statusPhase.Text = "Completed";
                    _currentAction.Text = string.Empty;
                    _currentCard?.SetDone();
                    _currentCard?.SetActivity("Done");
                    _agentPill.IsVisible = false;
                    break;
                case EventKind.TaskFailed:
                    _statusPhase.Text = "Failed";
                    _currentCard?.SetFailed();
                    _currentCard?.SetActivity("Failed");
                    break;
            }
        });
    }

    private static string DescribeToolActivity(string toolInvokedSummary)
    {
        var spaceIndex = toolInvokedSummary.IndexOf(' ');
        var name = spaceIndex > 0 ? toolInvokedSummary[..spaceIndex] : toolInvokedSummary;
        var argsJson = spaceIndex > 0 ? toolInvokedSummary[(spaceIndex + 1)..] : string.Empty;

        string? Hint(string key)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                    return v.GetString();
            }
            catch { /* best-effort only - a truncated/compacted arg preview may not parse */ }
            return null;
        }

        return name switch
        {
            "write_file" => Hint("path") is { } p ? $"Writing {p}…" : "Writing a file…",
            "read_file" => Hint("path") is { } p ? $"Reading {p}…" : "Reading a file…",
            "list_dir" => Hint("path") is { } p ? $"Listing {p}…" : "Listing files…",
            "run_command" => Hint("command") is { } c
                ? $"Running: {(c.Length <= 60 ? c : c[..60] + "…")}"
                : "Running a command…",
            _ => $"Running {name}…"
        };
    }

    /// <summary>Routes a "{tool_name} {argsJson}" ToolInvoked summary into the card's structured
    /// action log (a command line, a file operation, or a generic tool entry) instead of a raw
    /// transcript dump - this is what the collapsed "Used N tools..." summary counts.</summary>
    private static void LogToolInvocation(StepCard card, string toolInvokedSummary)
    {
        var spaceIndex = toolInvokedSummary.IndexOf(' ');
        var name = spaceIndex > 0 ? toolInvokedSummary[..spaceIndex] : toolInvokedSummary;
        var argsJson = spaceIndex > 0 ? toolInvokedSummary[(spaceIndex + 1)..] : string.Empty;

        string? Hint(string key)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                    return v.GetString();
            }
            catch { /* best-effort only - a truncated/compacted arg preview may not parse */ }
            return null;
        }

        switch (name)
        {
            case "write_file":
                card.AddFileOp("Wrote", Hint("path") ?? "a file");
                break;
            case "read_file":
                card.AddFileOp("Read", Hint("path") ?? "a file");
                break;
            case "list_dir":
                card.AddFileOp("Listed", Hint("path") ?? "a directory");
                break;
            case "run_command":
                card.AddCommand(Hint("command") ?? argsJson);
                break;
            default:
                card.AddGenericTool($"Ran {name}");
                break;
        }
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
            var card = new StepCard(title.Trim());
            _cards.Add(card);
            _stepsPanel.Children.Add(card.Root);
        }
        UpdateProgress();
    }

    private void BeginStep(string summary)
    {
        _stepIndex++;
        StepCard card;
        if (_stepIndex - 1 < _cards.Count)
        {
            card = _cards[_stepIndex - 1];
        }
        else
        {
            card = new StepCard(summary);
            _cards.Add(card);
            _stepsPanel.Children.Add(card.Root);
            _totalSteps = _cards.Count;
        }
        card.SetRunning();
        _currentCard = card;
        _currentAction.Text = summary;
    }

    private StepCard EnsureCurrentCard()
    {
        if (_currentCard is null)
        {
            var card = new StepCard("Working");
            card.SetRunning();
            _cards.Add(card);
            _stepsPanel.Children.Add(card.Root);
            _currentCard = card;
            if (_totalSteps == 0)
                _totalSteps = 1;
        }
        return _currentCard;
    }

    private void UpdateProgress()
        => _statusProgress.Text = _totalSteps > 0 ? $"{_doneSteps} / {_totalSteps} steps" : "—";

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

        // One card per file: repeated writes to the same path update nothing new, so skip duplicates.
        if (!_shownArtifacts.Add(relative))
            return;

        var root = _currentWorkspaceRoot;

        var pathText = new TextBlock
        {
            Text = relative,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 12
        };
        var status = new TextBlock { Text = string.Empty, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(6, 0, 0, 0) };
        var review = new Button { Content = "Review", FontSize = 11, Padding = new Thickness(8, 2, 8, 2) };
        var undo = new Button { Content = "Undo", FontSize = 11, Padding = new Thickness(8, 2, 8, 2) };

        review.Click += (_, _) =>
        {
            try
            {
                var full = Path.Combine(root, relative);
                ShowViewer(relative, File.Exists(full) ? File.ReadAllText(full) : "(file not found)");
            }
            catch (Exception ex) { ShowViewer(relative, "Error: " + ex.Message); }
        };
        undo.Click += (_, _) =>
        {
            try
            {
                var full = Path.Combine(root, relative);
                if (File.Exists(full))
                    File.Delete(full);
                status.Text = "undone";
                review.IsEnabled = false;
                undo.IsEnabled = false;
            }
            catch (Exception ex) { status.Text = "error: " + ex.Message; }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { review, undo, status }
        };
        _artifactsPanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1affffff")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = new StackPanel { Children = { pathText, buttons } }
        });
    }

    private void AddStagedArtifact()
    {
        if (_staging is null || _stagedShown >= _staging.Changes.Count)
            return;

        var change = _staging.Changes[_stagedShown++];
        var staging = _staging;

        var pathText = new TextBlock
        {
            Text = change.RelativePath,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 12
        };
        var tag = new TextBlock { Text = change.IsNew ? "new file" : "modified", Foreground = Brushes.Gray, FontSize = 11 };
        var status = new TextBlock
        {
            Text = "staged",
            Foreground = new SolidColorBrush(Color.Parse("#c9a227")),
            FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0)
        };
        var diffButton = new Button { Content = "Diff", FontSize = 11, Padding = new Thickness(8, 2, 8, 2) };
        var applyButton = new Button { Content = "Apply", FontSize = 11, Padding = new Thickness(8, 2, 8, 2) };
        var rejectButton = new Button { Content = "Reject", FontSize = 11, Padding = new Thickness(8, 2, 8, 2) };

        var diffView = BuildInlineDiff(change.OldContent, change.NewContent, change.RelativePath);
        diffButton.Click += (_, _) => diffView.IsVisible = !diffView.IsVisible;
        applyButton.Click += (_, _) =>
        {
            staging.Apply(change.Id);
            status.Text = "applied";
            status.Foreground = new SolidColorBrush(Color.Parse("#4caf50"));
            applyButton.IsEnabled = false;
            rejectButton.IsEnabled = false;
        };
        rejectButton.Click += (_, _) =>
        {
            staging.Reject(change.Id);
            status.Text = "rejected";
            status.Foreground = new SolidColorBrush(Color.Parse("#e05555"));
            applyButton.IsEnabled = false;
            rejectButton.IsEnabled = false;
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { tag, status } };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { diffButton, applyButton, rejectButton }
        };
        _artifactsPanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1affffff")),
            BorderBrush = new SolidColorBrush(Color.Parse("#c9a227")),
            BorderThickness = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = new StackPanel { Children = { pathText, header, buttons, diffView } }
        });
    }

    private static Control BuildInlineDiff(string? oldContent, string newContent, string path)
    {
        var lines = new StackPanel();
        var unified = TextDiff.Unified(oldContent, newContent, path).Replace("\r\n", "\n").Split('\n');
        foreach (var line in unified)
        {
            IBrush brush =
                line.StartsWith('+') ? new SolidColorBrush(Color.Parse("#5fd35f")) :
                line.StartsWith('-') ? new SolidColorBrush(Color.Parse("#e86f6f")) :
                line.StartsWith('#') ? Brushes.Gray :
                new SolidColorBrush(Color.Parse("#9a9a9a"));
            lines.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = brush,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                FontSize = 11
            });
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#22000000")),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 6, 0, 0),
            MaxHeight = 220,
            IsVisible = false,
            Child = new ScrollViewer { Content = lines }
        };
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _inputBox.Focus();
            _inputBox.SelectAll();
            return;
        }

        if (e.Key == Key.Enter && _inputBox.IsFocused)
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
                _ = RunAsync();
            }
        }
    }

    private void InsertNewlineIntoInput()
    {
        var text = _inputBox.Text ?? string.Empty;
        var caret = Math.Clamp(_inputBox.CaretIndex, 0, text.Length);
        _inputBox.Text = text[..caret] + "\n" + text[caret..];
        _inputBox.CaretIndex = caret + 1;
    }

    private Button VerbChip(string verb)
    {
        var chip = new Button { Content = verb, FontSize = 11, Padding = new Thickness(8, 2, 8, 2) };
        chip.Click += (_, _) =>
        {
            var current = _inputBox.Text ?? string.Empty;
            _inputBox.Text = string.IsNullOrWhiteSpace(current) ? verb + " " : verb + " " + current;
            _inputBox.Focus();
            _inputBox.CaretIndex = _inputBox.Text.Length;
        };
        return chip;
    }

    private static string RecentsPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIClient", "workspaces.txt");

    private void LoadRecents()
    {
        try
        {
            var file = RecentsPath();
            if (!File.Exists(file))
                return;
            foreach (var line in File.ReadAllLines(file))
                if (!string.IsNullOrWhiteSpace(line))
                    _recentWorkspaces.Add(line.Trim());
        }
        catch { /* ignore */ }
    }

    private void AddRecent(string path)
    {
        for (var i = _recentWorkspaces.Count - 1; i >= 0; i--)
            if (string.Equals(_recentWorkspaces[i], path, StringComparison.OrdinalIgnoreCase))
                _recentWorkspaces.RemoveAt(i);

        _recentWorkspaces.Insert(0, path);
        while (_recentWorkspaces.Count > 12)
            _recentWorkspaces.RemoveAt(_recentWorkspaces.Count - 1);

        try
        {
            var file = RecentsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllLines(file, _recentWorkspaces);
        }
        catch { /* ignore */ }
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
        var root = _workspaceBox.Text;
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
            var ws = new WorkspaceInfo(Guid.NewGuid(), name, full);
            var (info, snapshot) = await _envProbe.ProbeFullAsync(ws, CancellationToken.None);
            _envSummary.Text = info.OneLine();
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

    private static WorkspaceInfo WorkspaceFrom(string path)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
        return new WorkspaceInfo(Guid.NewGuid(), string.IsNullOrEmpty(name) ? "workspace" : name, full);
    }

    private void StartBackground(string text, string fullPath)
    {
        var workspace = WorkspaceFrom(fullPath);
        var policy = PolicyFor((int)Math.Round(_autonomySlider.Value));
        var workerId = _workerProvider.All.Count > 0
            && _workerBox.SelectedIndex >= 0 && _workerBox.SelectedIndex < _workerProvider.All.Count
            ? _workerProvider.All[_workerBox.SelectedIndex].Id : null;
        var inbox = new JsonInboxStore(workspace);
        AddRecent(fullPath);

        _statusPhase.Text = "Background task started";
        _currentAction.Text = text;
        _inputBox.Text = string.Empty;

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
                    BuildRouter(), 1, _settings.NumCtx, _settings.DisableThinking);
                var recorder = new RunRecorder(runStore, new JsonMemoryStore(workspace), workspace.Id);
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
            Dispatcher.UIThread.Post(RefreshInboxButton);
        });
    }

    private async void RefreshInboxButton()
    {
        try
        {
            var path = _workspaceBox.Text?.Trim();
            if (string.IsNullOrEmpty(path)) { _inboxButton.Content = "Inbox"; return; }
            var items = await new JsonInboxStore(WorkspaceFrom(path)).LoadAllAsync(CancellationToken.None);
            var unread = items.Count(i => string.Equals(i.Status, "unread", StringComparison.OrdinalIgnoreCase));
            _inboxButton.Content = unread > 0 ? $"Inbox ({unread})" : "Inbox";
        }
        catch { /* ignore */ }
    }

    private async Task ShowInboxAsync()
    {
        var path = _workspaceBox.Text?.Trim();
        if (string.IsNullOrEmpty(path)) { ShowViewer("Inbox", "Set a workspace first."); return; }
        var store = new JsonInboxStore(WorkspaceFrom(path));
        var items = await store.LoadAllAsync(CancellationToken.None);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"INBOX — {Path.GetFullPath(path)}");
        sb.AppendLine();
        if (items.Count == 0)
            sb.AppendLine("(empty)");
        else
            foreach (var i in items.OrderByDescending(x => x.At))
            {
                var dot = string.Equals(i.Status, "unread", StringComparison.OrdinalIgnoreCase) ? " •" : "";
                sb.AppendLine($"{i.At.ToLocalTime():yyyy-MM-dd HH:mm}  [{i.Kind}]{dot}  {i.Title}");
                sb.AppendLine("    " + i.Summary);
                sb.AppendLine();
            }
        ShowViewer("Inbox", sb.ToString());
        await store.MarkAllReadAsync(CancellationToken.None);
        RefreshInboxButton();
    }

    private async Task ShowTimelineAsync()
    {
        var workspacePath = _workspaceBox.Text?.Trim();
        if (string.IsNullOrEmpty(workspacePath))
            return;

        var workspace = new WorkspaceInfo(Guid.NewGuid(), "workspace", Path.GetFullPath(workspacePath));
        var runStore = RunStoreFactory.Create(workspace);
        var memory = new JsonMemoryStore(workspace);
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
            _decisionText.Text = string.IsNullOrEmpty(request.Detail)
                ? request.Topic
                : request.Topic + "\n" + request.Detail;

            _decisionButtons.Children.Clear();
            foreach (var option in request.Options)
            {
                var captured = option;
                var button = new Button { Content = captured.Label };
                button.Click += (_, _) => ResolveDecision(captured.Id);
                _decisionButtons.Children.Add(button);
            }

            // Remember-this-approval shortcuts so the user isn't clicking Allow for every command.
            if (!string.IsNullOrEmpty(request.Subject))
            {
                var subject = request.Subject;
                var allowId = AllowOptionId(request);

                var sessionBtn = new Button { Content = "Allow (session)" };
                sessionBtn.Click += (_, _) => { _sessionApprovals.Add(subject); ResolveDecision(allowId); };
                _decisionButtons.Children.Add(sessionBtn);

                var workspaceBtn = new Button { Content = "Allow (workspace)" };
                workspaceBtn.Click += (_, _) => { SaveWorkspaceApproval(subject); ResolveDecision(allowId); };
                _decisionButtons.Children.Add(workspaceBtn);
            }
            _decisionPanel.IsVisible = true;
        });

        return tcs.Task;
    }

    private void ResolveDecision(string optionId)
    {
        _decisionPanel.IsVisible = false;
        var tcs = _pendingDecision;
        _pendingDecision = null;
        tcs?.TrySetResult(new DecisionOutcome(optionId));
    }

    private static string AllowOptionId(DecisionRequest request)
        => request.RecommendedOptionId
        ?? request.Options.FirstOrDefault(o => o.Id.Contains("allow", StringComparison.OrdinalIgnoreCase))?.Id
        ?? request.Options.FirstOrDefault()?.Id
        ?? "allow";

    // Workspace-scoped approvals persist in <workspace>/.aiclient/permissions.json (a JSON array of tool names).
    private bool WorkspaceApproves(string tool)
    {
        try { return LoadWorkspaceApprovals(_currentWorkspaceRoot).Contains(tool, StringComparer.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static List<string> LoadWorkspaceApprovals(string root)
    {
        try
        {
            if (string.IsNullOrEmpty(root)) return new List<string>();
            var path = Path.Combine(root, ".aiclient", "permissions.json");
            if (!File.Exists(path)) return new List<string>();
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    private void SaveWorkspaceApproval(string tool)
    {
        try
        {
            var root = _currentWorkspaceRoot;
            if (string.IsNullOrEmpty(root)) return;
            var dir = Path.Combine(root, ".aiclient");
            Directory.CreateDirectory(dir);
            var list = LoadWorkspaceApprovals(root);
            if (!list.Contains(tool, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(tool);
                File.WriteAllText(Path.Combine(dir, "permissions.json"),
                    System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { /* ignore */ }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private void ShowViewer(string title, string content)
    {
        var box = new TextBox
        {
            Text = content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Menlo, monospace")
        };
        new Window { Title = title, Width = 780, Height = 540, Content = box }.Show();
    }

    private void SetBusy(bool busy)
    {
        _runButton.IsEnabled = !busy;
        _stopButton.IsEnabled = busy;
        _inputBox.IsEnabled = !busy;
        _workspaceBox.IsEnabled = !busy;
        _autonomySlider.IsEnabled = !busy;
        _stageBox.IsEnabled = !busy;
    }

    private void SetAgent(string label, string colorHex)
    {
        if (_agentBadge.Text == label && _agentPill.IsVisible)
            return;
        _agentBadge.Text = label;
        _agentPill.Background = new SolidColorBrush(Color.Parse(colorHex));
        _agentPill.IsVisible = true;
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

        // Refresh the role picker if the UI is already built (settings can be re-applied after Save).
        if (_workerBox is not null)
        {
            var keep = _workerBox.SelectedIndex;
            _workerBox.ItemsSource = _workerProvider.All.Select(w => w.Role).ToArray();
            _workerBox.SelectedIndex = keep >= 0 && keep < _workerProvider.All.Count ? keep : 0;
        }

        _model = _workerProvider.Default.ModelPolicy.Preferred.Model;
        if (_modelLabel is not null)
            _modelLabel.Text = $"model: {_model}";
    }

    private void UpdateAutonomyLabel()
        => _autonomyLabel.Text = LevelName((int)Math.Round(_autonomySlider.Value));

    private static string LevelName(int level) => level switch
    {
        0 => "Observe — read only, asks before changes",
        1 => "Suggest — prepares changes, asks to apply",
        2 => "Execute — edits freely, asks before run_command",
        _ => "Autonomous — runs everything without asking"
    };

    private static PermissionPolicy PolicyFor(int level) => level switch
    {
        0 => new PermissionPolicy(PermissionLevel.Observe, new[] { "*" }, Array.Empty<string>()),
        1 => new PermissionPolicy(PermissionLevel.Suggest, new[] { "*" }, Array.Empty<string>()),
        2 => new PermissionPolicy(PermissionLevel.Execute, new[] { "*" }, new[] { "run_command", "run_powershell", "git", "docker" }),
        _ => new PermissionPolicy(PermissionLevel.Autonomous, new[] { "*" }, Array.Empty<string>())
    };

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        FontSize = 11,
        Foreground = Brushes.Gray,
        Margin = new Thickness(0, 8, 0, 2)
    };

    private sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
