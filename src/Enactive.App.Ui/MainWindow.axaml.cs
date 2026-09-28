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
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using Enactive.Providers;
using Enactive.Remote.Host;
using Enactive.Settings;
using Enactive.Tools;
using Enactive.Tools.Mcp;
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
    private readonly HttpClient _http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private string _model = "qwen2.5-coder";
    private string _globalInstructions = string.Empty;
    private ChatProviderFactory _providerFactory = null!;
    /// <summary>
    /// Rebuilt on Save, not only at startup — see <see cref="BuildToolRegistry"/>. Every use reads
    /// the field at call time, so replacing it is all that is needed.
    /// </summary>
    private IToolRegistry _toolRegistry;
    // Global, app-wide log hub. Default Debug (readable); the log window can drop it to Trace for raw wire.
    // Held separately from the hub so settings can reach it: this is built before any settings are
    // read, and retention is a setting.
    private readonly FileLogSink _logFile = new();
    private readonly LogHub _log;
    private LogWindow? _logWindow;
    private InboxWindow? _inboxWindow;
    private readonly EnvironmentProbe _envProbe = new();
    private readonly Planner _planner = new();
    private readonly ModelResolver _modelResolver = new();
    // The interface, not the concrete provider: what composes the team is EngineComposition now, and
    // this window only reads it.
    private IWorkerProvider _workerProvider = null!;

    /// <summary>
    /// Why there is no engine, or null when there is one.
    ///
    /// <para>Set when the settings name no model — which is now what a machine nobody has configured
    /// looks like, instead of one configured for a model name compiled into the app. The window
    /// still opens and everything that does not need a model still works; the things that DO need
    /// one say this instead of running.</para>
    /// </summary>
    private string? _engineProblem;
    private readonly PermissionEngine _permissionEngine = new();
    private AppSettings _settings = new();

    /// <summary>Everything the window shows. Nothing below touches a control - it sets a property here.</summary>
    private readonly MainWindowViewModel _vm = new();
    private readonly HashSet<string> _shownArtifacts = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Workspaces already told about their ignored legacy approvals file — say it once.</summary>
    private readonly HashSet<string> _legacyApprovalsNoted = new(StringComparer.OrdinalIgnoreCase);

    // ── Run state ────────────────────────────────────────────────────────────
    private readonly ForegroundRunSlot _foreground = new();
    private DecisionCompletion? _pendingDecision;
    private readonly SessionApprovals _sessionApprovals = new();
    private readonly DecisionQueue _decisionQueue = new();
    private readonly List<StepCardViewModel> _cards = new();
    private readonly List<StepCardViewModel> _running = new();
    private StepCardViewModel? _currentCard;
    private int _stepIndex;
    private int _doneSteps;
    private int _totalSteps;
    private readonly Stopwatch _runStopwatch = new();
    private DispatcherTimer? _elapsedTimer;

    /// <summary>See where it is started: the badge against work this process did not do.</summary>
    private DispatcherTimer? _inboxPoll;
    private string _currentWorkspaceRoot = string.Empty;
    private readonly WorkspaceRegistry _registry = WorkspaceRegistry.Load(deferWrites: true);

    /// <summary>What each workspace was left showing in the middle column. See OpenRunMemory.</summary>
    private readonly OpenRunMemory _openRuns = new();

    /// <summary>
    /// Runs in flight, and the workspace each belongs to.
    ///
    /// <para>Held here because nothing else knows: a run is written to the store when it ENDS, so
    /// between Run and the end this list is the only record that it is happening at all. The
    /// workspace is carried alongside because a background run keeps going after the workspace has
    /// been switched, and it must not appear in another project's column.</para>
    /// </summary>
    private readonly Dictionary<Guid, (LiveRun Run, string Workspace)> _live = new();

    /// <summary>The foreground run's row, so events can find it. Null when nothing is running.</summary>
    private Guid _liveRow;

    /// <summary>
    /// The workspace whose run the middle column belongs to, normalised. Set when a foreground run
    /// starts and kept after it ends, because the finished feed still belongs to that workspace and
    /// coming back to it should show what was left there.
    /// </summary>
    private string _liveWorkspace = string.Empty;

    /// <summary>Whether the middle column is currently showing that run.</summary>
    private bool _liveAttached = true;

    /// <summary>
    /// Every write the live run makes to the view model, in order, for the length of the run.
    ///
    /// <para>The middle column can now be walked away from - a run keeps going while another
    /// workspace is on screen - and what it looked like has to come back. Keeping a JOURNAL rather
    /// than a snapshot is what makes that a small change instead of a rewrite: the objects behind
    /// the column (the step cards) live in fields and go on updating themselves while nobody is
    /// looking, so re-attaching is "empty the panels, replay the writes" and nothing has to be
    /// captured field by field or kept in step with the next thing somebody adds.</para>
    ///
    /// <para>Cleared when a run starts. Kept after one ends, so a finished feed is still restorable
    /// until it is replaced.</para>
    /// </summary>
    private readonly List<Action> _liveJournal = new();
    private string? _liveAgent;
    private Guid _renderedRunId;
    private readonly BackgroundRunGroup _background = new();
    private bool _exitRequested;
    private bool _shuttingDown;
    private bool _forceClose;
    private StagingArtifactStore? _staging;
    /// <summary>The current run's disk store, when it is writing straight to the workspace. Kept so the
    /// artifact cards can ask whether a file was CREATED by this run or only overwritten.</summary>
    private DiskArtifactStore? _disk;
    private int _stagedShown;

    public MainWindow()
    {
        _log = new LogHub(minLevel: LogLevel.Debug, downstream: new ILogSink[] { _logFile });
        _settings = AppSettings.Load();
        _toolRegistry = BuildToolRegistry();

        // A settings file the app cannot build from must not make the app unlaunchable. Saving is
        // validated now, but a file edited by hand — or written by an older build — can still be
        // impossible, and the constructor is the one place where throwing means the window never
        // opens. Start on defaults instead, keep the file, and say so.
        var settingsProblem = TryApplySettings();

        _log.Info(LogSource.System, $"Enactive UI started — logs at {FileLogSink.DefaultDirectory()}");
        if (settingsProblem is not null)
            _log.Error(LogSource.System,
                $"settings.json could not be applied ({settingsProblem}). Running on defaults — your file "
                + "has NOT been overwritten; fix it in Settings, or edit it and restart.");

        // A file an older version left in a state the runtime cannot build was repaired in memory so
        // the app could open at all. Say what changed, and where the untouched original went.
        foreach (var repair in _settings.LoadProblems)
            _log.Error(LogSource.System,
                $"settings.json needed repair to start: {repair} A copy of the original is beside it "
                + "as settings.before-repair-*.json. Review Settings and save to keep the repair.");

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
        _vm.StopRequested += () => _foreground.Stop();
        _vm.TimelineRequested += () => _ = ShowTimelineAsync();
        _vm.LogRequested += ShowLogWindow;
        _vm.EnvironmentRequested += () => _ = ShowEnvironmentAsync();
        _vm.InboxRequested += ShowInbox;
        _vm.TemplatesRequested += ShowTemplates;
        _vm.SchedulesRequested += ShowSchedules;
        _vm.Runs.RefreshRequested += () => _ = LoadRunsAsync();
        _vm.Runs.OpenRequested += summary => _ = OpenPastRunAsync(summary);
        _vm.Runs.DeleteRequested += summary => _ = DeleteRunAsync(summary);
        _vm.Runs.DeleteShownRequested += shown => _ = DeleteRunsAsync(shown);
        _vm.Runs.ResumeRequested += checkpoint => _ = RunAsync(background: false, resume: checkpoint);
        // Pressing the running row is the way back to the live feed. The "← Back" in the past-run
        // header does the same thing and is where nobody looks.
        _vm.Runs.OpenLiveRequested += () => _vm.ShowLiveRun();
        _vm.WorkspacePathChanged += RefreshWorkspaces;
        _vm.WorkspaceSwitchRequested += SwitchWorkspace;
        _vm.WorkspaceRenameRequested += path => _ = RenameWorkspaceAsync(path);
        _vm.WorkspaceForgetRequested += path => _ = ForgetWorkspaceAsync(path);
        _vm.RunSettingsChanged += SaveRunSettings;
        _vm.AddWorkspaceRequested += () => _ = AddWorkspaceAsync();
        _vm.SettingsRequested += () =>
            // SettingsWindow gets the live settings and CLONES them, so Cancel/close leave these
            // untouched and Save hands back the clone, which is then written and put in place of
            // this one. That makes AppSettings.Clone the whole of what survives a visit to this
            // window: a property missing there is a setting the next Save resets, whether or not
            // the window has a control for it. Pinned by SettingsSurviveTheEditorTests.
            new SettingsWindow(_settings, workspaceRoot: WorkspaceRootOrNull(),
                toolNames: _toolRegistry.Definitions.Select(d => d.Name).ToArray(),
                remoteCheck: CheckRemoteAsync,
                onSaved: saved =>
            {
                if (!saved.Save(replaceUnreadable: true))
                    throw new InvalidOperationException(
                        saved.LastSaveError is { Length: > 0 } why
                            ? "Settings were not saved. " + why
                            : "Settings could not be saved. Check disk access and Windows credential encryption.");

                // Read BEFORE _settings is replaced: the comparison is the only thing that decides
                // whether to disturb a connection that may have a run on it.
                var wasRemote = Describe(_settings.RemoteAccess);

                _settings = saved;
                ApplySettings();

                if (Describe(_settings.RemoteAccess) != wasRemote)
                    _ = RestartRemoteAccessAsync();
            }).Show(this);
        _vm.InputFocusRequested += () =>
        {
            InputBox.Focus();
            InputBox.CaretIndex = _vm.InputText.Length;
        };

        DataContext = _vm;
        InitializeComponent();

        // ── The badge, against work this process did not do ───────────────────────
        // The Inbox count used to change only when THIS window did something: switched workspace,
        // opened the Inbox, or finished a background run of its own. A scheduled run is a different
        // process; its item lands in the store while this window sits showing the old number, so the
        // one place a person would learn about the run says nothing.
        //
        // A poll rather than a file watcher, because the store may be SQLite, MySQL or JSON and only
        // one of those has a file to watch. Once at startup — the count is a fact about the store,
        // not about anything this session has done yet — and then on a minute, which is well inside
        // "I looked over and it was right".
        RefreshInboxButton();
        _inboxPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _inboxPoll.Tick += (_, _) => RefreshInboxButton();
        _inboxPoll.Start();

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

        StartRemoteAccess();
    }

    // ── Remote access ────────────────────────────────────────────────────────

    /// <summary>
    /// This computer's end of remote access, or null when the settings say not to connect. Held so
    /// it can be let go of on exit; everything else about it happens on its own.
    /// </summary>
    private RemoteAccessService? _remote;

    /// <summary>Where this computer keeps what it was asked to do and has not yet reported.</summary>
    private static string RemoteDatabasePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Enactive", "remote.db");

    private void StartRemoteAccess()
    {
        if (_shuttingDown) return;
        _remote = new RemoteAccessService(
            _settings.RemoteAccess,
            // On the UI thread, because it reads the worker list and the app settings. Governed by
            // the workspace THE TASK NAMED, not by the slider: the slider is about the folder open
            // on this screen, and a phone naming a different project must get the level saved for
            // that project. Read when the task arrives rather than now, so editing a workspace's
            // autonomy takes effect without restarting.
            entry => Dispatcher.UIThread.InvokeAsync(
                () => SnapshotEnvironment(
                    Math.Clamp(entry.Autonomy, 0, 3), entry.WorkerId, entry.StageChanges)).GetTask(),
            () => _registry.Entries,
            // The desktop's own handler. RemoteRunner wraps it rather than replacing it, so a
            // permission question from a remote run shows here as well as on the phone.
            this,
            RemoteDatabasePath());

        _remote.Changed += () => Dispatcher.UIThread.Post(() =>
            _log.Info(LogSource.System, "Remote access: " + _remote!.Status));

        _remote.Start();
        _log.Info(LogSource.System, "Remote access: " + _remote.Status);
    }

    /// <summary>
    /// The remote settings as a value that can be compared, so a Save that did not touch them
    /// leaves the connection alone.
    ///
    /// <para>The token is compared by whether there is one, not by what it is: this decides whether
    /// to reconnect, and putting a bearer credential into a string that is compared, logged by
    /// accident or held in a local is not worth the precision. A token REPLACED with a different
    /// one of the same emptiness is the one case this misses, and Test connection is what covers
    /// it.</para>
    /// </summary>
    private static string Describe(RemoteAccessSettings remote)
        => $"{remote.Enabled}|{remote.GatewayUrl}|{remote.Token.Length > 0}";

    /// <summary>
    /// Applies changed remote settings without restarting the application.
    ///
    /// <para>This exists because of what happened the first time somebody set this up. The service
    /// was built once in the constructor from the settings as they were AT STARTUP - which said
    /// off, because the token had not been pasted yet. Entering everything correctly and pressing
    /// Save then did nothing at all, said nothing at all, and the panel went on reporting the
    /// computer Offline. There was no way to tell that from settings that were simply wrong.</para>
    /// </summary>
    private async Task RestartRemoteAccessAsync()
    {
        var previous = _remote;
        _remote = null;

        if (previous is not null)
        {
            _log.Info(LogSource.System, "Remote access: settings changed, reconnecting.");
            await previous.DisposeAsync();
        }

        StartRemoteAccess();
    }

    /// <summary>
    /// Tries a gateway address and token for the settings window, and says what happened.
    ///
    /// <para>It runs against the REAL gateway with this computer's real workspace list, because a
    /// check that stopped short of that would answer a narrower question than the one being
    /// asked - and the question being asked is "why does the phone say Offline".</para>
    /// </summary>
    private async Task<string> CheckRemoteAsync(string gatewayUrl, string token, CancellationToken ct)
    {
        var workspaces = RemoteAccessService.Publishable(_registry.Entries);
        var check = await GatewayProbe.CheckAsync(gatewayUrl, token, workspaces, ct);

        _log.Info(LogSource.System, "Remote access check: " + check.Detail);

        return check.Detail;
    }

    // ── Closing ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Work that must be cancelled and drained before this process exits.
    /// </summary>
    private bool HasWorkInFlight()
        => _vm.IsBusy || _pendingDecision is not null
            || _background.Count > 0 || RemoteRunsInFlight > 0;

    /// <summary>
    /// Runs a phone started that are still going. They die with the process exactly as a background
    /// run does, and the person being asked about quitting is not the person watching them - which
    /// is the reason to say so rather than to count them in silently.
    /// </summary>
    private int RemoteRunsInFlight => _remote?.Running.Count ?? 0;

    private string DescribeWorkInFlight()
    {
        var background = _background.Count;
        var remote = RemoteRunsInFlight;

        if (_pendingDecision is not null)
            return "A run is waiting for your decision. Closing now cancels it.";

        if (remote > 0)
            return remote == 1
                ? "A task started from your phone is still going. Closing now stops it where it is."
                : $"{remote} tasks started from your phone are still going. Closing now stops them where they are.";
        if (_vm.IsBusy && background > 0)
            return $"A run is going, and {background} more in the background. Closing cancels them and waits for cleanup and recording.";
        if (_vm.IsBusy)
            return "A run is going. Closing cancels it and waits for cleanup and recording.";
        return background == 1
            ? "A background run is still going. Closing cancels it and waits for cleanup and recording."
            : $"{background} background runs are still going. Closing cancels them and waits for cleanup and recording.";
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
        if (_exitRequested || _shuttingDown) return;
        _exitRequested = true;
        try
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
                    "Stop and quit",
                    "Keep working");

                if (!quit)
                    return;

            }

            _shuttingDown = true;
            _inboxPoll?.Stop();
            _vm.StatusPhase = "Stopping…";
            // Begin all shutdown paths before awaiting any one of them. Keep the dispatcher and log
            // alive until recorder, resources and final UI cleanup have finished.
            try
            {
                await Task.WhenAll(_foreground.StopAsync(), _background.StopAsync(), _registry.FlushAsync(), _workspacePreparation,
                    _remote is null ? Task.CompletedTask : _remote.DisposeAsync().AsTask());
            }
            catch (Exception ex)
            {
                _log.Error(LogSource.System, "Shutdown cleanup failed: " + ex.Message);
            }

            await _registry.FlushAsync();
            _forceClose = true;
            Close();

            // Shutdown is explicit now: the lifetime no longer ends with the main window, because the
            // main window comes and goes from the tray.
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        finally { _exitRequested = false; }
    }


    // ── Run an intent ────────────────────────────────────────────────────────
    /// <summary>
    /// Starts the request in the box. <paramref name="background"/> comes from WHICH BUTTON was
    /// pressed, not from a setting: the same words typed into the same box should not do two
    /// different things depending on state the user cannot see from here.
    /// </summary>
    /// <summary>
    /// Opens the template library. It resolves a specification and hands it back; starting the run
    /// stays here, where the providers, the tools and the artifact store already are.
    /// </summary>
    /// <summary>The open workspace as a full path, or null when the box is empty.</summary>
    private string? WorkspaceRootOrNull()
    {
        var root = _vm.WorkspacePath.Trim();
        return string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
    }

    private TemplatesWindow? _templatesWindow;

    private void ShowTemplates()
    {
        // One library, kept and re-shown. Opening a second copy of a list that has to follow the
        // workspace is two lists that can disagree about which workspace that is.
        if (_templatesWindow is not null)
        {
            _templatesWindow.FollowWorkspace(WorkspaceRootOrNull(), PolicyFor(_vm.AutonomyTier));
            _templatesWindow.Show();
            _templatesWindow.Activate();
            return;
        }

        _templatesWindow = new TemplatesWindow(
            WorkspaceRootOrNull(),
            PolicyFor(_vm.AutonomyTier),
            spec =>
            {
                // The goal goes into the command bar as well as into the run. Otherwise a templated
                // run has no visible request at all, and the header shows a task nobody typed.
                _vm.InputText = spec.Goal;
                _ = RunAsync(background: false, spec);
            });
        _templatesWindow.Closed += (_, _) => _templatesWindow = null;
        _templatesWindow.Show(this);
    }

    private SchedulesWindow? _schedulesWindow;

    /// <summary>
    /// Opens the schedules. Kept and re-shown like the library, and for the same reason: schedules
    /// are per-workspace, and a second copy is a second answer to which workspace this is.
    /// </summary>
    private void ShowSchedules()
    {
        if (WorkspaceRootOrNull() is not { } root)
        {
            ShowViewer("Schedules", "Set a workspace first.");
            return;
        }

        if (_schedulesWindow is not null)
        {
            _schedulesWindow.FollowWorkspace(root);
            _schedulesWindow.Show();
            _schedulesWindow.Activate();
            return;
        }

        _schedulesWindow = new SchedulesWindow(root, _settings);
        _schedulesWindow.Closed += (_, _) =>
        {
            _schedulesWindow = null;

            // A schedule edited here may have fired, or been turned off; either way the count on
            // the rail is now a fact about a moment that has passed.
            RefreshInboxButton();
        };
        _schedulesWindow.Show(this);
    }

    /// <param name="taskId">
    /// The task this run is an attempt at. Null starts a new task - which is every run typed into
    /// the command bar. Retry and Run again pass the original, which is what makes them ATTEMPTS
    /// rather than unrelated runs that happen to ask the same thing.
    /// </param>
    /// <param name="resume">
    /// An interrupted run to carry on from, or null to start fresh. When it is set the run uses the
    /// PERMISSIONS AND ROLE the checkpoint recorded rather than the ones the sliders are on now:
    /// continuing a run under permissions it did not have is not continuing it.
    /// </param>
    private async Task RunAsync(
        bool background, ResolvedTaskSpec? spec = null, Guid? taskId = null, RunCheckpoint? resume = null)
    {
        if (_shuttingDown || (!background && _vm.IsBusy) || _pendingDecision is not null)
            return;

        // A resumed run's request comes from the checkpoint. The command bar is empty by then - the
        // interrupted run cleared it when it started, possibly days ago in another process.
        var text = spec?.Goal.Trim() ?? resume?.Request.Trim() ?? _vm.InputText.Trim();
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

        // No model, no run - said here rather than discovered as a 404 from a provider. Before the
        // model names came out of AppSettings this could not happen: an unconfigured machine was
        // configured for whatever string was compiled in, and it ran.
        if (_engineProblem is { Length: > 0 } notConfigured)
        {
            _vm.StatusPhase = "Error";
            _vm.CurrentAction = notConfigured;
            return;
        }

        // Background: fire the run headless (results land in the Inbox) and keep the UI free.
        if (background)
        {
            StartBackground(text, Path.GetFullPath(workspacePath));
            return;
        }

        // Everything the previous run left on screen goes first, through the one method that knows
        // what "on screen" is made of. Reading history is fine; watching it while a new run of your
        // own starts is not.
        using var runCancellation = _foreground.TryStart();
        if (runCancellation is null) return;
        try
        {
            _vm.IsBusy = true;
            ClearRunView();

            // The command bar clears, so the request moves into the header - otherwise what you asked
            // for survives only in the log.
            // This run's workspace, and the column is showing it: whatever was on screen a moment ago,
            // pressing Run means watching THIS.
            _liveWorkspace = WorkspaceRegistry.Normalise(workspacePath);
            _liveAttached = true;

            _vm.InputText = string.Empty;
            Live(() => _vm.TaskIntent = text);
            SetLiveTitle(Summarise(text));
            Live(() => _vm.HasTask = true);
            Live(() => _vm.StatusPhase = "Running");

            // A row in the column from the FIRST moment, not from the last one. The store learns about
            // a run when it ends, so without this the run is on screen nowhere but the middle column -
            // and opening an older run to compare loses it.
            _liveRow = BeginLive(_liveTitle, Path.GetFullPath(workspacePath), headless: false);
            Live(() => _vm.StatusProgress = "—");
            Live(() => _vm.CurrentAction = string.Empty);
            _vm.StatusElapsed = "0s";
            _vm.IsBusy = true;
            RefreshColumnState();
                _elapsedTimer?.Stop();
                _runStopwatch.Restart();
                // Not journalled - see AttachLive, which sets the elapsed time from the stopwatch itself.
                _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _elapsedTimer.Tick += (_, _) =>
                {
                    if (_liveAttached)
                        _vm.StatusElapsed = FormatElapsed(_runStopwatch.Elapsed);
                };
                _elapsedTimer.Start();



                // What this run is allowed to do. A resumed run keeps what the interrupted one recorded, so
                // the history of the second half says what actually governed it.
                var runSettings = resume?.Settings ?? CurrentRunSettings();
                var engineOptions = RunEngineOptions.Capture(_settings);

                var fullPath = Path.GetFullPath(workspacePath);
                _currentWorkspaceRoot = fullPath;
                NoteLegacyApprovalsIfAny(fullPath);
                _registry.Touch(fullPath);
                RefreshWorkspaces();

                // Adopt, not For: a run is where a folder is genuinely taken up as a workspace, so this is
                // where its id gets written down beside it. The id written is the one it already had, so a
                // workspace with history keeps it and can be renamed from here on without losing it.
                var workspace = WorkspaceInfo.Adopt(fullPath);

                // A template's permissions are already the INTERSECTION of its own ceiling and the
                // workspace's tier - TemplateResolution.Narrow did that when the specification was resolved,
                // and a ceiling has no way to widen anything. So this is never more than the slider allows.
                // A resumed run continues under the autonomy it was started with. The slider will have moved
                // by now - it is a control, not a record - and a run that finishes its remaining steps under
                // permissions nobody granted it is not the run somebody asked to resume.
                var policy = spec?.Permissions
                    ?? (resume?.Settings is { } was ? PolicyFor(was.Autonomy) : PolicyFor(_vm.AutonomyTier));

                IArtifactStore artifactStore;
                // A resumed run never stages, whatever the toggle says. Its earlier steps wrote straight to
                // disk - that is the only kind of run that is ever checkpointed - so staging the rest would
                // put half of one piece of work behind a review gate and leave the other half applied.
                if (_vm.StageChanges && resume is null)
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

                await using var mcp = await McpRunTools.ConnectAsync(_toolRegistry, _settings.McpServers, fullPath, runCancellation.Token);

                // Said once per run, whether or not anything calls them: starting a server is a cost
                // the run has already paid, and the log had no record of it at all.
                _log.Info(LogSource.Tool, mcp.Summary());
                IToolRegistry runTools = new LoggingToolRegistry(mcp, _log);
                var runStore = RunStoreFactory.Create(workspace);
                // The same store the recorder folds into, so a run reads back what earlier ones
                // concluded (PLAN_v2 §11).
                var memoryStore = MemoryStoreFactory.Create(workspace);
                var contextProvider = new ContextProvider(workspace, new EnvironmentProbe(), memoryStore);
                var orchestrator = RunEngineComposition.Build(
                    new RunEngineResources(_providerFactory, _modelResolver, _workerProvider, runTools,
                        artifactStore, workspace, _planner, _permissionEngine, this, policy,
                        new EmptyProvider(), BuildRouter()), engineOptions,
                    checkpoints: new JsonCheckpointStore(workspace), settings: runSettings,
                    successCriteria: spec?.SuccessCriteria, limits: spec?.Limits);
                // The specification is recorded WITH the run, so reading it back later shows the template
                // as it was rather than as it has since been edited.
                var recorder = new RunRecorder(
                    runStore, memoryStore, workspace.Id, runSettings, spec?.Snapshot());

                var context = await contextProvider.BuildAsync(new IntentFocus(workspace.Id), runCancellation.Token);
                // The template names the role it needs; the picker decides only when it does not.
                var workerId = spec?.WorkerId
                    ?? (_workerProvider.All.Count > 0
                        && _vm.SelectedWorkerIndex >= 0 && _vm.SelectedWorkerIndex < _workerProvider.All.Count
                        ? _workerProvider.All[_vm.SelectedWorkerIndex].Id : null);
                // The intent's id IS the task id - the orchestrator takes it as one - so continuing a
                // task is a matter of handing back the id it had.
                var intent = new Intent(
                    taskId ?? Guid.NewGuid(), text, IntentSource.CommandBar, context, DateTimeOffset.UtcNow, workerId);
                var envLine = context.Environment?.OneLine();
                await Dispatcher.UIThread.InvokeAsync(() => _vm.EnvironmentSummary = envLine ?? "(no environment data)");

                var stream = resume is null
                    ? orchestrator.SubmitIntentAsync(intent, runCancellation.Token)
                    : orchestrator.ResumeRunAsync(resume, context, runCancellation.Token);

                await RunEventPump.RunAsync(
                    recorder.RecordAsync(stream.TeeToLog(_log, runCancellation.Token), runCancellation.Token),
                    async batch => await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var ev in batch) RenderEvent(ev);
                    }), runCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { Live(() => _vm.StatusPhase = "Cancelled"); _currentCard?.SetFailed(); });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Live(() => { _vm.StatusPhase = "Error"; _vm.CurrentAction = ex.Message; });
                    _currentCard?.SetFailed();
                });
            }
            finally
            {
                try
                {
                _elapsedTimer?.Stop();
                _elapsedTimer = null;
                _runStopwatch.Stop();
                var finalElapsed = FormatElapsed(_runStopwatch.Elapsed);
                var endedRow = _liveRow;
                _liveRow = Guid.Empty;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_liveAttached)
                        _vm.StatusElapsed = finalElapsed;
                    // The row goes BEFORE the history is re-read, so the moment it stops running is the
                    // moment it stops being listed as running. RunColumn.Live is the belt to this
                    // brace: if the two ever cross, the run is not shown twice.
                    EndLive(endedRow);
                    // The run that just ended belongs at the top of the history under the workspace.
                    // Only when that workspace is the one on screen - LoadRunsAsync reads whichever is
                    // current, and a run ending elsewhere is not a reason to re-read this one.
                    if (IsLiveWorkspace)
                        _ = LoadRunsAsync();
                    RefreshColumnState();
                });
                }
                finally
                {
                    if (_foreground.Finish(runCancellation)) _vm.IsBusy = false;
                }
            }
        }

        // ── Map events to the three panels (always on the UI thread) ─────────────
        private void RenderEvent(WorkEvent ev)
        {
                // The run id is the orchestrator's to mint, so the log tab learns it from the first
                // event rather than being told in advance. So does the live row, which needs it to be
                // matched against the history when the run ends.
                _vm.RunLog?.SetRun(ev.RunId);
                if (_liveRow != Guid.Empty && _renderedRunId != ev.RunId)
                {
                    _renderedRunId = ev.RunId;
                    UpdateLive(_liveRow, r => r.RunId == ev.RunId ? r : r with { RunId = ev.RunId });
                }

                switch (ev.Kind)
                {
                    case EventKind.IntentReceived:
                        Live(() => _vm.StatusPhase = "Understanding");
                        break;
                    case EventKind.Routed:
                        Live(() => _vm.Routing.Apply(ev.Summary, ev.PayloadJson));
                        if (ev.Summary.Contains("-> model"))
                            Live(() => _vm.StatusPhase = "Planning");
                        if (ev.Summary.StartsWith("Quick action: ", StringComparison.Ordinal))
                            SetLiveTitle(ev.Summary["Quick action: ".Length..]);
                        if (ev.Summary.StartsWith("Reasoner", StringComparison.Ordinal))
                            SetLiveAgent("Reasoner · planning", Brand.PillReasoner);
                        break;
                    case EventKind.PlanCreated:
                        Live(() => _vm.StatusPhase = "Executing");
                        // The planner's title is a better header than the raw request, which is often a
                        // paragraph. From the payload; the sentence is read only for a run produced by a
                        // build that predates it, where a title containing " — " lost its tail.
                        if (ev.PlanTitle() is { Length: > 0 } plannedTitle)
                            SetLiveTitle(plannedTitle);
                        else
                        {
                            var dash = ev.Summary.IndexOf(" — ", StringComparison.Ordinal);
                            if (dash > 0)
                                SetLiveTitle(ev.Summary[..dash]);
                        }
                        CreateStepCards(ev);
                        break;
                    case EventKind.StepStarted:
                        SetLiveAgent("Coder", Brand.PillCoder);
                        BeginStep(ev);
                        (CardFor(ev) ?? EnsureCurrentCard()).SetActivity("Thinking…");
                        break;
                    case EventKind.StepCompleted:
                        var doneCard = CardFor(ev) ?? _currentCard;
                        // A failed step and a dependency-skipped step arrive as StepCompleted too, so the
                        // card must not go green for either of them. The step's outcome is now a value in
                        // the payload; the old string search is the fallback for a run recorded by an
                        // earlier build, and is exactly the fragility it replaces — rewording a summary
                        // used to turn a red card green.
                        var stepOutcome = ev.StepOutcome();
                        var wasSkipped = stepOutcome == StepOutcomeKind.Skipped
                            || (stepOutcome is null && ev.Summary.Contains("skipped (dependency", StringComparison.Ordinal));
                        var wasFailed = wasSkipped
                            || (stepOutcome is not null && stepOutcome != StepOutcomeKind.Succeeded)
                            || (stepOutcome is null && ev.Summary.Contains("FAILED:", StringComparison.Ordinal));
                        // The line under the title is the step's own REASON when it recorded one, and
                        // the outcome word alone otherwise. It used to be three literals here, which is
                        // how a run stopped by "nothing is listening at http://localhost:11434/v1"
                        // showed a card that said "Failed" and nothing else, with the diagnosis sitting
                        // unread in the payload this very method is holding. The wording is in Core
                        // (RunOutcomeWords) because this file is in a WinExe no test can reach - which
                        // is exactly where a literal like that gets written and never questioned.
                        var stepSays = RunOutcomeWords.StepActivity(
                            wasSkipped ? StepOutcomeKind.Skipped : stepOutcome,
                            ev.OutcomeReason());

                        if (wasSkipped)
                        {
                            // Skipped is not failed: nothing went wrong in THIS step, and painting it red
                            // sends you looking for a fault that is in another card.
                            doneCard?.SetSkipped();
                            doneCard?.SetActivity(stepSays);
                        }
                        else if (stepOutcome == StepOutcomeKind.DoneUnverified)
                        {
                            // Checked before wasFailed, which counts anything short of Succeeded as a
                            // failure and would paint work that is on disk red.
                            doneCard?.SetUnverified();
                            doneCard?.SetActivity(stepSays);
                        }
                        else if (wasFailed)
                        {
                            doneCard?.SetFailed();
                            doneCard?.SetActivity(stepSays);
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
                    // A long generation still arriving - shown on the activity line, replaced in place,
                    // so minutes of writing a big tool call do not look like a hang.
                    case EventKind.GenerationProgress:
                        (CardFor(ev) ?? EnsureCurrentCard()).SetActivity(ev.Summary);
                        break;

                    case EventKind.AssistantDelta:
                        SetLiveAgent("Coder", Brand.PillCoder);
                        var streamCard = CardFor(ev) ?? EnsureCurrentCard();
                        // Buffered, not shown live - the raw streamed reply isn't interesting on its own;
                        // it gets folded into one short note the next time a tool runs or the step ends.
                        streamCard.AppendAssistantText(ev.Summary);
                        streamCard.SetActivity("Thinking…");
                        break;
                    case EventKind.ToolInvoked:
                        SetLiveAgent("Coder", Brand.PillCoder);
                        Live(() =>
                        {
                            _vm.ToolCalls++;
                            _vm.CurrentAction = ev.Summary;
                        });
                        var toolCard = CardFor(ev) ?? EnsureCurrentCard();
                        StepCardWriter.LogInvocation(toolCard, ev.Summary);
                        toolCard.SetActivity(StepCardWriter.DescribeActivity(ev.Summary));
                        break;
                    case EventKind.ToolResult:
                        (CardFor(ev) ?? EnsureCurrentCard()).AppendEntryDetail(ev.Summary);
                        break;
                    case EventKind.ErrorObserved:
                        // Surface the warning in the activity line without changing disclosure state.
                        var warnCard = CardFor(ev) ?? EnsureCurrentCard();
                        warnCard.AddNote("⚠ " + ev.Summary);
                        warnCard.SetActivity("⚠ " + ev.Summary);

                        break;
                    case EventKind.ReviewRequested:
                    case EventKind.ReviewPassed:
                    case EventKind.ReviewFailed:
                        SetLiveAgent("Reasoner · review", Brand.PillReasoner);
                        Live(() =>
                        {
                            _vm.CurrentAction = ev.Summary;
                        });
                        var reviewCard = CardFor(ev) ?? EnsureCurrentCard();
                        reviewCard.AddNote(ev.Summary);
                        reviewCard.SetActivity(
                            ev.Kind == EventKind.ReviewRequested ? "Reviewing…" :
                            ev.Kind == EventKind.ReviewPassed ? "Review passed" : "Review flagged an issue…");
                        break;
                    case EventKind.DecisionRequested:
                    case EventKind.DecisionResolved:
                        Live(() => _vm.CurrentAction = ev.Summary);
                        var decisionCard = CardFor(ev) ?? EnsureCurrentCard();
                        // A refused call gets its own word in the summary rather than being folded in
                        // with the remarks - by the event's VALUE, exactly as the replay reads it, so
                        // the live card and the same run reopened later cannot disagree.
                        if (ev.WasRefused() == true)
                            decisionCard.AddRefusal(ev.Summary);
                        else
                            decisionCard.AddNote(ev.Summary);
                        if (ev.Kind == EventKind.DecisionRequested)
                        {
                            decisionCard.SetActivity("Waiting for your approval…");
                            decisionCard.SetWaitingForYou();
                        }
                        else
                        {
                            // Answered: the step is moving again, and the card should stop saying it is
                            // not. The step's own ending overwrites this either way.
                            decisionCard.SetRunning();
                        }
                        break;
                    case EventKind.UsageReported:
                        if (ev.Usage() is { } used)
                        {
                            var usageProvider = ev.ProviderId();
                            Live(() => _vm.AddUsage(used.In, used.Out, usageProvider, ReachOf(usageProvider)));
                        }
                        break;
                    case EventKind.ArtifactProduced:
                        (CardFor(ev) ?? EnsureCurrentCard()).AddNote("Artifact: " + ev.Summary);
                        if (_staging is not null)
                            AddStagedArtifact();
                        else
                            AddArtifact(ev);
                        break;

                    // The conversation was pruned to fit the model's window, or is being handed over to a
                    // fresh one - announced before the note is written, because writing it is one long
                    // silent turn. Shown on the step card, not buried in the log: from here on the model
                    // is working with less than it was given, and that explains behaviour a person would
                    // otherwise blame on the model - or on a hang.
                    case EventKind.ContextTrimmed:
                        (CardFor(ev) ?? EnsureCurrentCard()).AddNote(ev.Summary);
                        break;

                    // The step's work was put back after the reviewer rejected it. Its cards must stop
                    // offering to open or undo a file that is no longer the file they describe.
                    case EventKind.ArtifactReverted:
                        (CardFor(ev) ?? EnsureCurrentCard()).AddNote(ev.Summary);
                        MarkRevertedArtifacts(ev.Summary);
                        break;
                    // The pill says what the engine DECIDED, read from the event's typed outcome rather
                    // than from which of the two terminal kinds arrived. "Incomplete" is its own answer:
                    // nothing failed, but the work is not done, and calling that Completed is what let a
                    // truncated or half-run task look finished.
                    case EventKind.TaskCompleted:
                    case EventKind.TaskFailed:
                        var outcome = ev.Outcome()
                            ?? (ev.Kind == EventKind.TaskCompleted
                                ? RunOutcomeKind.Completed
                                : RunOutcomeKind.Failed);

                        Live(() =>
                        {
                            _vm.StatusPhase = outcome.ToString();
                            _vm.IsAgentVisible = false;
                            // Why it stopped belongs on screen, not only in the log.
                            _vm.CurrentAction = outcome == RunOutcomeKind.Completed
                                ? string.Empty
                                : ev.OutcomeReason() ?? ev.Summary;
                        });

                        if (outcome == RunOutcomeKind.Completed)
                        {
                            _currentCard?.SetDone();
                            _currentCard?.SetActivity("Done");
                        }
                        else
                        {
                            _currentCard?.SetFailed();
                            _currentCard?.SetActivity(outcome.ToString());
                        }
                        break;
                }
        }

        private void CreateStepCards(WorkEvent ev)
        {
            // Values first. Splitting the sentence on " | " turned a step whose own title contains one
            // into two cards, and every event afterwards was attributed to the wrong card. The sentence
            // is read only for a run produced by a build that predates the payload.
            var titles = ev.PlanSteps()?.ToArray() ?? FromSummary(ev.Summary);
            if (titles.Length == 0)
                return;

            _totalSteps = titles.Length;
            foreach (var title in titles)
            {
                var card = new StepCardViewModel(title.Trim());
                _cards.Add(card);
                Live(() => _vm.Steps.Add(card));
            }
            UpdateProgress();

            static string[] FromSummary(string summary)
            {
                const string marker = " steps: ";
                var index = summary.IndexOf(marker, StringComparison.Ordinal);
                return index < 0
                    ? Array.Empty<string>()
                    : summary[(index + marker.Length)..].Split(" | ", StringSplitOptions.RemoveEmptyEntries);
            }
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
                var fresh = new StepCardViewModel(ev.Summary);
                card = fresh;
                _cards.Add(fresh);
                Live(() => _vm.Steps.Add(fresh));
                _totalSteps = _cards.Count;
            }
            card.SetRunning();
            _running.Add(card);
            // With one step in flight this is that step; with several, events without a step number have
            // no single owner, so nothing claims to be "current".
            _currentCard = _running.Count == 1 ? card : null;
            Live(() => _vm.CurrentAction = ev.Summary);
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
                // The quick action's own title when the planner has given one - the same name replay
                // puts on this card, so a run reads identically live and from the history.
                var card = new StepCardViewModel(
                    string.IsNullOrWhiteSpace(_liveTitle) ? "Working" : _liveTitle);
                card.SetRunning();
                _cards.Add(card);
                Live(() => _vm.Steps.Add(card));
                _currentCard = card;
                if (_totalSteps == 0)
                    _totalSteps = 1;
            }
            return _currentCard;
        }

        /// <summary>
        /// The run's title, in the window and on the view model both.
        ///
        /// <para>Kept in a field as well, because the view model's copy is EMPTY while the middle column
        /// is showing another workspace - and the title is read while that is true, to name a quick
        /// action's card and the run's row in the column.</para>
        /// </summary>
        private void SetLiveTitle(string title)
        {
            _liveTitle = title;
            Live(() => _vm.TaskTitle = title);
        }

        private string _liveTitle = string.Empty;

        private void UpdateProgress()
        {
            var progress = _totalSteps > 0 ? $"{_doneSteps} / {_totalSteps} steps" : "—";
            Live(() => _vm.StatusProgress = progress);

            // The one place step counts change, so the one place the live row has to be told. The row
            // also carries the planner's title, which arrives after the row does.
            if (_liveRow != Guid.Empty)
                UpdateLive(_liveRow, r => r with
                {
                    Title = _liveTitle,
                    StepsDone = _doneSteps,
                    StepsTotal = _totalSteps
                });
        }

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

        private void AddArtifact(WorkEvent ev)
        {
            // The path as a value. Splitting the summary on its first ": " is a guess about a sentence,
            // and a path is not a sentence.
            var relative = ev.ArtifactPath()
                           ?? (ev.Summary.Contains(": ", StringComparison.Ordinal)
                               ? ev.Summary[(ev.Summary.IndexOf(": ", StringComparison.Ordinal) + 2)..]
                               : ev.Summary);

            // One card per file: repeated writes to the same path say nothing new.
            if (!_shownArtifacts.Add(relative))
                return;

            var root = _currentWorkspaceRoot;

            // "Undo" used to be an unconditional File.Delete: if the agent EDITED an existing source
            // file, pressing it deleted the source outright and reported "undone". The store now keeps
            // what it overwrote, so the button can say which of the two things it will actually do.
            var createdByThisRun = _disk?.CreatedHere(relative) == true;

            // Built once and journalled, not rebuilt on replay: the card carries state of its own -
            // "reverted" is set on it later - and a second object would lose it.
            var item = new ArtifactItemViewModel(
                relative,
                card => ShowFile(root, card),
                card => _ = UndoLiveArtifactAsync(root, card, createdByThisRun),
                createdByThisRun ? "Delete" : "Undo");

            _liveArtifacts.Add(item);
            Live(() => _vm.Artifacts.Add(item));
        }

        /// <summary>
        /// The artifact cards this run has produced. The view model's copy is empty while the middle
        /// column is showing another workspace, and these cards are still written to from there - a
        /// rejected step puts its files back and its cards have to say so.
        /// </summary>
        private readonly List<ArtifactItemViewModel> _liveArtifacts = new();

        /// <summary>
        /// Marks the cards for files a rejected step wrote and the engine put back. The paths come from
        /// the event's own summary ("Rejected work put back: a.md, b.md"), which is not ideal — but the
        /// alternative is a payload schema for one line of text, and the card is cosmetic: the file on
        /// disk has already been restored whatever the card says.
        /// </summary>
        private void MarkRevertedArtifacts(string summary)
        {
            const string marker = "put back: ";
            var index = summary.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
                return;

            var paths = summary[(index + marker.Length)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var item in _liveArtifacts)
            {
                if (!paths.Contains(item.RelativePath, StringComparer.OrdinalIgnoreCase))
                    continue;

                item.Status = "put back — the reviewer rejected this step";
                item.CanAct = false;
            }
        }

        private void AddStagedArtifact()
        {
            if (_staging is null || _stagedShown >= _staging.Changes.Count)
                return;

            var change = _staging.Changes[_stagedShown++];
            var staging = _staging;
            var diff = change.OldByteCount + change.NewByteCount > 1024 * 1024
                ? new[] { $"Large change: {change.OldByteCount:N0} → {change.NewByteCount:N0} bytes. Text diff omitted; Apply preserves the complete content." }
                : change.IsBinary
                    ? new[] { $"Binary file: {change.OldByteCount:N0} → {change.NewByteCount:N0} bytes. Apply preserves the original bytes." }
                    : TextDiff.Unified(change.OldContent, change.NewContent, change.RelativePath)
                        .Replace("\r\n", "\n").Split('\n');

            // Built once, then journalled: the card carries its own applied/rejected state, and
            // rebuilding it on replay would throw that away.
            var staged = new StagedChangeViewModel(
                change.RelativePath,
                change.IsNew,
                diff,
                item =>
                {
                    // Apply can refuse now: if the file changed after the proposal was made, writing it
                    // would erase that edit. The card says so and stays actionable, so the user can look
                    // at the file and decide, instead of finding out afterwards.
                    var result = staging.Apply(change.Id);
                    if (!result.Applied)
                    {
                        item.Status = result.Conflict ?? "could not apply";
                        item.StatusBrush = Brand.Amber;
                        return;
                    }

                    item.Status = "applied";
                    item.StatusBrush = Brand.Success;
                    item.CanAct = false;
                },
                item =>
                {
                    var result = staging.Reject(change.Id);
                    if (!result.Rejected)
                    {
                        item.Status = result.Conflict ?? "could not reject";
                        item.StatusBrush = Brand.Amber;
                        return;
                    }
                    item.Status = "rejected";
                    item.StatusBrush = Brand.Danger;
                    item.CanAct = false;
                });

            Live(() => _vm.Artifacts.Add(staged));
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

        // ── Runs in flight ───────────────────────────────────────────────────────

        /// <summary>
        /// Starts a live row and returns its handle. The run id is not known yet - the orchestrator
        /// mints it and it arrives with the first event - so the row is identified by its own key
        /// until then. See <see cref="LiveRun"/>.
        /// </summary>
        private Guid BeginLive(string title, string workspace, bool headless)
        {
            var row = Guid.NewGuid();
            _live[row] = (new LiveRun(row, Guid.Empty, title, headless, 0, 0, DateTimeOffset.Now), workspace);
            RefreshLive();
            return row;
        }

        /// <summary>Updates a live row, if it is still there. Silent when it is not: a run that has
        /// ended while an event was in flight is not an error to report.</summary>
        private void UpdateLive(Guid row, Func<LiveRun, LiveRun> change)
        {
            if (!_live.TryGetValue(row, out var held))
                return;

            _live[row] = (change(held.Run), held.Workspace);
            RefreshLive();
        }

        private void EndLive(Guid row)
        {
            if (_live.Remove(row))
                RefreshLive();
        }

        /// <summary>
        /// Pushes the live rows for the CURRENT workspace into the column.
        ///
        /// <para>Filtered by workspace here rather than in RunColumn: which folder a run belongs to is
        /// the window's business, and a background run started in one project keeps running after a
        /// switch. Filtered against the history there, which is the rule that has a test.</para>
        /// </summary>
        private void RefreshLive()
        {
            var here = WorkspaceRegistry.Normalise(_vm.WorkspacePath);

            _vm.Runs.ShowRunning(
                RunColumn.Live(
                    _live.Values
                         .Where(e => string.Equals(e.Workspace, here, StringComparison.OrdinalIgnoreCase))
                         .Select(e => e.Run),
                    _vm.Runs.All));
        }

        /// <summary>
        /// Empties the middle column and the tiles beside it: the step feed, the artifacts, the tabs,
        /// the counters, and any past run being read.
        ///
        /// <para>One method, called by both the things that need it - a run starting, and a workspace
        /// switch - because the failure being fixed is precisely that one of them cleared less than the
        /// other. Two lists of what is on screen drift, and what drifts off the list is what stays on
        /// screen belonging to somewhere else.</para>
        /// </summary>
        private void ClearRunView()
        {
            ClearRunPanels();

            // The run's own state, which the panels are only a view of. Cleared HERE and not in
            // ClearRunPanels, because detaching from a run to look at another workspace empties the
            // panels while the run carries on filling these.
            _shownArtifacts.Clear();
            _liveArtifacts.Clear();
            _cards.Clear();
            _currentCard = null;
            _running.Clear();
            _stepIndex = 0;
            _doneSteps = 0;
            _totalSteps = 0;
            _liveTitle = string.Empty;
            _liveJournal.Clear();
            _liveAgent = null;
            _renderedRunId = Guid.Empty;
        }

        /// <summary>The panels only: what is drawn, not what it is drawn from. See ClearRunView.</summary>
        private void ClearRunPanels()
        {
            _vm.ShowLiveRun();
            _vm.HasTask = false;
            _vm.TaskIntent = string.Empty;
            _vm.TaskTitle = string.Empty;

            _vm.Steps.Clear();
            _vm.Artifacts.Clear();

            _vm.ToolCalls = 0;
            _vm.ResetUsage();
            _vm.Routing.Clear();
            _vm.SelectedTab = 0;
            if (_pendingDecision is null) _vm.IsDecisionVisible = false;
            _vm.IsAgentVisible = false;

            _vm.StatusPhase = "Idle";
            _vm.StatusProgress = "—";
            _vm.StatusElapsed = "—";
            _vm.CurrentAction = string.Empty;
        }

        private void SetLiveAgent(string name, IBrush color)
        {
            // Keep only badge transitions, including while another workspace is visible.
            if (_liveAgent == name) return;
            _liveAgent = name;
            Live(() => _vm.SetAgent(name, color));
        }

        /// <summary>
        /// Records a UI update for replay and applies it while this run is visible.
        /// </summary>
        private void Live(Action write)
        {
            _liveJournal.Add(write);

            if (_liveAttached)
                write();
        }

        /// <summary>
        /// Stops showing the live run without stopping it. The run keeps going, keeps its row in its
        /// own workspace's RUNNING block, and keeps journalling.
        /// </summary>
        private void DetachLive()
        {
            if (!_liveAttached)
                return;

            _liveAttached = false;
            ClearRunPanels();
        }

        /// <summary>
        /// Shows it again, as it stands now: the panels are emptied and the journal replayed. The step
        /// cards are the same objects the run has been updating all along, so what comes back is
        /// current rather than a picture of the moment you left.
        /// </summary>
        private void AttachLive()
        {
            if (_liveAttached)
                return;

            _liveAttached = true;
            ClearRunPanels();

            foreach (var write in _liveJournal)
                write();

            // Not journalled: it would be one entry per second of a run nobody was watching, all but
            // the last of them wrong by the time they replayed.
            if (_runStopwatch.IsRunning)
                _vm.StatusElapsed = FormatElapsed(_runStopwatch.Elapsed);
        }

        /// <summary>
        /// The run list is frozen only where the run actually is. IsBusy holds the whole app - there is
        /// one foreground run - but the workspace on screen may not be the one it is happening in.
        /// </summary>
        private void RefreshColumnState()
            => _vm.IsColumnIdle = !(_vm.IsBusy && IsLiveWorkspace);

        private bool IsLiveWorkspace
            => _liveWorkspace.Length > 0
               && string.Equals(
                   WorkspaceRegistry.Normalise(_vm.WorkspacePath), _liveWorkspace,
                   StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Switching a workspace re-scopes everything the window shows - runs, memory, the inbox badge,
        /// artifacts - because every store is built from the workspace it is asked about. It does not
        /// touch the folder, and it does not start anything.
        ///
        /// <para>The middle column is re-scoped here too, which it was not: it kept the previous
        /// workspace's execution feed and tiles under the new workspace's name. It is emptied, and then
        /// filled again only with what THIS workspace was left reading - see OpenRunMemory for why that
        /// is the rule rather than "show the newest run".</para>
        /// </summary>
        private void SwitchWorkspace(string path)
        {
            if (_shuttingDown) return;
            var next = WorkspaceRegistry.Normalise(path);

            // Switching to the workspace already open is not a switch. Said before anything is cleared:
            // otherwise clicking the current workspace would empty the middle column, and the person
            // would have lost what they were reading by pressing the thing they were already on.
            if (string.Equals(next, WorkspaceRegistry.Normalise(_vm.WorkspacePath), StringComparison.OrdinalIgnoreCase))
                return;

            // Asked before anything moves, and asked of the window rather than remembered as it went:
            // a person who opened a run and then pressed Back has nothing open, and this cannot get
            // that wrong.
            _openRuns.Leaving(_vm.WorkspacePath.Trim(), _vm.PastRun?.Record.RunId);

            // Leaving the run's own workspace detaches from it; leaving anywhere else just empties the
            // panels. Neither touches the run: it keeps going, keeps its row in the RUNNING block of
            // the workspace it belongs to, and keeps journalling what it draws.
            //
            // ClearRunView - which also throws away the step cards and the counters - is for a run
            // STARTING, and is not used here. Calling it on a switch would destroy the state of a run
            // that is still running, which is the whole thing this indirection exists to prevent.
            if (IsLiveWorkspace)
                DetachLive();
            else
                ClearRunPanels();

            // What the workspace being entered was left reading, handed over BEFORE the path changes:
            // assigning the path is what starts its run list loading, and the list arriving is when
            // there is a row to select. Null for a workspace nobody has opened a run in, which is a
            // fresh start and every workspace at launch.
            _vm.Runs.Reopen = _openRuns.Entering(next);

            _vm.WorkspacePath = next;
            _registry.Touch(_vm.WorkspacePath);
            RefreshWorkspaces();
            ApplyWorkspaceDefaults();
            RefreshInboxButton();

            // Arriving in the run's own workspace puts its feed back, as it stands now. A past run this
            // workspace was left reading opens OVER it a moment later, when the list arrives - which is
            // the same order as reading history during a run, and Back returns to the feed underneath.
            if (IsLiveWorkspace)
                AttachLive();

            RefreshColumnState();
            RefreshLive();
            // A library left open across the switch would go on offering the previous project's
            // templates for a project that has never heard of them.
            _templatesWindow?.FollowWorkspace(WorkspaceRootOrNull(), PolicyFor(_vm.AutonomyTier));

            // And a schedules window left open would offer Delete and Disable for schedules belonging
            // to a project it is no longer looking at.
            if (WorkspaceRootOrNull() is { } schedulesRoot)
                _schedulesWindow?.FollowWorkspace(schedulesRoot);
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
            if (_shuttingDown) return;
            var path = _vm.WorkspacePath.Trim();
            if (path.Length == 0)
                return;

            _registry.SaveSettings(path, _vm.AutonomyTier, CurrentWorkerRole(), _vm.StageChanges);
        }

        /// <summary>
        /// Loads a workspace's saved setup into the window. Guarded, or every load would look like an
        /// edit and write itself back onto whichever workspace was current a moment ago.
        /// </summary>
        private Task _workspacePreparation = Task.CompletedTask;

        private void ApplyWorkspaceDefaults()
        {
            // Opening a workspace is where its working area comes into being, so that a person can
            // see '.enactive/scratch/' in their own file manager before any run, and so that the
            // engine's folder is on the project's .gitignore if the project keeps one. Idempotent,
            // and never able to fail: a workspace that cannot be prepared is still a workspace.
            var preparingRoot = _vm.WorkspacePath.Trim();
            _workspacePreparation = _workspacePreparation.ContinueWith(_ => WorkspaceSetup.Prepare(preparingRoot),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

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


        /// <summary>
        /// Reads the log with the model bound to Review, falling back to the one the worker runs on.
        ///
        /// <para>Review because that is the phase already chosen for judging evidence rather than
        /// producing work, and reading a log is exactly that; the worker's model because a build with no
        /// Review binding still has one model that is known to work. Null when neither exists, and then
        /// the button says so instead of failing when it is pressed.</para>
        /// </summary>
        private Func<string, CancellationToken, Task<LogAnalysisResult>>? LogAnalysis()
        {
            // No engine, no analysis - and the button already says so when this returns null, which is
            // the behaviour a build with no Review binding has always had.
            if (_engineProblem is not null)
                return null;

            var worker = _workerProvider.Get(CurrentWorkerRole());
            var reference = BuildRouter().Resolve(ModelPurpose.Review, worker) ?? worker?.ModelPolicy.Preferred;
            if (reference is null)
                return null;

            // How much of the log can be sent, from the provider that is actually going to read it.
            // It used to be _settings.NumCtx unconditionally — an OLLAMA setting, applied to whatever
            // model the Review binding points at. With Review on a cloud model and num_ctx left blank
            // the analyst assumed 16,000 tokens and sent about a seventh of what a 200,000-token
            // window would have taken; with num_ctx set for a local model it sent that model's window
            // to Claude. Neither is a fact about the provider doing the work, and nothing can ask it —
            // so it is declared per provider, and num_ctx is the fallback only because for Ollama it
            // IS the window.
            var declared = _settings.Providers
                .FirstOrDefault(p => string.Equals(p.Id, reference.ProviderId, StringComparison.Ordinal))
                ?.ContextWindowTokens;

            // promptBodies: false — this prompt CARRIES the log, and the provider decorator would write
            // it straight back into it. One analysis of a 10,429-line run added 4,785 lines; the second
            // then read a log that was half its own previous prompt. See LoggingChatProvider.
            return async (text, ct) => await new LogAnalyst().AnalyseAsync(
                text, _providerFactory.Create(reference.ProviderId, promptBodies: false),
                reference.Model, declared ?? _settings.NumCtx, ct);
        }

        private void ShowLogWindow()
        {
            // The owner is passed on the FIRST show and only then, the same shape ShowTemplates uses:
            // CenterOwner has nothing to centre on without one, so the window opened wherever the
            // window manager put it — on a multi-monitor desk, often not the screen the application is
            // on. Re-showing the same instance keeps the owner it was given.
            if (_logWindow is null)
            {
                _logWindow = new LogWindow(_log, LogAnalysis());
                _logWindow.Closed += (_, _) => _logWindow = null;
                _logWindow.Show(this);
            }
            else
            {
                _logWindow.Show();
            }

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
                // Looking at the environment is a read, so this only READS the workspace's id.
                var ws = WorkspaceInfo.For(root);
                var (info, snapshot) = await _envProbe.ProbeFullAsync(ws, CancellationToken.None);
                _vm.EnvironmentSummary = info.OneLine();
                ShowViewer("Environment", info.Summary() + "\n\n" + snapshot.Describe());
            }
            catch (Exception ex)
            {
                ShowViewer("Environment", "Probe failed: " + ex.Message);
            }
        }

        // The phase->model router, from the shared composition. A scheduled run built its own and was
        // never given any bindings at all, so planning bound here to Anthropic ran on the local model
        // and nothing said so.
        private IModelRouter BuildRouter() => EngineComposition.Router(_settings, _modelResolver);

        /// <summary>
        /// The run's setup as it is RIGHT NOW, for the record. Read once at the start, because by the
        /// time anyone opens the run to ask what it was allowed to do, the slider will have moved.
        /// </summary>
        private RunSettings CurrentRunSettings()
            => new(_vm.AutonomyTier, MainWindowViewModel.LevelName(_vm.AutonomyTier), CurrentWorkerRole(), _vm.StageChanges);

        /// <summary>
        /// Everything an unattended run needs from this window, read while we are still on the UI
        /// thread and then handed over as a frozen thing.
        ///
        /// <para>Read here rather than inside the run for the same reason
        /// <see cref="CurrentRunSettings"/> is: a run started now and finishing in ten minutes must be
        /// governed by the autonomy level it was started under, not by wherever the slider has since
        /// been dragged. The MCP configurations are cloned for the same reason - the settings dialog
        /// edits the live ones.</para>
        /// </summary>
        /// <param name="autonomy">
        /// WHOSE autonomy, which is the whole reason this is a parameter. The slider on screen is about
        /// the folder on screen; a task from a phone names a folder of its own and must be governed by
        /// the level saved against THAT one. Reading the slider for both meant a remote task running at
        /// whatever permission an unrelated project happened to be sitting at.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// When no model is configured, so there is no engine to snapshot. The two callers are the
        /// background run — stopped earlier, by RunAsync — and a task started from a phone, which has
        /// no earlier gate and would otherwise reach a null provider. A named refusal is what the panel
        /// can report; a NullReferenceException is not.
        /// </exception>
        private RunEnvironment SnapshotEnvironment(int autonomy, string? workerRole, bool stageChanges)
            => _engineProblem is { Length: > 0 } problem
                ? throw new InvalidOperationException(problem)
                : new(
                _providerFactory, _modelResolver, _workerProvider, _toolRegistry,
                _settings.McpServers.Select(c => c.Clone()).ToArray(),
                _planner, _permissionEngine, BuildRouter(), _log, _settings,
                PolicyFor(autonomy),
                new RunSettings(
                    autonomy, MainWindowViewModel.LevelName(autonomy), workerRole, stageChanges),
                WorkerIdForRole(workerRole));

        /// <summary>
        /// The worker a saved ROLE NAME refers to, or null for the default.
        ///
        /// <para>The registry stores the role, not the id - by name on purpose, because the worker list
        /// is editable and an index would quietly select somebody else the first time a role was
        /// added. The two lists are built together, so the position of a role is the position of its
        /// worker.</para>
        /// </summary>
        private string? WorkerIdForRole(string? role)
        {
            if (role is null)
                return null;

            var index = _vm.WorkerRoles.IndexOf(role);

            return index >= 0 && index < _workerProvider.All.Count ? _workerProvider.All[index].Id : null;
        }

        /// <summary>The role the worker box is on, by name - null when there are no roles to pick from.</summary>
        private string? CurrentWorkerRole()
            => _vm.SelectedWorkerIndex >= 0 && _vm.SelectedWorkerIndex < _vm.WorkerRoles.Count
                ? _vm.WorkerRoles[_vm.SelectedWorkerIndex]
                : null;

        /// <summary>
        /// The workspace at a path, for READING what has been recorded about it — the run list, the
        /// timeline, the inbox. It resolves the id the folder carries and writes nothing: opening a
        /// history should not put a file in somebody's project.
        /// </summary>
        private static WorkspaceInfo WorkspaceFrom(string path) => WorkspaceInfo.For(path);

        private void StartBackground(string text, string fullPath)
        {
            if (_shuttingDown) return;
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

            // Adopt: a background run is a run, and the folder is being taken up as a workspace here
            // exactly as it is in the foreground.
            var workspace = WorkspaceInfo.Adopt(fullPath);
            // The slider on screen, and rightly: this run is against the folder on screen.
            var environment = SnapshotEnvironment(_vm.AutonomyTier, CurrentWorkerRole(), _vm.StageChanges);
            var inbox = InboxStoreFactory.Create(workspace);
            _registry.Touch(fullPath);
            RefreshWorkspaces();

            var title = Summarise(text);
            // A background launch has its own row; it must not overwrite the foreground header.
            if (!_vm.IsBusy)
            {
                _vm.StatusPhase = "Background task started";
                _vm.CurrentAction = text;
                _vm.TaskIntent = text;
                _vm.TaskTitle = title;
                _vm.HasTask = true;
                _vm.ShowLiveRun();
            }
            _vm.InputText = string.Empty;

            // A row of its own, carrying the workspace it belongs to: the window is free again the
            // moment this starts, so this row is the only thing on screen that says it is happening.
            // It never learns a run id - the events go to the Inbox writer, not here - so it is removed
            // by this method and by nothing else.
            var row = BeginLive(title, fullPath, headless: true);

            var execution = _background.TryStart(async ct =>
            {
                try
                {
                    var composed = await UnattendedRun.ComposeAsync(
                        environment, workspace, text, IntentSource.Inbox,
                        new BackgroundDecisionHandler(inbox, workspace), ct);

                    await using (composed.Resources)
                    {
                        await BackgroundRunner.RunAsync(
                            composed.Engine.SubmitIntentAsync(composed.Intent, ct),
                            inbox, workspace, text, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Composition can be cancelled before the recorder ever receives a run id.
                    var line = InboxLines.For("Cancelled", 0, 0, null);
                    await inbox.AppendAsync(new InboxItem(Guid.NewGuid(), workspace.Id, line.Kind, text,
                        line.Summary, Guid.Empty, "unread", DateTimeOffset.UtcNow), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    await inbox.AppendAsync(new InboxItem(Guid.NewGuid(), workspace.Id, "error", text,
                        "Failed to start: " + ex.Message, Guid.Empty, "unread", DateTimeOffset.UtcNow), CancellationToken.None);
                }
                finally
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        EndLive(row);
                        if (!_shuttingDown) RefreshInboxButton();
                        // Refresh only the workspace currently on screen, while the app stays open.
                        if (!_shuttingDown && string.Equals(
                                WorkspaceRegistry.Normalise(_vm.WorkspacePath), fullPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            _ = LoadRunsAsync();
                        }
                    });
                }
            });
            if (execution is null) EndLive(row);
            else _ = ObserveBackgroundAsync(execution);
        }

    private async Task ObserveBackgroundAsync(Task execution)
    {
        try { await execution; }
        catch (Exception ex)
        {
            if (!_forceClose) _log.Error(LogSource.System, "Background cleanup or recording failed: " + ex.Message);
        }
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
    /// Opens the run a row stands for. The row holds only the HEADER, so this is where the
    /// transcript is read - by id, for the one run that was clicked, rather than for every run in
    /// the list on the way to drawing it.
    ///
    /// <para>A run that is gone by the time it is opened says so and leaves the view alone. It is a
    /// real case: another window, or another copy of the app, can have deleted it since the list was
    /// drawn.</para>
    /// </summary>
    private async Task OpenPastRunAsync(RunSummary summary)
    {
        var path = _vm.WorkspacePath.Trim();
        if (string.IsNullOrEmpty(path))
            return;

        RunRecord? record;
        try
        {
            record = await RunStoreFactory.Create(WorkspaceFrom(path))
                .LoadAsync(summary.RunId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _vm.Runs.Fail(ex.Message);
            return;
        }

        if (record is null)
        {
            _vm.Runs.Fail($"That run is no longer in the store: {RunTitle.For(summary)}");
            return;
        }

        _vm.ShowPastRun(BuildPastRun(record));
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

        var past = new PastRunViewModel(
            record,
            item => ShowFile(root, item),
            item => _ = DeletePastArtifactAsync(root, item),
            ReachOf,
            RunHistory.AttemptsOf(_vm.Runs.All, record),
            // Looked up lazily: whether a template is still there is a fact about NOW, and building
            // a past run should not be the moment the library is read from disk if nobody asks.
            () => record.Spec is null
                ? null
                : ResolvedTaskSpec.Parse(record.Spec) is { } s
                    ? new TemplateStore(root).Find(s.TemplateId)
                    : null);

        past.RetryRequested += Retry;
        past.RunAgainRequested += RunAgain;
        return past;
    }

    /// <summary>
    /// The same run again, exactly as it was: the specification it recorded, or - for a run that was
    /// typed rather than started from a template - the request it recorded.
    ///
    /// <para>It keeps the task id, which is the whole point of M4: these are attempts at ONE task,
    /// not unrelated runs that happen to say the same thing.</para>
    /// </summary>
    private void Retry(PastRunViewModel past)
    {
        var taskId = past.Record.TaskId == Guid.Empty ? Guid.NewGuid() : past.Record.TaskId;

        if (past.Spec is { } spec)
        {
            _vm.InputText = spec.Goal;
            _ = RunAsync(background: false, spec, taskId);
            return;
        }

        if (past.Request is { Length: > 0 } request)
        {
            _vm.InputText = request;
            _ = RunAsync(background: false, spec: null, taskId);
        }
    }

    /// <summary>
    /// The template as it stands NOW, with the answers this run used. The difference from Retry is
    /// the whole reason both exist: one repeats what happened, the other asks the current version of
    /// the same question.
    /// </summary>
    private void RunAgain(PastRunViewModel past)
    {
        if (past.Spec is not { } old)
            return;

        var root = WorkspaceRegistry.Normalise(_vm.WorkspacePath);
        if (new TemplateStore(root).Find(old.TemplateId) is not { } template)
            return;

        var workspace = WorkspaceInfo.For(root);
        var resolved = TemplateResolution.Resolve(
            template, workspace, PolicyFor(_vm.AutonomyTier), old.Parameters);

        if (resolved.Spec is not { } spec)
        {
            // The template has changed under it - a new required parameter, a value its type no
            // longer accepts. Say which, rather than doing nothing when a button is pressed.
            _vm.StatusPhase = "Error";
            _vm.CurrentAction =
                $"'{template.Name}' has changed since this run: "
                + string.Join("; ", resolved.Problems.Select(p => p.Message))
                + " Open Templates and fill it in again.";
            return;
        }

        var taskId = past.Record.TaskId == Guid.Empty ? Guid.NewGuid() : past.Record.TaskId;
        _vm.InputText = spec.Goal;
        _ = RunAsync(background: false, spec, taskId);
    }

    /// <summary>Opens an artifact in the viewer. Reading is safe at any age.</summary>
    private async void ShowFile(string root, ArtifactItemViewModel item)
    {
        try
        {
            var full = Path.Combine(root, item.RelativePath);

            // The PATH goes with the text, because it is what decides how the file is shown -
            // see ArtifactViewerCatalog. A run's deliverable is often one Markdown document, and
            // every one of them used to arrive as monospaced source that does not wrap.
            ViewerWindow.Show(
                this, item.RelativePath,
                await File.ReadAllTextAsync(full),
                item.RelativePath);
        }
        catch (Exception ex)
        {
            ShowViewer(item.RelativePath, "Error: " + ex.Message);
        }
    }

    /// <summary>
    /// Undoes what the CURRENT run did to a file: a file it created is removed, a file it overwrote
    /// is restored from the copy the store took first. Both refuse when the file has changed since,
    /// because at that point undoing would throw away an edit the run did not make.
    /// </summary>
    private async Task UndoLiveArtifactAsync(string root, ArtifactItemViewModel item, bool createdByThisRun)
    {
        if (_disk is null)
        {
            item.Status = "this run cannot be undone";
            item.CanAct = false;
            return;
        }

        // Removing a generated file is cheap to redo; replacing the file the user has been looking
        // at is not, so only that one asks.
        if (!createdByThisRun)
        {
            var go = await ConfirmWindow.AskAsync(
                this,
                $"Restore the previous version of “{item.RelativePath}”?",
                "The file as this run left it will be replaced by the version that was there before "
                + "the run started.",
                "Restore",
                "Keep");

            if (!go)
                return;
        }

        var result = _disk.Undo(item.RelativePath);
        if (!result.Undone)
        {
            // A conflict is not an error to swallow: the user needs to know the file moved on.
            item.Status = result.Conflict ?? "could not undo";
            return;
        }

        item.Status = result.Restored ? "previous version restored" : "deleted (created by this run)";
        item.CanAct = false;
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
    /// Forgets one run, after asking.
    ///
    /// <para>What goes is the RECORD - what was asked, what the engine did, and the paths it wrote.
    /// The files themselves stay exactly where they are: they are the user's work sitting in their
    /// workspace, and somebody pruning a list of old runs is tidying a list, not asking for their
    /// code back. The dialog says so, because a delete that is vaguer than what it does gets
    /// answered by guessing.</para>
    /// </summary>
    /// <summary>
    /// Forgets every run the list is currently showing.
    ///
    /// <para>The filter above the list is what decides the set, so the button destroys exactly what
    /// is on screen and nothing that is not. The confirmation names the COUNT and the filter,
    /// because "delete these" is the one phrasing where the person's idea of "these" and the
    /// program's have to be the same thing.</para>
    ///
    /// <para>One failure does not stop the rest. A store that refuses one row - a file locked, a
    /// record already gone - would otherwise leave a bulk delete half done with no way to tell how
    /// far it got, so the ones that failed are counted and reported.</para>
    /// </summary>
    private async Task DeleteRunsAsync(IReadOnlyList<RunSummary> runs)
    {
        if (runs.Count == 0)
            return;

        var artifacts = runs.Sum(r => r.Artifacts.Count);

        if (!await ConfirmWindow.AskAsync(
                this,
                $"Delete {runs.Count} run{(runs.Count == 1 ? "" : "s")}?",
                "Their history goes: what was asked, every step, and every event behind it. "
                + "This cannot be undone."
                + (artifacts > 0
                    ? $" The {artifacts} file(s) they wrote stay in your workspace — only the record "
                      + "of them goes."
                    : ""),
                $"Delete {runs.Count}", "Keep"))
            return;

        var path = _vm.WorkspacePath.Trim();
        if (string.IsNullOrEmpty(path))
            return;

        var store = RunStoreFactory.Create(WorkspaceFrom(path));
        var refused = 0;

        foreach (var run in runs)
        {
            try
            {
                await store.DeleteAsync(run.RunId, CancellationToken.None);
            }
            catch (Exception)
            {
                refused++;
            }
        }

        // Not something to come back to, in this workspace or any other.
        foreach (var run in runs)
            _openRuns.Forget(run.RunId);

        if (_vm.PastRun is { } open && runs.Any(r => r.RunId == open.Record.RunId))
            _vm.ShowLiveRun();

        await LoadRunsAsync();

        if (refused > 0)
            _vm.Runs.Fail($"{refused} of {runs.Count} could not be deleted; the rest are gone.");
    }

    private async Task DeleteRunAsync(RunSummary record)
    {
        var artifacts = record.Artifacts.Count;
        if (!await ConfirmWindow.AskAsync(
                this,
                $"Delete the run “{RunTitle.For(record)}”?",
                // It used to name the event count. The list holds headers now, so that number is not
                // known here - and reading the whole run to put a number in a warning about deleting
                // it would be an odd thing to spend a transcript on.
                "Its history goes: what was asked, every step, and every event behind it. "
                + "This cannot be undone."
                + (artifacts > 0
                    ? $" The {artifacts} file(s) it wrote stay in your workspace — only the record of "
                      + "them goes."
                    : ""),
                "Delete", "Keep"))
            return;

        var path = _vm.WorkspacePath.Trim();
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            await RunStoreFactory.Create(WorkspaceFrom(path)).DeleteAsync(record.RunId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Said out loud and the list left alone. A row that vanishes from the screen while the
            // record is still in the store is a lie the next Refresh exposes.
            _vm.Runs.Fail("Could not delete that run: " + ex.Message);
            return;
        }

        _openRuns.Forget(record.RunId);

        // Reading a run that no longer exists is worse than being sent back to the live one.
        if (_vm.PastRun?.Record.RunId == record.RunId)
            _vm.ShowLiveRun();

        await LoadRunsAsync();
    }

    /// <summary>
    /// Applies the workspace's run-retention setting, and returns what is left.
    ///
    /// <para>Off by default, and a COUNT rather than an age. What makes this list unusable is how
    /// many rows are in it, and a week of heavy use puts more in it than a month of light use - an
    /// age-based rule would leave the busy workspace, the one with the problem, untouched.</para>
    ///
    /// <para>A failure here is swallowed on purpose. Housekeeping that cannot run is not a reason
    /// to fail the thing the person actually asked for, which was to see their runs.</para>
    /// </summary>
    private async Task<IReadOnlyList<RunSummary>> TrimAsync(
        IRunStore store, IReadOnlyList<RunSummary> records)
    {
        var keep = _settings.KeepRuns;
        var doomed = RunHousekeeping.BeyondTheNewest(records, keep);

        if (doomed.Count == 0)
            return records;

        foreach (var run in doomed)
        {
            try
            {
                await store.DeleteAsync(run.RunId, CancellationToken.None);
            }
            catch (Exception)
            {
                // Left in the list rather than hidden: a row that is still in the store belongs on
                // screen, and the next load will try again.
                return records;
            }
        }

        return records.Except(doomed).ToArray();
    }

    /// <summary>
    /// Loads the workspace's runs for the context column. The store type is the environment's
    /// choice (SQLite, MySQL or files), which is why the list does not create one itself.
    ///
    /// <para>Summaries, not records. The column shows six fields per row; it used to fetch every
    /// event of every run in the workspace to fill them, which is a cost that grows with how much
    /// work has been done here and is paid on every Refresh.</para>
    /// </summary>
    private async Task LoadRunsAsync()
    {
        var path = _vm.WorkspacePath.Trim();
        if (string.IsNullOrEmpty(path))
        {
            _vm.Runs.Show(Array.Empty<RunSummary>());
            _vm.Runs.ShowResumable(Array.Empty<RunCheckpoint>());
            return;
        }

        var workspace = WorkspaceFrom(path);

        // Interrupted runs first, and in their own try: a checkpoint folder that cannot be read must
        // not cost the history list, which is the thing somebody opened this column for.
        try
        {
            _vm.Runs.ShowResumable(
                await new JsonCheckpointStore(workspace).LoadAllAsync(CancellationToken.None));
        }
        catch (Exception)
        {
            _vm.Runs.ShowResumable(Array.Empty<RunCheckpoint>());
        }

        try
        {
            var store = RunStoreFactory.Create(workspace);
            var records = await store.LoadSummariesAsync(CancellationToken.None);

            // Retention, applied where the list is read rather than on a timer: this is the moment
            // the number of runs matters, and a background trimmer would be deleting somebody's
            // history while they were not looking at it.
            records = await TrimAsync(store, records);

            _vm.Runs.Show(records);
            // The history has just changed, and a live row whose run is now IN it must go. See
            // RunColumn.Live: the window learns a run ended by two paths and they arrive in
            // whatever order they arrive in.
            RefreshLive();
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
        var runs = await runStore.LoadSummariesAsync(CancellationToken.None);
        var entries = await memory.LoadAllAsync(CancellationToken.None);
        ShowViewer("Project Memory", ProjectMemory.Render(runs, entries, workspace.RootPath));
    }

    // ── IDecisionHandler: inline approval card ───────────────────────────────
    public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
        => _decisionQueue.RequestAsync(request, ct, ShowDecisionAsync);

    private async Task<DecisionOutcome> ShowDecisionAsync(DecisionRequest request, CancellationToken ct)
    {
        var root = request.Action?.WorkingDirectory;
        // Already approved for this session or this workspace? Allow silently — no click needed.
        if (!request.RequiresExplicitAnswer && !string.IsNullOrEmpty(request.Subject))
        {
            // Which one answered is carried back, so the timeline can say so. The two used to be
            // one line and one millisecond apart.
            if (_sessionApprovals.Approves(request))
                return new DecisionOutcome(AllowOptionId(request), "remembered for this session and workspace");

            if (request.MayBeRemembered && root is not null && ApprovalStore.Default.Approves(root, request.Subject))
                return new DecisionOutcome(AllowOptionId(request), "remembered for this workspace");
        }

        var tcs = new DecisionCompletion();

        // Cancelling used to complete the task and stop there: the card stayed on screen and
        // _pendingDecision stayed set, so the NEXT run returned immediately from RunAsync because
        // "a decision is pending" — a decision belonging to a task that had already been stopped.
        // The lifetime of the card is now tied to the lifetime of the request, in one place.
        using var registration = ct.Register(() => tcs.Cancel(ct));
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Cancelled before the post ran: do not put a card on screen for a dead request.
                if (tcs.Task.IsCompleted)
                    return;

                _pendingDecision = tcs;

                // Each button belongs to this request, independently of the selected workspace.
                var options = new List<DecisionOptionViewModel>();
                foreach (var option in request.Options)
                {
                    var captured = option;
                    options.Add(new DecisionOptionViewModel(captured.Label, () => ResolveDecision(tcs, captured.Id)));
                }

                // Remember-this-approval shortcuts, so the user is not clicking Allow for every command.
                if (!request.RequiresExplicitAnswer && !string.IsNullOrEmpty(request.Subject) && root is not null)
                {
                    var subject = request.Subject;
                    var allowId = AllowOptionId(request);
                    options.Add(new DecisionOptionViewModel(
                        "Allow (session)", () => ResolveDecision(tcs, allowId, () => _sessionApprovals.Remember(request))));

                    // Not offered for a shell: that approval would outlive the process, and what it
                    // grants is arbitrary command execution rather than one named action.
                    if (request.MayBeRemembered)
                        options.Add(new DecisionOptionViewModel(
                            "Allow (workspace)", () => ResolveDecision(tcs, allowId, () => ApprovalStore.Default.Approve(root, subject))));
                }

                {
                    _vm.DecisionText = root is null ? request.Topic : $"{request.Topic} — {root} (run {request.Action!.RunId})";
                    // The full action, not the summary: this is what the click authorises.
                    _vm.DecisionDetail = request.FullText;
                    _vm.DecisionOptions.Clear();
                    foreach (var option in options)
                        _vm.DecisionOptions.Add(option);
                    _vm.IsDecisionVisible = true;
                }

            });

            return await tcs.Task.ConfigureAwait(false);
            }
            finally { await Dispatcher.UIThread.InvokeAsync(() => ClearDecision(tcs)); }
        }

        private void ResolveDecision(DecisionCompletion tcs, string optionId, Action? remember = null)
        {
            if (!ReferenceEquals(_pendingDecision, tcs)) return;
            tcs.Resolve(optionId, remember);
            ClearDecision(tcs);
        }

        /// <summary>
        /// Takes THIS request's card off screen. Checking which request it belongs to matters: by the
        /// time a cancellation is dispatched, a later run may already have put its own card up, and
        /// clearing that one would leave the new run waiting on something the user can no longer answer.
        /// </summary>
        private void ClearDecision(DecisionCompletion? tcs)
        {
            if (tcs is not null && !ReferenceEquals(_pendingDecision, tcs))
                return;

            _pendingDecision = null;


            {
                _vm.IsDecisionVisible = false;
                _vm.DecisionOptions.Clear();
                _vm.DecisionText = string.Empty;
                _vm.DecisionDetail = string.Empty;
            }
        }

        private static string AllowOptionId(DecisionRequest request)
            => request.RecommendedOptionId
            ?? request.Options.FirstOrDefault(o => o.Id.Contains("allow", StringComparison.OrdinalIgnoreCase))?.Id
            ?? request.Options.FirstOrDefault()?.Id
            ?? "allow";

        // Workspace-scoped approvals live OUTSIDE the workspace — see ApprovalStore in Core, which is
        // where the rules are and where they are tested. This window passes the FOLDER and decides
        // nothing: which key an approval is filed under, whether a shell may ever be remembered, and
        // whether a legacy in-workspace file counts are all answered in there.
        //
        // They used to be answered here, correctly, in a WinExe no test project references.
        /// <summary>
        /// Where a provider runs, by its id. The engine records WHICH provider produced each turn and
        /// deliberately stops there — it has no idea of a provider's address. This is the only place
        /// that does, because addresses are settings.
        ///
        /// A provider the settings no longer contain is Unknown, not Cloud: a deleted or renamed
        /// provider is a gap in what we know, and filing it under "cloud" would put made-up numbers next
        /// to real ones. Same rule as the token tile's em dash.
        /// </summary>
        private ModelWorkSplit.Reach ReachOf(string? providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return ModelWorkSplit.Reach.Unknown;

            var provider = _settings.Providers.FirstOrDefault(
                p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
                return ModelWorkSplit.Reach.Unknown;

            return Enactive.Settings.ProviderReach.Local(provider.BaseUrl)
                ? ModelWorkSplit.Reach.Local
                : ModelWorkSplit.Reach.Cloud;
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

        /// <summary>
        /// Applies the loaded settings, falling back to defaults if they cannot be built. Returns what
        /// went wrong, or null. The saved file is left exactly as it is: overwriting it with defaults
        /// would destroy the configuration the user is about to fix.
        /// </summary>
        private string? TryApplySettings()
        {
            try
            {
                ApplySettings();
                return null;
            }
            catch (Exception ex)
            {
                try
                {
                    _settings = new AppSettings();
                    _settings.BlockAutomaticSave();
                    ApplySettings();
                }
                catch
                {
                    // Defaults themselves failing is not something to paper over.
                    throw;
                }

                return ex.Message;
            }
        }

        /// <summary>
        /// The tools this host offers, built from the CURRENT settings.
        ///
        /// <para>It used to be a list inline in the constructor, which made every setting a tool reads
        /// a restart-only setting. Reported 2026-09-22: an SMTP account filled in and saved, and
        /// <c>send_email</c> still telling the agent it was unavailable, because the account it holds
        /// was read once when the window opened. Nothing about that is particular to mail - any tool
        /// taking configuration would have behaved the same way - so the registry is rebuilt wherever
        /// the settings are applied, and the pane no longer has to tell anybody to restart.</para>
        ///
        /// <para>Safe to swap while the application is running: <c>_toolRegistry</c> is read at the
        /// point of use, and a run already in flight holds the registry it started with.</para>
        /// </summary>
        private IToolRegistry BuildToolRegistry()
            => new ToolRegistry(BuiltInTools.Create(EngineComposition.Mail(_settings)));

        private void ApplySettings()
        {
            _globalInstructions = _settings.GlobalInstructions;

            // Before the early return below: a tool's configuration is not the engine's, and an SMTP
            // account saved on a machine with no model chosen should still reach the tool.
            _toolRegistry = BuildToolRegistry();

            // Nothing to build an engine out of is a STATE, not an error. A machine where nobody has
            // chosen a model now says so — where it used to be silently configured for a model name
            // compiled into AppSettings, and the first anyone heard of it was a 404. The window opens
            // either way; the run paths read this.
            _engineProblem = EngineComposition.Missing(_settings) is { Count: > 0 } missing
                ? string.Join(" ", missing)
                : null;

            if (_engineProblem is not null)
            {

                _vm.WorkerRoles.Clear();
                return;
            }

            // The engine itself — providers, team, router — from the composition every host shares.
            // It was built inline here, which is why nothing could check it and why the console's own
            // version had drifted onto a different provider kind and a model nobody had installed.
            var engine = EngineComposition.Build(_settings, _http, _log);
            _providerFactory = engine.Providers;
            _providerFactory.MetricsReported = metrics => Dispatcher.UIThread.Post(() => _vm.Performance.Add(metrics));
            _workerProvider = engine.Workers;

            // Applied here rather than at construction because the sink predates the settings. The
            // setter prunes, so lowering it takes effect on Save instead of at the next midnight.
            _logFile.RetentionDays = _settings.LogRetentionDays;

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

    }

    /// <summary>
    /// The tier as a policy, with the shell setting on top — from the shared composition.
    ///
    /// <para>This window used to hold both halves itself: its own copy of the tier table, plus the
    /// shell rule applied to it. The console had neither, so a run started here and the same
    /// workspace run from a command line or a schedule did not agree about what was permitted, and
    /// "never run commands" was a setting that only held in one host. Both halves now live in
    /// <see cref="EngineComposition"/>, where a test can reach them.</para>
    /// </summary>
    private PermissionPolicy PolicyFor(int level) => EngineComposition.PolicyFor(_settings, level);

    private sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
