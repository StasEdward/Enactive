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
using Avalonia.VisualTree;
using Enactive.Application.Projections;
using Enactive.Application;
using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Context;
using Enactive.Core.Diagnostics;
using Enactive.App.Ui.ViewModels;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Enactive.Core.Schedules;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using Enactive.Providers;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Settings;
using Enactive.Tools;
using Enactive.Tools.Mcp;
using Enactive.Workspace;

namespace Enactive.App.Ui;

/// <summary>Three-panel Enactive shell: Workspace + autonomy (left) · plan-step feed (center) ·
/// AI status + decision card + artifacts (right). Reuses the engine unchanged.</summary>
public sealed partial class MainWindow : Window, IDecisionPresenter
{
    // ── Reusable singletons ──────────────────────────────────────────────────
    private readonly HttpClient _http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private string _globalInstructions = string.Empty;
    // Global, app-wide log hub. Default Debug (readable); the log window can drop it to Trace for raw wire.
    // Held separately from the hub so settings can reach it: this is built before any settings are
    // read, and retention is a setting.
    private readonly FileLogSink _logFile = new();
    private readonly LogHub _log;
    private LogWindow? _logWindow;
    private InboxWindow? _inboxWindow;
    private readonly EnvironmentProbe _envProbe = new();

    /// <summary>
    /// The engine every run started here is composed on - built by EngineComposition from the current
    /// settings and rebuilt when they are saved; null while <see cref="_engineProblem"/> says why there is
    /// none. This window assembles none of it: it used to, and its copy had drifted from the console's.
    /// </summary>
    private ComposedEngine? _engine;

    /// <summary>
    /// Why there is no engine, or null when there is one.
    ///
    /// <para>Set when the settings name no model — which is now what a machine nobody has configured
    /// looks like, instead of one configured for a model name compiled into the app. The window
    /// still opens and everything that does not need a model still works; the things that DO need
    /// one say this instead of running.</para>
    /// </summary>
    private string? _engineProblem;
    private AppSettings _settings = new();

    // What the close button says when it hides the window to the tray, and the note saying it, while it is up.
    private readonly TrayHint _trayHint = new();
    private TrayHintWindow? _trayNote;

    /// <summary>Everything the window shows. Nothing below touches a control - it sets a property here.</summary>
    private readonly MainWindowViewModel _vm = new();
    /// <summary>
    /// The live run's files - written, proposed, put back - as every host reads them from its events (RunFiles): which
    /// card an ArtifactProduced makes, and which cards an ArtifactReverted marks, is decided there, not here.
    /// </summary>
    private RunFiles _files = new();
    /// <summary>Workspaces already told about their ignored legacy approvals file — say it once.</summary>
    private readonly HashSet<string> _legacyApprovalsNoted = new(StringComparer.OrdinalIgnoreCase);

    // ── Run state ────────────────────────────────────────────────────────────
    private readonly ForegroundRunSlot _foreground = new();
    private readonly SessionApprovals _sessionApprovals = new();
    /// <summary>Asks this window's person what runs need answered, through the card (DecisionRouter).</summary>
    private readonly DecisionRouter _decisions;
    /// <summary>The question the card is showing, so a late "take it down" leaves a newer one alone.</summary>
    private Guid? _shownDecision;
    // The live run's step cards, title, phase and step count - the same fold a run reopened from the history
    // is drawn from (RunFeed) - and the card views drawn from it.
    private RunFeed _feed = new();
    private readonly List<StepCardViewModel> _cardViews = new();
    private string? _shownPhase;
    private (int Done, int Total) _shownProgress;
    private readonly Stopwatch _runStopwatch = new();
    private DispatcherTimer? _elapsedTimer;

    /// <summary>See where it is started: the badge against work this process did not do.</summary>
    private DispatcherTimer? _inboxPoll;

    // The strip at the foot of the window (RepoBar): local git every half minute while nothing runs, GitHub at
    // most every two minutes - it is a network call, and a strip is not worth a rate limit.
    private DispatcherTimer? _repoPoll;

    // The schedules, asked every minute while the app is open - in the tray too. See TickSchedulesAsync.
    private DispatcherTimer? _scheduleTick;
    private int _scheduleTicking;
    private static readonly TimeSpan ScheduleTickEvery = TimeSpan.FromMinutes(1);
    private string _currentWorkspaceRoot = string.Empty;
    private readonly WorkspaceRegistry _registry = WorkspaceRegistry.Load(deferWrites: true);

    /// <summary>What each workspace was left showing in the middle column. See OpenRunMemory.</summary>
    private readonly OpenRunMemory _openRuns = new();

    /// <summary>Reading and forgetting runs, and the questions asked first (RunHistoryService).</summary>
    private readonly RunHistoryService _history;

    /// <summary>Runs in flight, and the workspace each belongs to - see LiveRuns.</summary>
    private readonly LiveRuns _liveRuns = new();

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
    /// <summary>Who is working on the live run and what it is doing now (RunActivity).</summary>
    private RunActivity _activity = new();
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

    /// <summary>The run id of the live run, while it stages - for its "Run checks now" once it has ended (Plan Phase 3.2).</summary>
    private Guid? _stagedRunId;

    /// <summary>The folder that run ran in - its proposals' and its checks' - whatever folder is on screen when it ends.</summary>
    private string? _stagedRunRoot;

