using System.Collections.ObjectModel;
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
using AIClient.Core.Events;
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
    private string _baseUrl = "http://localhost:11434/v1";
    private string _globalInstructions = string.Empty;
    private ChatProviderFactory _providerFactory = null!;
    private readonly ToolRegistry _toolRegistry;
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
    private readonly TextBox _inputBox;
    private readonly Button _runButton;
    private readonly Button _stopButton;
    private readonly StackPanel _stepsPanel;
    private readonly ScrollViewer _stepsScroll;
    private readonly TextBlock _statusPhase;
    private readonly TextBlock _statusProgress;
    private readonly TextBlock _currentAction;
    private readonly Border _agentPill;
    private readonly TextBlock _agentBadge;
    private readonly Border _decisionPanel;
    private readonly TextBlock _decisionText;
    private readonly StackPanel _decisionButtons;
    private readonly StackPanel _artifactsPanel;
    private readonly ListBox _workspacesList;
    private readonly ObservableCollection<string> _recentWorkspaces = new();

    // ── Run state ────────────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<DecisionOutcome>? _pendingDecision;
    private readonly List<StepCard> _cards = new();
    private StepCard? _currentCard;
    private int _stepIndex;
    private int _doneSteps;
    private int _totalSteps;
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
        Closing += (_, _) => SaveWindowBounds();

        _toolRegistry = new ToolRegistry(new ITool[]
        {
            new WriteFileTool(), new ReadFileTool(), new ListDirectoryTool(), new RunCommandTool()
        });
        ApplySettings();

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

        var timelineButton = new Button { Content = "Timeline", HorizontalAlignment = HorizontalAlignment.Stretch };
        timelineButton.Click += (_, _) => _ = ShowTimelineAsync();

        var settingsButton = new Button { Content = "Settings", HorizontalAlignment = HorizontalAlignment.Stretch };
        settingsButton.Click += (_, _) =>
        {
            var copy = new AppSettings
            {
                BaseUrl = _settings.BaseUrl,
                Model = _settings.Model,
                GlobalInstructions = _settings.GlobalInstructions,
                MultiAgent = _settings.MultiAgent,
                AnthropicApiKey = _settings.AnthropicApiKey,
                ReasonerModel = _settings.ReasonerModel,
                AnthropicWorkspaceId = _settings.AnthropicWorkspaceId,
                WindowX = _settings.WindowX,
                WindowY = _settings.WindowY,
                WindowWidth = _settings.WindowWidth,
                WindowHeight = _settings.WindowHeight
            };
            new SettingsWindow(copy, saved => { _settings = saved; saved.Save(); ApplySettings(); }).Show(this);
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
                new Border { Height = 8 },
                timelineButton,
                settingsButton
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

        // reset run state / panels
        _inputBox.Text = string.Empty;
        _stepsPanel.Children.Clear();
        _artifactsPanel.Children.Clear();
        _cards.Clear();
        _currentCard = null;
        _stepIndex = 0;
        _doneSteps = 0;
        _totalSteps = 0;
        _decisionPanel.IsVisible = false;
        _agentPill.IsVisible = false;
        _statusPhase.Text = "Running";
        _statusProgress.Text = "—";
        _currentAction.Text = string.Empty;
        SetBusy(true);

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
            var contextProvider = new ContextProvider(workspace);
            IChatProvider? reasoner = null;
            string? reasonerModel = null;
            if (_settings.MultiAgent && !string.IsNullOrWhiteSpace(_settings.AnthropicApiKey))
            {
                reasoner = _providerFactory.Create("anthropic");
                reasonerModel = _settings.ReasonerModel;
            }

            var orchestrator = new Orchestrator(
                _providerFactory, _modelResolver, _workerProvider, _toolRegistry, artifactStore,
                workspace, _planner, _permissionEngine, this, policy, new EmptyProvider(),
                reasoner, reasonerModel, 1);
            var recorder = new RunRecorder(runStore);

            var context = await contextProvider.BuildAsync(new IntentFocus(workspace.Id), _cts.Token);
            var intent = new Intent(Guid.NewGuid(), text, IntentSource.CommandBar, context, DateTimeOffset.UtcNow);

            await Task.Run(async () =>
            {
                await foreach (var ev in recorder.RecordAsync(orchestrator.SubmitIntentAsync(intent, _cts.Token), _cts.Token))
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
                    break;
                case EventKind.StepCompleted:
                    _currentCard?.SetDone();
                    _doneSteps++;
                    UpdateProgress();
                    break;
                case EventKind.AssistantDelta:
                    SetAgent("Coder", "#3a7d44");
                    EnsureCurrentCard().AppendStreaming(ev.Summary);
                    break;
                case EventKind.ToolInvoked:
                    SetAgent("Coder", "#3a7d44");
                    _currentAction.Text = ev.Summary;
                    EnsureCurrentCard().AppendLine("-> " + ev.Summary);
                    break;
                case EventKind.ToolResult:
                case EventKind.ErrorObserved:
                    EnsureCurrentCard().AppendLine(ev.Summary);
                    break;
                case EventKind.ReviewRequested:
                case EventKind.ReviewPassed:
                case EventKind.ReviewFailed:
                    SetAgent("Reasoner · review", "#3a6ea5");
                    _currentAction.Text = ev.Summary;
                    EnsureCurrentCard().AppendLine(ev.Summary);
                    break;
                case EventKind.DecisionRequested:
                case EventKind.DecisionResolved:
                    _currentAction.Text = ev.Summary;
                    EnsureCurrentCard().AppendLine(ev.Summary);
                    break;
                case EventKind.ArtifactProduced:
                    EnsureCurrentCard().AppendLine(ev.Summary);
                    if (_staging is not null)
                        AddStagedArtifact();
                    else
                        AddArtifact(ev.Summary);
                    break;
                case EventKind.TaskCompleted:
                    _statusPhase.Text = "Completed";
                    _currentAction.Text = string.Empty;
                    _currentCard?.SetDone();
                    _agentPill.IsVisible = false;
                    break;
                case EventKind.TaskFailed:
                    _statusPhase.Text = "Failed";
                    _currentCard?.SetFailed();
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

    private void AddArtifact(string summary)
    {
        var relative = summary.Contains(": ", StringComparison.Ordinal)
            ? summary[(summary.IndexOf(": ", StringComparison.Ordinal) + 2)..]
            : summary;
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

    private async Task ShowTimelineAsync()
    {
        var workspacePath = _workspaceBox.Text?.Trim();
        if (string.IsNullOrEmpty(workspacePath))
            return;

        var workspace = new WorkspaceInfo(Guid.NewGuid(), "workspace", Path.GetFullPath(workspacePath));
        var runStore = RunStoreFactory.Create(workspace);
        var runs = await runStore.LoadAllAsync(CancellationToken.None);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"PROJECT TIMELINE — {workspace.RootPath}");
        sb.AppendLine();
        if (runs.Count == 0)
        {
            sb.AppendLine("(no runs yet)");
        }
        else
        {
            string? currentDay = null;
            foreach (var run in runs)
            {
                var day = run.StartedAt.ToLocalTime().ToString("yyyy-MM-dd");
                if (day != currentDay) { sb.AppendLine(day); currentDay = day; }
                sb.AppendLine($"  {run.StartedAt.ToLocalTime():HH:mm}  {run.Status,-10} {run.Title}");
                if (run.Artifacts.Count > 0)
                    sb.AppendLine($"        artifacts: {string.Join(", ", run.Artifacts)}");
                if (run.Decisions.Count > 0)
                    sb.AppendLine($"        decisions: {string.Join("; ", run.Decisions)}");
            }
        }
        ShowViewer("Timeline", sb.ToString());
    }

    // ── IDecisionHandler: inline approval card ───────────────────────────────
    public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
    {
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
        _baseUrl = _settings.BaseUrl;
        _model = _settings.Model;
        _globalInstructions = _settings.GlobalInstructions;

        var descriptors = new List<ProviderDescriptor>
        {
            new("ollama", "Ollama (local)", ProviderKind.OpenAiCompatible, _baseUrl, null, new[] { _model })
        };
        if (_settings.MultiAgent && !string.IsNullOrWhiteSpace(_settings.AnthropicApiKey))
        {
            IReadOnlyDictionary<string, string>? headers = string.IsNullOrWhiteSpace(_settings.AnthropicWorkspaceId)
                ? null
                : new Dictionary<string, string> { ["anthropic-workspace-id"] = _settings.AnthropicWorkspaceId };
            descriptors.Add(new ProviderDescriptor(
                "anthropic", "Anthropic", ProviderKind.Anthropic, "https://api.anthropic.com",
                _settings.AnthropicApiKey, new[] { _settings.ReasonerModel }, headers));
        }
        _providerFactory = new ChatProviderFactory(descriptors, _http);

        var instructions = string.IsNullOrWhiteSpace(_globalInstructions)
            ? DeveloperInstructions
            : DeveloperInstructions + "\n\n## Global instructions (apply to every run)\n" + _globalInstructions;

        var developer = new Worker(
            "developer", "Developer", instructions,
            new[] { "write_file", "read_file", "list_dir", "run_command" },
            PermissionLevel.Execute, new ModelPolicy(new ModelRef("ollama", _model)));
        _workerProvider = new StaticWorkerProvider(developer);

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
        2 => new PermissionPolicy(PermissionLevel.Execute, new[] { "*" }, new[] { "run_command" }),
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