    public MainWindow()
    {
        _log = new LogHub(minLevel: LogLevel.Debug, downstream: new ILogSink[] { _logFile });
        _settings = AppSettings.Load();
        // The desktop's questions are its account's own: no principal (ApprovalStore, rule 5).
        _decisions = new DecisionRouter(this, _sessionApprovals, ApprovalStore.Default);
        _history = new RunHistoryService(() => _settings.KeepRuns);

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
        _vm.Runs.ForgetUnfinishedRequested += checkpoint => _ = ForgetUnfinishedAsync(checkpoint);
        // Pressing the running row is the way back to the live feed. The "← Back" in the past-run
        // header does the same thing and is where nobody looks.
        _vm.Runs.OpenLiveRequested += () => _vm.ShowLiveRun();
        _vm.WorkspacePathChanged += RefreshWorkspaces;
        _vm.WorkspaceSwitchRequested += SwitchWorkspace;
        _vm.WorkspaceRenameRequested += path => _ = RenameWorkspaceAsync(path);
        _vm.WorkspaceForgetRequested += path => _ = ForgetWorkspaceAsync(path);
        _vm.RunSettingsChanged += SaveRunSettings;
        _vm.AddWorkspaceRequested += () => _ = AddWorkspaceAsync();
        _vm.SettingsRequested += ShowSettings;
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
        _repoPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        // While a run is going too (2026-10-09): what a step changes is what a person watches for, and a strip that waited
        // for the run to end showed the state before it the whole time. Reading git is five short commands, each bounded.
        _repoPoll.Tick += (_, _) => { if (IsActive) _ = RefreshRepoAsync(); };
        _repoPoll.Start();
        if (this.FindControl<Border>("RepoBar") is { } repoBar)
            repoBar.PointerPressed += (_, _) => _ = RefreshRepoAsync(forcePullRequest: true);
        // A click anywhere on the model figures opens or folds them - but not one on the scroll bar of the opened panel,
        // which is there to move through the figures, not to close them.
        if (this.FindControl<Border>("PerformancePanel") is { } performance)
            performance.Tapped += (_, e) =>
            {
                if ((e.Source as Visual)?.FindAncestorOfType<ScrollBar>(includeSelf: true) is null)
                    _vm.Performance.Toggle();
            };
        if (this.FindControl<Button>("RepoPullRequest") is { } pullRequest)
            pullRequest.Click += (_, _) => OpenRepoPage(_vm.Repo.PullRequestUrl);
        if (this.FindControl<Button>("RepoChecks") is { } checks)
            checks.Click += (_, _) => OpenRepoPage(_vm.Repo.PullRequestUrl is { } url ? url + "/checks" : null);

        _inboxPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _inboxPoll.Tick += (_, _) => RefreshInboxButton();
        _inboxPoll.Start();

        _scheduleTick = new DispatcherTimer { Interval = ScheduleTickEvery };
        _scheduleTick.Tick += (_, _) => _ = TickSchedulesAsync();
        _scheduleTick.Start();
        // Once at start too: an occurrence that came due while the app was closed is still within its grace now.
        _ = TickSchedulesAsync();

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
                    // The screen it was on, while it still is on one: the note goes in that screen's corner.
                    var screen = Screens.ScreenFromWindow(this);
                    SaveWindowBounds();
                    Hide();
                    if (_trayHint.TakeOnHide())
                    {
                        var note = TrayHintWindow.ShowNote(screen, ShowFromTray);
                        // Gone by itself or by a click: nothing left for the window's return to take down.
                        note.Closed += (_, _) => { if (ReferenceEquals(_trayNote, note)) _trayNote = null; };
                        _trayNote = note;
                    }
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

    /// <summary>
    /// The trusted devices and invitations, for the settings pane and the Add a device window - through
    /// whichever service is running, behind <see cref="_remoteGate"/>.
    /// </summary>
    private RemoteDevices RemoteDeviceAccess => _remoteDevices ??= new RemoteDevices(() => _remote, _remoteGate);

    private RemoteDevices? _remoteDevices;

    /// <summary>Where this computer keeps what it was asked to do and has not yet reported.</summary>
    private static string RemoteDatabasePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Enactive", "remote.db");

    private void StartRemoteAccess()
    {
        if (_shuttingDown) return;

        // One service per remote.db: a second one would hold a second key store over the same file,
        // and a code applied meanwhile could reset the keys under either.
        if (_remote is not null)
        {
            _log.Info(LogSource.System, "Remote access: already running, so it was not started again.");
            return;
        }

        _remote = new RemoteAccessService(
            _settings.RemoteAccess,
            // Read by the service from remote.db, under the computer id a connection code stored in
            // the settings. With no id it has none, and says so rather than connecting with nothing
            // to seal or open with.
            keys: null,
            // The engine as it is when the task arrives, read on the UI thread where it is replaced. What
            // governs the task - the level and worker saved for the folder it names - the service reads
            // from the workspace list itself (RunRequest.FromPhone).
            () => Dispatcher.UIThread.InvokeAsync(CurrentEngine).GetTask(),
            () => _registry.Entries,
            // The desktop's own handler. RemoteRunner wraps it rather than replacing it, so a
            // permission question from a remote run shows here as well as on the phone.
            _decisions,
            RemoteDatabasePath());

        // The service this handler belongs to, not whatever _remote is when the post runs. A restart sets _remote
        // to null and then disposes the old service, and a status the old one raised while stopping was posted
        // here and read _remote!.Status - null when remote access had been switched off, and the next service's
        // status otherwise. So only the service still in use is logged; one that has been replaced is ignored.
        // Not under test: the handler is the window's, and nothing smaller than the window runs it.
        var service = _remote;
        service.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(service, _remote))
                _log.Info(LogSource.System, "Remote access: " + service.Status);
        });

        // Said in the log as well as in the window that made the invitation: that window may have been
        // closed, and a device that now holds every key of this computer must be traceable to a moment.
        _remote.DeviceAdmitted += device => Dispatcher.UIThread.Post(() =>
            _log.Info(LogSource.System, $"Remote access: \"{device.Label}\" answered an invitation and is now trusted."));
        // A refused answer is logged once, as the status line it also becomes; only answers set aside
        // are logged from here.
        _remote.InvitationNoticed += notice =>
        {
            if (!notice.Refused)
                Dispatcher.UIThread.Post(() => _log.Info(LogSource.System, "Remote access: " + notice.Detail));
        };
        RemoteDeviceAccess.Attach(_remote);

        _remote.Start();
        _log.Info(LogSource.System, "Remote access: " + _remote.Status);
    }

    /// <summary>
    /// The remote settings as a value that can be compared, so a Save that did not touch them
    /// leaves the connection alone.
    ///
    /// <para>The token is compared by whether there is one, not by what it is: this decides whether
    /// to reconnect, and putting a bearer credential into a string that is compared, logged by
    /// accident or held in a local is not worth the precision. A new token always comes with a new
    /// connection code, and applying a code restarts the service itself.</para>
    /// </summary>
    private static string Describe(RemoteAccessSettings remote)
        => $"{remote.Enabled}|{remote.GatewayUrl}|{remote.HostId}|{remote.Token.Length > 0}";

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
        await _remoteGate.WaitAsync();
        try
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
        finally
        {
            _remoteGate.Release();
        }
    }

    /// <summary>
    /// Restarting the service and applying a code, one at a time. Interleaved, a restart could start a
    /// service - and a key store over remote.db - while a code was resetting the keys in that file.
    /// </summary>
    private readonly SemaphoreSlim _remoteGate = new(1, 1);

    /// <summary>
    /// Answers the settings window's "Test connection", and says what happened.
    ///
    /// <para>Through the running service, because the check publishes this computer's workspace list
    /// and the names in it are sealed with the keys that service holds - one key store per remote.db.
    /// A check that sent an empty list instead would unpublish every workspace.</para>
    /// </summary>
    private async Task<string> CheckRemoteAsync(CancellationToken ct)
    {
        var detail = _remote is { } remote
            ? await remote.CheckAsync(ct)
            : "Remote access is restarting - try again in a moment.";

        _log.Info(LogSource.System, "Remote access check: " + detail);
        return detail;
    }

    /// <summary>
    /// Applies a connection code from the settings window: this computer's keys, then the live
    /// settings, saved - and remote access started again on them.
    ///
    /// <para>The questions come first, with the service still running: a person who says no keeps
    /// it, and the remote tasks on it. Only once every answer is yes is it stopped - it holds the key
    /// store over remote.db, and a code can replace the keys in it; a service still running would go
    /// on sealing with keys that are no longer written anywhere - and it is started again whatever
    /// happened after that.</para>
    ///
    /// <para>Nothing here logs the code or the settings it fills: both hold the token, and the code
    /// holds the pairing secret too.</para>
    /// </summary>
    private async Task<(bool Connected, string Detail)> ConnectRemoteAsync(
        ConnectionCode code, Func<string, Task<bool>> confirm)
    {
        await _remoteGate.WaitAsync();
        var stopped = false;

        try
        {
            var outcome = await RemoteAccessService.ConnectWithCodeAsync(
                code, _settings.RemoteAccess, RemoteDatabasePath(), confirm,
                beforeChange: async () =>
                {
                    stopped = true;
                    var previous = _remote;
                    _remote = null;
                    if (previous is not null)
                        await previous.DisposeAsync();
                });

            if (!outcome.Paired)
                return (false, outcome.Problem
                    ?? "Nothing was changed: this computer keeps the keys and the connection it had.");

            _log.Info(LogSource.System,
                $"Remote access: connected as computer {outcome.HostId}; device {outcome.DeviceId} is trusted"
                + (outcome.Replaced ? ", and the previous keys were replaced." : "."));

            // The keys are already written, so a failed save is not a failed connection: the settings
            // window's copy takes the same values, and its Save writes them.
            if (!_settings.Save(replaceUnreadable: true))
                return (true, "Connected, but the settings could not be saved - press Save to try again. "
                    + (_settings.LastSaveError ?? string.Empty));

            return (true, "Connected. The browser that made the code is trusted, and gets this computer's key "
                + "as soon as the connection is up.");
        }
        catch (Exception failure)
        {
            return (false, "The code could not be applied: " + failure.Message);
        }
        finally
        {
            if (stopped)
                StartRemoteAccess();

            _remoteGate.Release();
        }
    }

    // ── Closing ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Work that must be cancelled and drained before this process exits.
    /// </summary>
    private bool HasWorkInFlight() => InFlight.Any;

    private WorkInFlight InFlight => new(_vm.IsBusy, _decisions.IsPending, _background.Count, RemoteRunsInFlight);

    /// <summary>
    /// Runs a phone started that are still going. They die with the process exactly as a background
    /// run does, and the person being asked about quitting is not the person watching them - which
    /// is the reason to say so rather than to count them in silently.
    /// </summary>
    private int RemoteRunsInFlight => _remote?.Running.Count ?? 0;

    private string DescribeWorkInFlight() => InFlight.Describe();

    /// <summary>
    /// False when this desktop has no system tray. Then the window has nowhere to hide and closing
    /// it means what it used to mean - so it asks, and quits.
    /// </summary>
    public bool HasTray { get; set; } = true;

    /// <summary>Brings the window back from the tray, wherever it was left.</summary>
    public void ShowFromTray()
    {
        // Back from the tray: the next close says where the window went again, and a note still up about this
        // one has nothing left to say.
        _trayHint.WindowShown();
        _trayNote?.Close();
        _trayNote = null;

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
            _scheduleTick?.Stop();
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
            // Avalonia.Application in full: Enactive.Application is a namespace (see Enactive.Application.csproj).
            (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
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
    /// <summary>
    /// Reads the workspace's repository into the strip, as every host reads it (RepoBar.ReadAsync): its pull request when
    /// two minutes have passed since it was last asked, or whenever the person asked by clicking.
    /// </summary>
    private Task RefreshRepoAsync(bool forcePullRequest = false)
        => _vm.Repo.ReadAsync(WorkspaceRootOrNull(), forcePullRequest);

    /// <summary>Opens a pull request's page in the browser - only a github.com address, which is what the strip shows.</summary>
    private void OpenRepoPage(string? url)
    {
        // Qualified: RepoBar here is the strip's Border (x:Name), not the model.
        if (global::Enactive.Application.Projections.RepoBar.GitHubPage(url) is not { } uri)
            return;
        _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
    }

    private string? WorkspaceRootOrNull()
    {
        var root = _vm.WorkspacePath.Trim();
        return string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
    }

    /// <summary>The Settings window, when one is open. See <see cref="ShowSettings"/>.</summary>
    private readonly OneAtATime<SettingsWindow> _settingsWindow = new();

    /// <summary>
    /// Opens Settings, or brings forward the one already open.
    ///
    /// <para>One at a time, because of what Save does. The window gets the live settings and CLONES
    /// them, so Cancel/close leave these untouched and Save hands back the clone, which is then
    /// written and put in place of this one. Every press used to open another window with another
    /// clone, and a window left open behind the others saved its old copy over what a newer one had
    /// just saved - all of it, not only the fields it showed. Unlike the library and the schedules
    /// the window is not kept and re-shown: it is closed for good and the next one is built from
    /// the settings as they are then, which is what makes its copy current.</para>
    ///
    /// <para>The same cloning makes AppSettings.Clone the whole of what survives a visit to this
    /// window: a property missing there is a setting the next Save resets, whether or not the
    /// window has a control for it. Pinned by SettingsSurviveTheEditorTests.</para>
    /// </summary>
    private void ShowSettings()
        => _settingsWindow.Request(
            open: () =>
            {
                var window = new SettingsWindow(_settings, workspaceRoot: WorkspaceRootOrNull(),
                    // With the web tools always among them: a role can be given them before reading the web is
                    // turned on, and the Team list would otherwise only show them after a save and a reopen.
                    toolNames: EngineComposition.Tools(_settings).Definitions.Select(d => d.Name)
                        .Union([Enactive.Tools.Web.FetchUrlTool.Name, Enactive.Tools.Web.WebSearchTool.Name]).ToArray(),
                    remoteCheck: CheckRemoteAsync,
                    remoteConnect: ConnectRemoteAsync,
                    remoteDevices: RemoteDeviceAccess,
                    onSaved: SaveSettings);
                window.Closed += (_, _) => _settingsWindow.Closed(window);
                window.Show(this);
                return window;
            },
            bringForward: window =>
            {
                // Minimised, Activate alone lights the taskbar button and shows nothing.
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
            });

    /// <summary>Writes what the Settings window hands back, and puts it in place of the live settings.</summary>
    private void SaveSettings(AppSettings saved)
    {
        // Before it is written: the copy is from when the window opened, and the app has gone on
        // writing its own values into the live settings since (see KeepWhatTheAppWrote).
        saved.KeepWhatTheAppWrote(_settings);

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

        // The way to ask, not the object: a Settings save replaces _settings, and this window is
        // kept and shown again (see SchedulePolicy).
        _schedulesWindow = new SchedulesWindow(root, () => _settings);
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
        if (_shuttingDown || (!background && _vm.IsBusy) || _decisions.IsPending)
            return;

        // A resumed run's request comes from the checkpoint. The command bar is empty by then - the
        // interrupted run cleared it when it started, possibly days ago in another process.
        var text = spec?.Goal.Trim() ?? resume?.Request.Trim() ?? _vm.InputText.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        var workspacePath = _vm.WorkspacePath.Trim();
        if (string.IsNullOrEmpty(workspacePath))
            return;

        // A folder that is not there, or no model to run on - the checks every host makes (RunLauncher).
        if (RunLauncher.Refusal(workspacePath, _engineProblem) is { } refused)
        {
            _vm.StatusPhase = "Error";
            _vm.CurrentAction = refused;
            return;
        }

        // Background: fire the run headless (results land in the Inbox) and keep the UI free.
        if (background)
        {
            // The template goes with it: a saved task run in the background is still that task, with its
            // permissions, checks, limits and role - not its goal typed as a request.
            StartBackground(text, Path.GetFullPath(workspacePath), spec: spec);
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
            // "Pause after this step" in the header is this run's: its own pause, made beside the cancellation Stop uses
            // (ForegroundRunSlot), offered once the run turns out to be planned and not staged (PauseOffer).
            var pause = _foreground.Pause;
            _vm.Pause = new PauseOffer(pause);

            // The command bar clears, so the request moves into the header - otherwise what you asked
            // for survives only in the log.
            // This run's workspace, and the column is showing it: whatever was on screen a moment ago,
            // pressing Run means watching THIS.
            _liveWorkspace = WorkspaceRegistry.Normalise(workspacePath);
            _liveAttached = true;

            _vm.InputText = string.Empty;
            Live(() => _vm.TaskIntent = text);
            SetLiveTitle(RunLauncher.Title(text));
            _feed = new RunFeed(_liveTitle);
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



                var fullPath = Path.GetFullPath(workspacePath);
                _currentWorkspaceRoot = fullPath;
                NoteLegacyApprovalsIfAny(fullPath);
                _registry.Touch(fullPath);
                RefreshWorkspaces();

                // Adopt, not For: a run is where a folder is genuinely taken up as a workspace, so this is
                // where its id gets written down beside it. The id written is the one it already had, so a
                // workspace with history keeps it and can be renamed from here on without losing it.
                var workspace = WorkspaceInfo.Adopt(fullPath);

                // The same composition every host uses (RunComposer): what governs a resumed run, what a
                // template brings, whether the changes are staged - this window decides none of it. What it
                // keeps is its own: the approval card (this), the timeline it renders, its cancellation.
                await using var composed = await RunComposer.ComposeAsync(
                    CurrentEngine(),
                    new RunRequest(workspace, text, IntentSource.CommandBar, taskId, resume, spec, Stage: _vm.StageChanges)
                        { Defaults = DefaultsOnScreen(), Pause = pause },
                    _decisions, runCancellation.Token);

                _staging = composed.Artifacts as StagingArtifactStore;
                _disk = composed.Artifacts as DiskArtifactStore;
                _stagedShown = 0;
                _stagedRunId = null;
                _stagedRunRoot = fullPath;
                // The last run's "Run checks now" goes with its proposals: this run's are about to be shown.
                await Dispatcher.UIThread.InvokeAsync(() => _vm.LiveChecks = null);
                _files.Stages = _staging is not null;

                var envLine = composed.Intent.Context.Environment?.OneLine();
                var staged = _staging is not null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _vm.EnvironmentSummary = envLine ?? "(no environment data)";
                    // A run that stages its changes ignores a pause (it cannot be resumed), so it is not offered one.
                    if (staged)
                    {
                        _vm.Pause?.Staging();
                        _vm.PauseChanged();
                    }
                });

                await RunEventPump.RunAsync(
                    composed.Events(runCancellation.Token),
                    async batch => await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var ev in batch) RenderEvent(ev);
                    }), runCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { Live(() => _vm.StatusPhase = "Cancelled"); _feed.Stopped(); SyncFeed(); });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Live(() => { _vm.StatusPhase = "Error"; _vm.CurrentAction = ex.Message; });
                    // Every card still short of an end never reached one - not only the one that happened to be current.
                    _feed.Stopped();
                    SyncFeed();
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
                    // Over, however it ended: nothing left to pause.
                    _vm.Pause = null;
                    // The row goes BEFORE the history is re-read, so the moment it stops running is the
                    // moment it stops being listed as running. RunColumn.Live is the belt to this
                    // brace: if the two ever cross, the run is not shown twice.
                    EndLive(endedRow);
                    // A staged run that ended: its checks can be run now, against the files as its proposals are decided
                    // (Plan Phase 3.2). Beside the proposals, as long as they are on screen; its records are in its history too.
                    if (_staging is not null && _stagedRunId is { } stagedRun && _stagedRunRoot is { Length: > 0 } stagedRoot)
                        _ = ShowLiveChecksAsync(stagedRoot, stagedRun);
                    // The run that just ended belongs at the top of the history under the workspace.
                    // Only when that workspace is the one on screen - LoadRunsAsync reads whichever is
                    // current, and a run ending elsewhere is not a reason to re-read this one.
                    if (IsLiveWorkspace)
                        _ = LoadRunsAsync();
                    RefreshColumnState();
                    // A run changes the files, and perhaps the branch: the strip is read again now, not in half a minute.
                    _ = RefreshRepoAsync();
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
                if (_staging is not null)
                    _stagedRunId ??= ev.RunId;
                // The panel of model figures is this run's, and starts again with the next (ModelPerformance).
                _vm.Performance.ShowRun(ev.RunId);
                // The header's "Pause after this step": offered once the plan is here, gone with a quick action or the end.
                _vm.ApplyToPause(ev);
                // A step that ended has changed what it changes: the repository strip is read again then, not only when
                // the run is over (2026-10-09).
                if (ev.Kind == EventKind.StepCompleted)
                    _ = RefreshRepoAsync();
                if (_liveRow != Guid.Empty && _renderedRunId != ev.RunId)
                {
                    _renderedRunId = ev.RunId;
                    UpdateLive(_liveRow, r => r.RunId == ev.RunId ? r : r with { RunId = ev.RunId });
                }

                // The cards, the title, the phase and the step count: folded by RunFeed, exactly as a run reopened
                // from the history is, so the two cannot disagree. New cards are shown where the feed put them.
                foreach (var (card, at) in _feed.Apply(ev))
                {
                    var view = new StepCardViewModel(card);
                    _cardViews.Add(view);
                    Live(() => _vm.Steps.Insert(Math.Min(at, _vm.Steps.Count), view));
                }
                SyncFeed();

                // The agent pill, the line saying what is happening now and the count of tool calls: folded by
                // RunActivity, as any host shows them. Drawn here only where they changed - the pill in particular
                // is redrawn on a change of hands, not on every token.
                var changed = _activity.Apply(ev);
                if (changed.HasFlag(RunActivity.Change.Agent))
                {
                    var (label, agent) = (_activity.AgentLabel, _activity.Agent);
                    Live(() => _vm.SetAgent(label, agent));
                }
                if (changed.HasFlag(RunActivity.Change.ToolCalls))
                {
                    var calls = _activity.ToolCalls;
                    Live(() => _vm.ToolCalls = calls);
                }
                if (changed.HasFlag(RunActivity.Change.Ended))
                    Live(() => _vm.IsAgentVisible = false);
                if (changed.HasFlag(RunActivity.Change.Action))
                {
                    var action = _activity.CurrentAction;
                    Live(() => _vm.CurrentAction = action);
                }

                // What only the window shows: the routing panel, tokens and artifacts.
                switch (ev.Kind)
                {
                    case EventKind.Routed:
                        Live(() => _vm.Routing.Apply(ev.Summary, ev.PayloadJson));
                        break;
                    case EventKind.UsageReported:
                        if (ev.Usage() is { } used)
                        {
                            var usageProvider = ev.ProviderId();
                            Live(() => _vm.AddUsage(used.In, used.Out, usageProvider, ReachOf(usageProvider)));
                        }
                        break;
                    case EventKind.ArtifactProduced or EventKind.ArtifactReverted:
                        switch (_files.Apply(ev))
                        {
                            case FileNews.Written written:
                                AddArtifact(written.Path);
                                break;
                            case FileNews.Staged:
                                AddStagedArtifact();
                                break;
                            // The step's work was put back after the reviewer rejected it. Its cards must stop
                            // offering to open or undo a file that is no longer the file they describe.
                            case FileNews.Reverted reverted:
                                MarkRevertedArtifacts(reverted.Paths);
                                break;
                        }
                        break;
                }
        }

        /// <summary>
        /// Draws what the feed has become: every card view brought up to its card, and the phase, the title and
        /// the step count wherever they changed.
        /// </summary>
        private void SyncFeed()
        {
            foreach (var view in _cardViews)
                view.Sync();

            if (_feed.Phase is { } phase && phase != _shownPhase)
            {
                _shownPhase = phase;
                Live(() => _vm.StatusPhase = phase);
            }

            if (_feed.Title.Length > 0 && _feed.Title != _liveTitle)
                SetLiveTitle(_feed.Title);

            if ((_feed.StepsDone, _feed.StepsTotal) != _shownProgress)
            {
                _shownProgress = (_feed.StepsDone, _feed.StepsTotal);
                UpdateProgress();
            }
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
            var progress = RunFigures.Steps(_feed.StepsDone, _feed.StepsTotal);
            Live(() => _vm.StatusProgress = progress);

            // The one place step counts change, so the one place the live row has to be told. The row
            // also carries the planner's title, which arrives after the row does.
            if (_liveRow != Guid.Empty)
                UpdateLive(_liveRow, r => r with
                {
                    Title = _liveTitle,
                    StepsDone = _feed.StepsDone,
                    StepsTotal = _feed.StepsTotal
                });
        }

        private static string FormatElapsed(TimeSpan span) => RunElapsed.Format(span);

        /// <summary>A card for a file the run wrote - once per file: RunFiles tells a path only the first time.</summary>
        private void AddArtifact(string relative)
        {
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
        /// Marks the cards for files a rejected step wrote and the engine put back - the paths the event carries as
        /// values (WorkEventPayload.RevertPayload). They were read out of its sentence, after "put back: ", and the line
        /// about files the review found right reads "NOT put back: a.md, b.md - ...": a.md was marked put back while it
        /// stayed. Only the live run's cards are marked; a run reopened from the history shows the line as a note.
        /// </summary>
        private void MarkRevertedArtifacts(IReadOnlyList<string> paths)
        {
            foreach (var item in _liveArtifacts)
            {
                if (!paths.Contains(item.RelativePath, StringComparer.OrdinalIgnoreCase))
                    continue;

                item.Status = RunFiles.PutBackStatus;
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
                        item.State = ChangeState.Refused;
                        return;
                    }

                    item.Status = "applied";
                    item.State = ChangeState.Applied;
                    item.CanAct = false;
                },
                item =>
                {
                    var result = staging.Reject(change.Id);
                    if (!result.Rejected)
                    {
                        item.Status = result.Conflict ?? "could not reject";
                        item.State = ChangeState.Refused;
                        return;
                    }
                    item.Status = "rejected";
                    item.State = ChangeState.Rejected;
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
            var row = _liveRuns.Begin(title, workspace, headless);
            RefreshLive();
            return row;
        }

        private void UpdateLive(Guid row, Func<LiveRun, LiveRun> change)
        {
            if (_liveRuns.Update(row, change))
                RefreshLive();
        }

        private void EndLive(Guid row)
        {
            if (_liveRuns.End(row))
                RefreshLive();
        }

        /// <summary>
        /// Pushes the live rows for the CURRENT workspace into the column: those of the folder on screen
        /// (LiveRuns), filtered against the history (RunColumn), which are the two rules that have tests.
        /// </summary>
        private void RefreshLive()
            => _vm.Runs.ShowRunning(RunColumn.Live(_liveRuns.In(_vm.WorkspacePath), _vm.Runs.All));

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
            _files = new RunFiles();
            _liveArtifacts.Clear();
            _feed = new RunFeed();
            _cardViews.Clear();
            _shownPhase = null;
            _shownProgress = default;
            _liveTitle = string.Empty;
            _liveJournal.Clear();
            _activity = new RunActivity();
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
            if (!_decisions.IsPending) _vm.IsDecisionVisible = false;
            _vm.IsAgentVisible = false;

            _vm.StatusPhase = "Idle";
            _vm.StatusProgress = "—";
            _vm.StatusElapsed = "—";
            _vm.CurrentAction = string.Empty;
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

            var name = await PromptWindow.AskAsync(this, WorkspaceRows.RenameTitle, WorkspaceRows.RenamePrompt, entry.Name);

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

            _registry.SaveSettings(path, _vm.AutonomyTier, CurrentWorkerId(), _vm.StageChanges);
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

            // A different folder is a different repository, or none: read before anything else can return early.
            _ = RefreshRepoAsync(forcePullRequest: true);

            var entry = _registry.Find(_vm.WorkspacePath.Trim());
            if (entry is null)
                return;

            _vm.IsLoadingWorkspaceDefaults = true;
            try
            {
                _vm.AutonomyLevel = AutonomyTiers.Clamp(entry.Autonomy);
                _vm.StageChanges = entry.StageChanges;

                // By id - or by role name, as this window saved it until 2026-10-08 - and not by position:
                // the list changes when the roles are edited, and an index would then quietly select a
                // different worker. The role box lists the workers in order, so a worker's position is its row.
                var index = _engine?.WorkerIdFor(entry.WorkerId) is { } id
                    ? _engine.Workers.All.ToList().FindIndex(w => w.Id == id)
                    : -1;
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

            var question = WorkspaceRows.Forget(entry.Name);
            var forget = await ConfirmWindow.AskAsync(this, question.Headline, question.Body, question.Confirm, question.Cancel);

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
            if (_engine is not { } engine)
                return null;

            // By id: this looked the worker box's ROLE NAME up as an id, and so read every log with the
            // default worker's model unless a role happened to be named as its id.
            var worker = engine.Workers.Get(CurrentWorkerId());
            var reference = engine.Router.Resolve(ModelPurpose.Review, worker) ?? worker?.ModelPolicy.Preferred;
            if (reference is null)
                return null;

            // How much of the log can be sent is the provider's answer, as it is for a step (LogAnalyst.AnalyseAsync);
            // the window hands on only the engine's num_ctx - from the engine that makes the provider, not from the
            // settings as they stand now. It looked the provider's declared window up in the live settings itself and fell
            // back to num_ctx for any provider: a second rule, read from another snapshot, that sent Ollama a log
            // cut to a num_ctx its request did not carry.
            // promptBodies: false — this prompt CARRIES the log, and the provider decorator would write
            // it straight back into it. One analysis of a 10,429-line run added 4,785 lines; the second
            // then read a log that was half its own previous prompt. See LoggingChatProvider.
            return async (text, ct) => await new LogAnalyst().AnalyseAsync(
                text, engine.Providers.Create(reference.ProviderId, promptBodies: false),
                reference.Model, engine.EngineOptions.NumCtx, ct);
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
            // The report's words - with no workspace, and when the probe fails - are the service's page's too.
            var report = await WorkspaceReports.EnvironmentAsync(_vm.WorkspacePath, _envProbe.ProbeFullAsync, CancellationToken.None);
            if (report.OneLine is { } line)
                _vm.EnvironmentSummary = line;
            ShowViewer(WorkspaceReports.EnvironmentTitle, report.Text);
        }

        /// <summary>
        /// The engine a run is composed on, or the reason there is none, as a refusal.
        ///
        /// <para>Thrown rather than returned as null: the callers that reach this without an earlier gate - a
        /// task from a phone - would otherwise reach a null provider. A named refusal is what the panel can
        /// report; a NullReferenceException is not.</para>
        /// </summary>
        private ComposedEngine CurrentEngine()
            => _engine ?? throw new InvalidOperationException(_engineProblem ?? "There is no engine to run on.");

        /// <summary>The slider and the worker box: the defaults of the folder on screen, which a run against it is in.</summary>
        private WorkspaceDefaults DefaultsOnScreen() => new(_vm.AutonomyTier, CurrentWorkerId());

        /// <summary>
        /// The worker the worker box is on, by id - null when there are none to pick from. The box lists the
        /// engine's workers in order, so the selected row is the worker at that position.
        /// </summary>
        private string? CurrentWorkerId()
            => _engine is { } engine && _vm.SelectedWorkerIndex >= 0 && _vm.SelectedWorkerIndex < engine.Workers.All.Count
                ? engine.Workers.All[_vm.SelectedWorkerIndex].Id
                : null;

        /// <summary>
        /// The workspace at a path, for READING what has been recorded about it — the run list, the
        /// timeline, the inbox. It resolves the id the folder carries and writes nothing: opening a
        /// history should not put a file in somebody's project.
        /// </summary>
        private static WorkspaceInfo WorkspaceFrom(string path) => WorkspaceInfo.For(path);

        /// <param name="taskId">The task this carries on, when it is one that stopped at a question - see <see cref="ContinueParkedAsync"/>.</param>
        /// <param name="resume">Its step boundary, when it has one; null starts the request again under <paramref name="taskId"/>.</param>
        /// <param name="spec">The saved task this is, when it is one.</param>
        private void StartBackground(string text, string fullPath, Guid? taskId = null, RunCheckpoint? resume = null,
            ResolvedTaskSpec? spec = null)
        {
            var continuing = taskId is not null || resume is not null;
            if (_shuttingDown) return;

            // Adopt: a background run is a run, and the folder is being taken up as a workspace here
            // exactly as it is in the foreground.
            var workspace = WorkspaceInfo.Adopt(fullPath);
            // The slider and the worker box on screen, and rightly: this run is against the folder on screen.
            var request = new RunRequest(workspace, text, IntentSource.Inbox, resume?.TaskId ?? taskId, resume, spec,
                Stage: _vm.StageChanges) { Defaults = DefaultsOnScreen() };
            // Refused here, where the request was made, and in the composer's own words - see RunComposer.Refusal.
            if (RunComposer.Refusal(request) is { } refused)
            {
                _vm.StatusPhase = "Not started";
                _vm.CurrentAction = refused;
                return;
            }
            var engine = CurrentEngine();
            _registry.Touch(fullPath);
            RefreshWorkspaces();

            var title = RunLauncher.Title(text);
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
            // What is being typed is somebody's next request; carrying an old one on is not a reason to clear it.
            if (!continuing) _vm.InputText = string.Empty;

            // A row of its own, carrying the workspace it belongs to: the window is free again the
            // moment this starts, so this row is the only thing on screen that says it is happening.
            // It never learns a run id - the events go to the Inbox writer, not here - so it is removed
            // by this method and by nothing else.
            var row = BeginLive(title, fullPath, headless: true);

            var execution = _background.TryStart(async ct =>
            {
                try
                {
                    // To its end and into the Inbox, however it ends - as every host runs one (BackgroundRun).
                    await BackgroundRun.RunAsync(engine, request, text, ct);
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

    /// <summary>
    /// One tick of the schedules: the pulse, then the most overdue schedule anywhere, run in the background.
    ///
    /// <para>The app did not run its own schedules (2026-10-11): only the console's --due-all did, woken by a Windows
    /// task nobody had registered, so a schedule made in this window never ran while the window was open all day.
    /// Now the app asks every minute while it is open, the tray included. The console path stays for a machine where
    /// the app is not running; both take the same claim (DueSchedules), so they do not start one schedule twice.</para>
    /// </summary>
    private async Task TickSchedulesAsync()
    {
        // One tick at a time: a slow one (a large schedules file, a workspace on a network drive) must not be
        // overtaken by the next and decide the same schedule twice in this process.
        if (_shuttingDown || Interlocked.Exchange(ref _scheduleTicking, 1) == 1)
            return;
        try
        {
            var now = DateTimeOffset.Now;
            // The pulse first: it says the schedules ARE being asked, which the schedules window reports.
            Heartbeat.Stamp(now);
            // No model, no run - and the occurrence is left for a tick within its grace, after it is configured.
            if (_engineProblem is { Length: > 0 })
                return;

            // The most overdue schedule anywhere, claimed with its folder - the same steps the console and the service take
            // (DueSchedules). Busy or broken, it says so where it is reported: busy waits for a later tick, broken is in its Inbox.
            var take = await Task.Run(() => DueSchedules.TakeFirstAsync(ScheduleStore.Default, RunMarkers.Default, now, CancellationToken.None));
            if (take.Run is not { } taken)
                return;
            await Dispatcher.UIThread.InvokeAsync(() => StartScheduled(taken));
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource.System, "Could not check the schedules: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _scheduleTicking, 0);
        }
    }

    /// <summary>
    /// A schedule's run, headless, under the schedule's own permissions and with nobody to answer: the same
    /// run the console's --due-all starts. Its outcome goes to the Inbox under the schedule, which is where the
    /// schedules window lists it.
    /// </summary>
    private void StartScheduled(DueRun taken)
    {
        var schedule = taken.Schedule;
        var workspace = taken.Workspace;
        var inbox = InboxStoreFactory.Create(workspace);
        // The schedule's level, not the slider on screen: the slider is about the folder on screen, and the
        // template's permissions are already narrowed to the schedule's (ScheduledSpec).
        var request = new RunRequest(workspace, taken.Spec.Goal, IntentSource.Schedule, Spec: taken.Spec)
            { Defaults = new WorkspaceDefaults((int)schedule.Permissions.Level) };

        if ((_shuttingDown ? "the app was closing" : RunComposer.Refusal(request)) is { } why)
        {
            taken.Dispose();
            _ = inbox.AppendAsync(ScheduledOutcome.CouldNotStart(workspace.Id, schedule.Id, schedule.Name, why, DateTimeOffset.UtcNow),
                CancellationToken.None);
            return;
        }

        var engine = CurrentEngine();
        var row = BeginLive(schedule.Name, workspace.RootPath, headless: true);
        _log.Info(LogSource.System, $"Schedule '{schedule.Name}' started in {workspace.RootPath}.");

        var execution = _background.TryStart(async ct =>
        {
            using (taken)
            {
                try
                {
                    // Nobody is here to answer, as at the console: a question is refused, not parked. A schedule's
                    // template was checked for that before it was taken (ScheduleTick, approvalIsPossible: false).
                    await using var composed = await RunComposer.ComposeAsync(engine, request, new UnattendedDecisionHandler(), ct);
                    await BackgroundRunner.RunAsync(composed.Events(ct), inbox, workspace, schedule.Name, ct, schedule.Id);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    var line = InboxLines.For("Cancelled", 0, 0, null);
                    await inbox.AppendAsync(new InboxItem(Guid.NewGuid(), workspace.Id, line.Kind, schedule.Name,
                        line.Summary, Guid.Empty, "unread", DateTimeOffset.UtcNow, schedule.Id), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    await inbox.AppendAsync(ScheduledOutcome.CouldNotStart(workspace.Id, schedule.Id, schedule.Name,
                        ex.Message, DateTimeOffset.UtcNow), CancellationToken.None);
                }
                finally
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        EndLive(row);
                        if (!_shuttingDown) RefreshInboxButton();
                        if (!_shuttingDown && string.Equals(
                                WorkspaceRegistry.Normalise(_vm.WorkspacePath), workspace.RootPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            _ = LoadRunsAsync();
                        }
                    });
                }
            }
        });
        if (execution is null)
        {
            EndLive(row);
            taken.Dispose();
        }
        else _ = ObserveBackgroundAsync(execution);
    }

    /// <summary>
    /// Carries on a background run that stopped at a question, now answered: from its last step
    /// boundary when it has one, otherwise by starting the same request again under the same task.
    /// Either way, the step that asked is done again and, asking the same thing, is given the answer.
    /// </summary>
    private async Task ContinueParkedAsync(ParkedDecision parked, InboxItem item, string workspaceRoot)
    {
        var fullPath = Path.GetFullPath(workspaceRoot);
        var checkpoint = await BackgroundRun.ContinueFromAsync(parked, fullPath,
            warning => _log.Warn(LogSource.System, warning), CancellationToken.None);

        await Dispatcher.UIThread.InvokeAsync(() =>
            StartBackground(checkpoint?.Request ?? item.Title, fullPath, parked.TaskId, checkpoint));
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
                InboxStoreFactory.Create(workspace), RunStoreFactory.Create(workspace), workspace.RootPath,
                (parked, item) => ContinueParkedAsync(parked, item, workspace.RootPath));
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

        var (record, problem) = await _history.OpenAsync(path, summary, CancellationToken.None);
        if (record is null)
        {
            _vm.Runs.Fail(problem ?? "That run could not be opened.");
            return;
        }

        _vm.ShowPastRun(BuildPastRun(record));
    }

    /// <summary>A question of the history's, asked in a dialog of this window.</summary>
    private Task<bool> AskAsync(Question question)
        => ConfirmWindow.AskAsync(this, question.Headline, question.Body, question.Confirm, question.Cancel);

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
        Wire(past.Checks, root, record.RunId);
        return past;
    }

    // ── "Run checks now" on a staged run (Plan Phase 3.2) ──────────────────────
    // As on the service's pages (ServerHistory), with the window's own rule for who: its one person, as for applying a
    // proposal. The checks run under the slider of the folder on screen - the permissions of now - against its files now.

    /// <summary>The live run's action, once the staged run that just ended is in the history and has checks to run.</summary>
    private async Task ShowLiveChecksAsync(string root, Guid runId)
    {
        RunRecord? record;
        try { record = await RunStoreFactory.Create(WorkspaceInfo.For(root)).LoadAsync(runId, CancellationToken.None); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return; }
        // Another run may have started meanwhile: its proposals are on screen now, not these.
        if (ChecksNow.Refusal(record) is not null || _stagedRunId != runId)
            return;
        var checks = new CheckRecordsViewModel(offered: true);
        Wire(checks, root, runId);
        _vm.LiveChecks = checks;
    }

    /// <summary>
    /// The level the checks of a run in <paramref name="root"/> run under: that FOLDER's - the slider when it is the folder on
    /// screen, else what is saved against it. The slider is about the folder on screen; a staged run that ended in another
    /// folder ran its checks at whatever level an unrelated folder sat at. Saved for neither: the slider, as a run's start.
    /// </summary>
    internal static int ChecksAutonomy(string root, string onScreen, int slider, Func<string, int?> saved)
        => string.Equals(WorkspaceRegistry.Normalise(root), onScreen, StringComparison.OrdinalIgnoreCase)
            ? slider
            : saved(root) ?? slider;

    /// <summary>The window runs what the view asks, and shows the run's records.</summary>
    private void Wire(CheckRecordsViewModel checks, string root, Guid runId)
    {
        checks.RunRequested += c => _ = RunChecksNowAsync(c, root, runId);
        _ = LoadChecksAsync(checks, root, runId);
    }

    private static async Task LoadChecksAsync(CheckRecordsViewModel checks, string root, Guid runId)
    {
        try { checks.Show(await Task.Run(() => CheckRecordViews.ForRunAsync(root, runId, CancellationToken.None))); }
        catch (Exception ex) when (ex is not OperationCanceledException) { checks.Said = "The check records could not be read: " + ex.Message; }
    }

    private async Task RunChecksNowAsync(CheckRecordsViewModel checks, string root, Guid runId)
    {
        if (checks.Busy)
            return;
        // Not while a run of this window is going: the workspace lease keeps out other processes, not this one's own run, and
        // the checks would read files it is in the middle of changing.
        if (_vm.IsBusy)
        {
            checks.Said = "A run is going; run the checks when it has ended, so they see the files as you left them.";
            return;
        }
        checks.Busy = true;
        try
        {
            var engine = CurrentEngine();
            var autonomy = ChecksAutonomy(root, WorkspaceRegistry.Normalise(_vm.WorkspacePath), _vm.AutonomyTier,
                folder => _registry.Find(folder)?.Autonomy);
            // Off the window's thread: the commands and the hashing of the files take their time.
            var (record, problem) = await Task.Run(() => ChecksNow.RunAsync(engine, WorkspaceInfo.For(root), runId,
                Environment.UserName, autonomy, CancellationToken.None));
            checks.Said = record is null
                ? problem ?? "The checks were not run."
                : $"Checked: {CheckRecordViews.Counts(record.Results)}. The record is kept with the run; it does not change how the run ended.";
            checks.Show(await Task.Run(() => CheckRecordViews.ForRunAsync(root, runId, CancellationToken.None)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Said = "The checks could not be run: " + ex.Message;
        }
        finally
        {
            checks.Busy = false;
        }
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
        if (RunHistoryService.Retry(past.Record) is not { } again)
            return;
        _vm.InputText = again.Goal;
        _ = RunAsync(background: false, again.Spec, again.TaskId);
    }

    /// <summary>
    /// The template as it stands NOW, with the answers this run used. The difference from Retry is
    /// the whole reason both exist: one repeats what happened, the other asks the current version of
    /// the same question.
    /// </summary>
    private void RunAgain(PastRunViewModel past)
    {
        var root = WorkspaceRegistry.Normalise(_vm.WorkspacePath);
        var (again, problem) = RunHistoryService.RunAgain(root, past.Record, PolicyFor(_vm.AutonomyTier));
        if (problem is not null)
        {
            _vm.StatusPhase = "Error";
            _vm.CurrentAction = problem;
            return;
        }
        if (again is null)
            return;

        _vm.InputText = again.Goal;
        _ = RunAsync(background: false, again.Spec, again.TaskId);
    }

    /// <summary>Opens an artifact in the viewer. Reading is safe at any age.</summary>
    private async void ShowFile(string root, ArtifactItemViewModel item)
    {
        try
        {
            // Only a file under the workspace: a record naming one outside it is not this folder's to show.
            var full = RunHistoryService.FileOf(root, item.RelativePath)
                       ?? throw new InvalidOperationException("That path is not in this workspace.");

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
        if (RunHistoryService.DeleteFile(root, item.RelativePath) is not { } question)
        {
            item.Status = "already gone";
            item.CanAct = false;
            return;
        }

        if (!await AskAsync(question))
            return;

        var (status, done) = RunHistoryService.DeleteFileNow(root, item.RelativePath);
        item.Status = status;
        if (done) item.CanAct = false;
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
        if (runs.Count == 0 || !await AskAsync(RunHistoryService.DeleteMany(runs)))
            return;

        var path = _vm.WorkspacePath.Trim();
        if (string.IsNullOrEmpty(path))
            return;

        var refused = await _history.DeleteAsync(path, runs, CancellationToken.None);

        // Not something to come back to, in this workspace or any other.
        foreach (var run in runs)
            _openRuns.Forget(run.RunId);

        if (_vm.PastRun is { } open && runs.Any(r => r.RunId == open.Record.RunId))
            _vm.ShowLiveRun();

        await LoadRunsAsync();

        if (refused is not null)
            _vm.Runs.Fail(refused);
    }

    /// <summary>
    /// Forgets an interrupted run: its checkpoint goes, and with it the offer to resume. Nothing else - the files it
    /// changed stay in the workspace, and a record it left in the history stays there with its own delete.
    /// </summary>
    private async Task ForgetUnfinishedAsync(RunCheckpoint checkpoint)
    {
        var path = _vm.WorkspacePath.Trim();
        if (string.IsNullOrEmpty(path))
            return;

        // What it changed is named in the question, and put back only when the person says so (RunHistoryService).
        var question = RunHistoryService.ForgetUnfinished(path, checkpoint);
        var putBack = false;
        if (question.Alternative is null)
        {
            if (!await AskAsync(question))
                return;
        }
        else
        {
            var choice = await ConfirmWindow.ChooseAsync(this, question.Headline, question.Body,
                question.Confirm, question.Alternative, question.Cancel);
            if (choice == ConfirmChoice.Cancel)
                return;
            putBack = choice == ConfirmChoice.Confirm;
        }

        var (kept, problem) = await _history.ForgetUnfinishedAsync(path, checkpoint, putBack, CancellationToken.None);
        if (kept is not null)
            _vm.Runs.Fail(kept);
        if (problem is not null)
        {
            _vm.Runs.Fail(problem);
            return;
        }

        await LoadRunsAsync();
    }

    private async Task DeleteRunAsync(RunSummary record)
    {
        if (!await AskAsync(RunHistoryService.DeleteOne(record)))
            return;

        var path = _vm.WorkspacePath.Trim();
        if (string.IsNullOrEmpty(path))
            return;

        if (await _history.DeleteAsync(path, record, CancellationToken.None) is { } problem)
        {
            _vm.Runs.Fail(problem);
            return;
        }

        _openRuns.Forget(record.RunId);

        // Reading a run that no longer exists is worse than being sent back to the live one.
        if (_vm.PastRun?.Record.RunId == record.RunId)
            _vm.ShowLiveRun();

        await LoadRunsAsync();
    }

    /// <summary>
    /// Loads the workspace's runs for the context column - retention applied, interrupted runs read on their own
    /// (RunHistoryService). The store type is the environment's choice (SQLite, MySQL or files).
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

        var history = await _history.LoadAsync(path, CancellationToken.None);
        _vm.Runs.ShowResumable(history.Resumable);
        if (history.Problem is not null)
        {
            // A workspace whose store is unreachable is a fact to show, not a crash: the rest of
            // the window is still perfectly usable.
            _vm.Runs.Fail(history.Problem);
            return;
        }

        _vm.Runs.Show(history.Runs);
        // The history has just changed, and a live row whose run is now IN it must go. See
        // RunColumn.Live: the window learns a run ended by two paths and they arrive in
        // whatever order they arrive in.
        RefreshLive();
    }

    private async Task ShowTimelineAsync()
    {
        var workspacePath = _vm.WorkspacePath.Trim();
        if (string.IsNullOrEmpty(workspacePath))
            return;

        // The service's Timeline page reads it the same way and says the same when it cannot (WorkspaceReports).
        ShowViewer(WorkspaceReports.TimelineTitle, await WorkspaceReports.TimelineAsync(workspacePath, CancellationToken.None));
    }

    // ── IDecisionPresenter: the inline approval card ─────────────────────────
    // Which answers are offered, what each remembers, one question at a time and a cancelled question never answered
    // are the DecisionRouter's (Enactive.Application), where they are tested. The card only draws them.

    Task IDecisionPresenter.ShowAsync(DecisionRequest request, IReadOnlyList<DecisionChoice> choices, Func<DecisionChoice, bool> answer)
        => Dispatcher.UIThread.InvokeAsync(() =>
        {
            _shownDecision = request.Id;
            var root = request.Action?.WorkingDirectory;
            _vm.DecisionText = root is null ? request.Topic : $"{request.Topic} — {root} (run {request.Action!.RunId})";
            // The full action, not the summary: this is what the click authorises.
            _vm.DecisionDetail = request.FullText;
            _vm.DecisionOptions.Clear();
            foreach (var choice in choices)
            {
                var chosen = choice;
                // Taken down at the click, not when the run gets round to it.
                _vm.DecisionOptions.Add(new DecisionOptionViewModel(chosen.Label, () =>
                {
                    if (answer(chosen)) ClearDecisionCard(request.Id);
                }));
            }
            _vm.IsDecisionVisible = true;
        }).GetTask();

    Task IDecisionPresenter.ClearAsync(DecisionRequest request)
        => Dispatcher.UIThread.InvokeAsync(() => ClearDecisionCard(request.Id)).GetTask();

        /// <summary>
        /// Takes THIS request's card off screen. Checking which request it belongs to matters: by the
        /// time a cancellation is dispatched, a later run may already have put its own card up, and
        /// clearing that one would leave the new run waiting on something the user can no longer answer.
        /// </summary>
        private void ClearDecisionCard(Guid request)
        {
            if (_shownDecision != request)
                return;

            _shownDecision = null;
            _vm.IsDecisionVisible = false;
            _vm.DecisionOptions.Clear();
            _vm.DecisionText = string.Empty;
            _vm.DecisionDetail = string.Empty;
        }

        // Workspace-scoped approvals live OUTSIDE the workspace — see ApprovalStore in Core, which is
        // where the rules are and where they are tested. This window passes the FOLDER and decides
        // nothing: which key an approval is filed under, whether a shell may ever be remembered, and
        // whether a legacy in-workspace file counts are all answered in there.
        //
        // They used to be answered here, correctly, in a WinExe no test project references.
        /// <summary>Where a provider runs, by its id, against this window's settings (ModelReaches).</summary>
        private ModelReach ReachOf(string? providerId) => ModelReaches.Of(_settings, providerId);

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

        private void ApplySettings()
        {
            _globalInstructions = _settings.GlobalInstructions;

            // Nothing to build an engine out of is a STATE, not an error. A machine where nobody has
            // chosen a model now says so — where it used to be silently configured for a model name
            // compiled into AppSettings, and the first anyone heard of it was a 404. The window opens
            // either way; the run paths read this.
            _engineProblem = EngineComposition.Missing(_settings) is { Count: > 0 } missing
                ? string.Join(" ", missing)
                : null;

            if (_engineProblem is not null)
            {
                _engine = null;
                _vm.WorkerRoles.Clear();
                return;
            }

            // The engine itself — providers, team, router, tools — from the composition every host shares.
            // It was built inline here, which is why nothing could check it and why the console's own
            // version had drifted onto a different provider kind and a model nobody had installed.
            // "Allow (session)" answers for every run this process starts, watched or not, and outlives
            // this engine: the next save builds another.
            _engine = EngineComposition.Build(_settings, _http, _log, _sessionApprovals,
                metrics => Dispatcher.UIThread.Post(() => _vm.Performance.Add(metrics)));

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
                foreach (var worker in _engine.Workers.All)
                    _vm.WorkerRoles.Add(worker.Role);
                _vm.SelectedWorkerIndex = keep >= 0 && keep < _vm.WorkerRoles.Count ? keep : 0;
        }
        finally
        {
            _vm.IsLoadingWorkspaceDefaults = false;
        }
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
}
