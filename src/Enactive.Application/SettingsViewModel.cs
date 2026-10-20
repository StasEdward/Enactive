namespace Enactive.Application;

using Enactive.Application;
using Enactive.Agents;

using System.Collections.ObjectModel;
using Enactive.Application.Mvvm;
using Enactive.Providers;
using Enactive.Core.Execution;
using Enactive.Workspace;
using Enactive.Core.Providers;
using Enactive.Settings;
using Enactive.Core.Permissions;

/// <summary>One row of the Providers list. Wraps the config so the row can be told to re-read it
/// after the edit dialog writes back, instead of rebuilding the whole list.</summary>
internal sealed class ProviderRow : ObservableObject
{
    public ProviderRow(ProviderConfig config, Action<ProviderRow> edit, Action<ProviderRow> remove)
    {
        Config = config;
        // The buttons are ON the card, so the commands are the card's. Nothing has to be selected
        // first to act on the thing you are already pointing at.
        EditCommand = new RelayCommand(() => edit(this));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public RelayCommand EditCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public ProviderConfig Config { get; }

    public string Name => Config.Id;
    public string Meta => $"{Config.Kind}  ·  {Config.BaseUrl}";

    public string Reach => IsLocal ? "local" : "remote";

    /// <summary>On this machine - drawn green, and blue when not (Palette.Local).</summary>
    public bool IsLocal =>
        Config.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
        || Config.BaseUrl.Contains("127.0.0.1", StringComparison.Ordinal)
        || Config.BaseUrl.Contains("[::1]", StringComparison.Ordinal);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Meta));
        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(Reach));

        // A card that was edited has not been re-checked, and the light must not go on claiming
        // what it learned about the address that was there before.
        Health = ProviderStatus.Unknown;
    }

    /// <summary>
    /// What the last check learned, and when. <see cref="ProviderStatus.Unknown"/> until one is
    /// made — the light starts grey, never green.
    /// </summary>
    public ProviderStatus Health
    {
        get => _health;
        set
        {
            _health = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HealthTip));
        }
    }

    private ProviderStatus _health = ProviderStatus.Unknown;


    /// <summary>
    /// The summary AND the time it was learned.
    ///
    /// <para>The time is not decoration. A green dot on its own says how things were at an unstated
    /// moment and lets the reader supply "now" — the same shape as a card that went on saying
    /// "Waiting" after the answer had been accepted.</para>
    /// </summary>
    public string HealthTip => Health.Health == ProviderHealth.Unknown
        ? "Not checked. Open this provider and press Test."
        // The time in one format on every machine: "HH:mm:ss" in a format string takes the culture's time separator,
        // which is not ':' everywhere.
        : $"{Health.Summary}\nChecked at {Health.At.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}";
}

/// <summary>One row of the Team list, on the same terms as <see cref="ProviderRow"/>.</summary>
internal sealed class WorkerRow : ObservableObject
{
    public WorkerRow(WorkerConfig config, Action<WorkerRow> edit, Action<WorkerRow> remove)
    {
        Config = config;
        EditCommand = new RelayCommand(() => edit(this));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public RelayCommand EditCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public WorkerConfig Config { get; }

    public string Name => Config.Role;
    public string Meta => Config.Summary();

    /// <summary>
    /// The worker's permission level, drawn on the same green-blue-yellow-red scale the autonomy slider
    /// uses (Palette.Autonomy), because it is the same question: how much this one may do without asking.
    /// </summary>
    public int Autonomy => (int)Config.Level;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Meta));
        OnPropertyChanged(nameof(Autonomy));
    }
}

/// <summary>
/// Settings over the universal team schema: General, Providers, Team and Phases.
/// Edits a throwaway clone of the settings; only Save hands it to <c>onSaved</c>.
///
/// The model catalog is a live collection rather than a snapshot, which is the whole reason the old
/// code-behind's picker plumbing is gone: adding a model on the Providers tab now reaches the Phases
/// pickers by itself. No tab-change handler, no deferred refresh, and no reassigning a ComboBox's
/// ItemsSource from inside another control's SelectionChanged - which is what used to crash, and then
/// hang, this window.
/// </summary>
internal sealed partial class SettingsViewModel : ObservableObject
{
    public const string NoneLabel = "(none)";

    private readonly AppSettings _working;
    private readonly Action<AppSettings> _onSaved;
    private readonly IStartupEntry _startup;
    private readonly bool _editsFilesHere;

    private string _numCtxText;
    private string _globalInstructions;
    private bool _disableThinking;
    private bool _verifyWrites;
    private bool _allowImplicitToolCalls;
    private string _reviewRetriesText = "1";
    private bool _revertRejectedSteps;
    private string _maxParallelStepsText;
    private string _evidenceBudgetText;
    private string _logRetentionDaysText;
    private bool _logPromptBodies;
    private int _shellCommandsIndex;
    private bool _closeToTray;
    private bool _runAtStartup;
    private readonly Task _startupLoad;
    private bool _startupLoaded;
    private async Task LoadStartupAsync()
    {
        RunAtStartup = await Task.Run(_startup.IsEnabled);
        _startupLoaded = true;
        OnPropertyChanged(nameof(StartupSupported));
    }
    private string _startupNote = string.Empty;
    private ProviderRow? _selectedProvider;
    private WorkerRow? _selectedWorker;
    private string _plan;
    private string _review;
    private string _executeLight;
    private string _executeHeavy;

    // ── Which section is showing ──────────────────────────────────────────────
    // Sections, not tabs: a tab strip stops being readable somewhere around six, and this
    // window is going to keep growing. The list on the left has room for a group heading,
    // which is what lets everything about the AI sit together under one word.
    private const int SectionGeneral = 0;
    private const int SectionAccount = 1;
    private const int SectionAbout = 2;
    private const int SectionAiGeneral = 3;
    private const int SectionAiProviders = 4;
    private const int SectionAiTeam = 5;
    private const int SectionAiPhases = 6;
    private const int SectionRemote = 9;

    private int _section = SectionGeneral;

    /// <summary>
    /// Opens at the top of the list. A window that lands somewhere other than its first item makes
    /// you check where you are before you can read anything.
    /// </summary>
    public int Section
    {
        get => _section;
        set
        {
            if (!Set(ref _section, value))
                return;
            OnPropertyChanged(nameof(IsGeneral));
            OnPropertyChanged(nameof(IsAccount));
            OnPropertyChanged(nameof(IsAbout));
            OnPropertyChanged(nameof(IsAiGeneral));
            OnPropertyChanged(nameof(IsAiProviders));
            OnPropertyChanged(nameof(IsAiTeam));
            OnPropertyChanged(nameof(IsAiPhases));
            OnPropertyChanged(nameof(IsMcp));
            OnPropertyChanged(nameof(IsTemplates));
            OnPropertyChanged(nameof(IsChecks));
            OnPropertyChanged(nameof(IsRemote));
            OnPropertyChanged(nameof(IsWritableRoots));
            OnPropertyChanged(nameof(IsSmtp));
            OnPropertyChanged(nameof(IsWeb));

            // Checked when the list is opened, once. Not on every visit: these are network calls
            // made without being asked, and a light that re-checks each time somebody passes
            // through the section spends the person's connection to tell them what it already knew.
            if (_section == SectionAiProviders && !_providersChecked)
            {
                _providersChecked = true;
                _ = CheckProvidersAsync();
            }
        }
    }

    private bool _providersChecked;

    /// <summary>
    /// Asks every provider whether it is there, in the background, without blocking the window.
    ///
    /// <para>They are asked in PARALLEL and each row updates as its own answer arrives. One
    /// unreachable endpoint waits out the client's timeout, and doing this in sequence would mean a
    /// single dead provider hides the state of every provider after it — the reverse of what these
    /// lights are for.</para>
    /// </summary>
    private async Task CheckProvidersAsync()
    {
        await Task.WhenAll(Providers.ToArray().Select(CheckProviderAsync));
    }

    private async Task CheckProviderAsync(ProviderRow row)
    {
        var config = row.Config;

        try
        {
            row.Health = await ProviderCalls.CheckAsync(config, config.Models?.FirstOrDefault());
        }
        catch (Exception ex)
        {
            // The probe answers rather than throws, so this is a fault in the check itself. It
            // still has to reach the light: a dot that stayed grey because the checker broke would
            // be indistinguishable from one nobody had asked about.
            row.Health = new ProviderStatus(
                ProviderHealth.Unreachable, "The check itself failed: " + ex.Message, [], DateTimeOffset.UtcNow);
        }
    }

    public bool IsGeneral => _section == SectionGeneral;
    public bool IsAccount => _section == SectionAccount;
    public bool IsAbout => _section == SectionAbout;
    public bool IsAiGeneral => _section == SectionAiGeneral;
    public bool IsAiProviders => _section == SectionAiProviders;
    public bool IsAiTeam => _section == SectionAiTeam;
    public bool IsAiPhases => _section == SectionAiPhases;

    public RelayCommand ShowGeneralCommand { get; }
    public RelayCommand ShowAccountCommand { get; }
    public RelayCommand ShowAboutCommand { get; }
    public RelayCommand ShowAiGeneralCommand { get; }
    public RelayCommand ShowAiProvidersCommand { get; }
    public RelayCommand ShowAiTeamCommand { get; }
    public RelayCommand ShowAiPhasesCommand { get; }

    // ── Remote access: the window's own pane (IHostSettingsPane) ──────────────
    public bool IsRemote => _section == SectionRemote;

    public RelayCommand ShowRemoteCommand { get; }

    /// <summary>A pane only this host has, or null: what Save waits for and lets write itself into the settings.</summary>
    private readonly IHostSettingsPane? _hostPane;

    /// <param name="workspaceRoot">
    /// The workspace open in the main window, or null. Templates need it: a template saved "for this
    /// workspace" lives in its .enactive folder, and with no workspace open that scope does not exist
    /// - which the pane says rather than offering a choice that cannot be honoured.
    /// </param>
    /// <param name="toolNames">
    /// The tools this build registers. The template editor lists them so a restriction is chosen from
    /// what exists instead of typed - three built-ins once denied "create_dir" while the tool is
    /// called "create_directory", which restricted nothing at all.
    /// </param>
    /// <param name="startup">
    /// Where "start with Windows" is read and changed: the window passes the real Run key, and anything
    /// else that builds this view model passes its own, so the registry of the machine it runs on is
    /// left alone. <see cref="NoStartupEntry"/> is a host that has no start-up of its own to offer - the
    /// service, which Windows starts as a service: nothing is read or changed, and nothing is said about it
    /// either. Required: with a default, a desktop caller that forgot it lost start-up without a word.
    /// </param>
    /// <param name="editsFilesHere">
    /// Whether the Templates and Writable folders panes write their files themselves - the window's, where a
    /// template is saved or deleted and a grant revoked at once. False for a host whose pages change those
    /// elsewhere, as the person and after its own checks (the service: templates through its library, grants not
    /// at all): its rows' Add, Edit, Duplicate, Remove and Revoke then do nothing, so no control a page might wire
    /// later can write past those checks. Required, for the same reason as <paramref name="startup"/>.
    /// </param>
    /// <param name="hostPane">
    /// Makes the pane only this host has, over the working copy this edits - the window's Remote access.
    /// Null for a host with none.
    /// </param>
    /// <param name="providerCalls">
    /// How a provider is asked whether it is there - the lights on the Providers list, and the editor's Test and
    /// Fetch (<see cref="ProviderCalls"/>). Null asks it directly with the key the settings hold (the window).
    /// </param>
    /// <param name="writableRoots">
    /// The standing grants the Writable folders pane lists, and the workspaces it names them by. Null for each is the
    /// machine's own (WritableRoots.Default, and the window's workspace list in %APPDATA%), which the window shows; a
    /// host whose engine keeps its workspaces elsewhere names them, and a test names files of its own.
    /// </param>
    public SettingsViewModel(
        AppSettings settings, Action<AppSettings> onSaved, IStartupEntry startup, bool editsFilesHere,
        string? workspaceRoot = null, IReadOnlyList<string>? toolNames = null,
        Func<AppSettings, IHostSettingsPane>? hostPane = null,
        IProviderCalls? providerCalls = null,
        WritableRoots? writableRoots = null, Func<IEnumerable<string>>? knownWorkspaces = null)
    {
        _working = settings.Clone();
        _onSaved = onSaved;
        _startup = startup;
        _editsFilesHere = editsFilesHere;
        ProviderCalls = providerCalls ?? DirectProviderCalls.Instance;
        ToolNames = toolNames ?? Array.Empty<string>();
        InitializeMcp();
        InitializeTemplates(workspaceRoot);
        _hostPane = hostPane?.Invoke(_working);
        InitializeSmtp();
        InitializeWeb();
        InitializeWritableRoots(writableRoots, knownWorkspaces);

        _numCtxText = _working.Engine.NumCtx?.ToString() ?? string.Empty;
        _generationAction = _working.Engine.GenerationBudgets.Action.ToString();
        _generationFileWrite = _working.Engine.GenerationBudgets.FileWrite.ToString();
        _generationFinalAnswer = _working.Engine.GenerationBudgets.FinalAnswer.ToString();
        _generationHandover = _working.Engine.GenerationBudgets.Handover.ToString();
        _generationPlanner = _working.Engine.GenerationBudgets.Planner.ToString();
        _repairModel = _working.Engine.RepairConsultation.Model is { } repairModel ? repairModel.ProviderId + "/" + repairModel.Model : "";
        _repairThreshold = _working.Engine.RepairConsultation.FailedRepairs.ToString();

        _globalInstructions = _working.GlobalInstructions;
        _disableThinking = _working.Engine.DisableThinking;
        _verifyWrites = _working.VerifyWrites;
        _allowImplicitToolCalls = _working.Engine.AllowImplicitToolCalls;
        _reviewRetriesText = _working.Engine.ReviewRetries.ToString();
        _revertRejectedSteps = _working.Engine.RevertRejectedSteps;
        _maxParallelStepsText = _working.Engine.MaxParallelSteps.ToString();
        // Blank is automatic (EngineOptions.EvidenceBudget); EvidenceBudgetAuto says what that comes to.
        _evidenceBudgetText = _working.Engine.EvidenceBudget?.ToString() ?? string.Empty;
        _logRetentionDaysText = _working.LogRetentionDays.ToString();
        _logPromptBodies = _working.LogPromptBodies;
        _shellCommandsIndex = (int)_working.ShellCommands;
        _closeToTray = _working.CloseToTray;

        // Read from the system, not from settings.json: the Run key is the truth, and a copy would
        // drift the first time the user removed it in Task Manager.
        _startupLoad = LoadStartupAsync();
        _startupNote = _startup.Supported || _startup is NoStartupEntry
            ? string.Empty
            : "Only Windows starts programs this way; this desktop does not.";

        foreach (var p in _working.Providers)
            Providers.Add(NewProviderRow(p));
        foreach (var w in _working.Workers)
            Workers.Add(NewWorkerRow(w));

        ShowGeneralCommand = new RelayCommand(() => Section = SectionGeneral);
        ShowAccountCommand = new RelayCommand(() => Section = SectionAccount);
        ShowAboutCommand = new RelayCommand(() => Section = SectionAbout);
        ShowAiGeneralCommand = new RelayCommand(() => Section = SectionAiGeneral);
        ShowAiProvidersCommand = new RelayCommand(() => Section = SectionAiProviders);
        ShowAiTeamCommand = new RelayCommand(() => Section = SectionAiTeam);
        ShowAiPhasesCommand = new RelayCommand(() => Section = SectionAiPhases);
        ShowRemoteCommand = new RelayCommand(() =>
        {
            Section = SectionRemote;
            _hostPane?.Shown();
        });

        foreach (var component in BuildInfo.Components())
            Components.Add(component);

        _plan = ToSelection(_working.Bindings.Plan);
        _review = ToSelection(_working.Bindings.Review);
        _executeLight = ToSelection(_working.Bindings.ExecuteLight);
        _executeHeavy = ToSelection(_working.Bindings.ExecuteHeavy);
        SyncCatalog();

        AddProviderCommand = new RelayCommand(AddProvider);

        AddWorkerCommand = new RelayCommand(AddWorker);

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !HostPaneBusy);
        if (_hostPane is not null)
            _hostPane.BusyChanged += SaveCommand.RaiseCanExecuteChanged;
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    private bool HostPaneBusy => _hostPane?.Busy == true;

    /// <summary>How a provider is asked whether it is there; the editors a host opens ask the same way.</summary>
    public IProviderCalls ProviderCalls { get; }

    public event Action? CloseRequested;

    /// <summary>Asks the view to open the provider editor: (config, what to do once it saved).</summary>
    public event Action<ProviderConfig, Action>? ProviderEditRequested;

    /// <summary>Asks a yes/no question. The window answers it, because the dialog needs a parent.</summary>
    public event Func<string, string, Task<bool>>? ConfirmRequested;

    /// <summary>Asks the view to open the worker editor: (config, model catalog, what to do once it saved).</summary>
    public event Action<WorkerConfig, IReadOnlyList<string>, Action>? WorkerEditRequested;

    // ── General ───────────────────────────────────────────────────────────────
    private string _generationAction = "";
    private string _repairModel = "";
    private string _repairThreshold = "0";
    public string RepairModelText { get => _repairModel; set => Set(ref _repairModel, value); }
    public string RepairThresholdText { get => _repairThreshold; set => Set(ref _repairThreshold, value); }
    private string _generationFileWrite = "";
    private string _generationFinalAnswer = "";
    private string _generationHandover = "";
    private string _generationPlanner = "";
    public string GenerationPlannerText { get => _generationPlanner; set => Set(ref _generationPlanner, value); }
    public string GenerationActionText { get => _generationAction; set => Set(ref _generationAction, value); }
    public string GenerationFileWriteText { get => _generationFileWrite; set => Set(ref _generationFileWrite, value); }
    public string GenerationFinalAnswerText { get => _generationFinalAnswer; set => Set(ref _generationFinalAnswer, value); }
    public string GenerationHandoverText { get => _generationHandover; set => Set(ref _generationHandover, value); }
    public string NumCtxText { get => _numCtxText; set => Set(ref _numCtxText, value); }
    public string GlobalInstructions { get => _globalInstructions; set => Set(ref _globalInstructions, value); }
    public bool DisableThinking { get => _disableThinking; set => Set(ref _disableThinking, value); }
    public bool VerifyWrites { get => _verifyWrites; set => Set(ref _verifyWrites, value); }
    public bool AllowImplicitToolCalls { get => _allowImplicitToolCalls; set => Set(ref _allowImplicitToolCalls, value); }
    public string ReviewRetriesText { get => _reviewRetriesText; set => Set(ref _reviewRetriesText, value); }
    public bool RevertRejectedSteps { get => _revertRejectedSteps; set => Set(ref _revertRejectedSteps, value); }
    public string MaxParallelStepsText { get => _maxParallelStepsText; set => Set(ref _maxParallelStepsText, value); }
    public string EvidenceBudgetText { get => _evidenceBudgetText; set => Set(ref _evidenceBudgetText, value); }

    /// <summary>
    /// What a blank evidence budget will be: automatic, from the window of the model the Review phase is bound to, as the
    /// providers on this screen declare it now - the most it can be (ReviewEvidence.UpTo; a step whose own files fill
    /// the window gets less). Shown where the box is blank, so "automatic" is never a number nobody can see.
    /// </summary>
    public string EvidenceBudgetAuto
    {
        get
        {
            if (AppSettings.ParseRef(FromSelection(Review)) is not { } reviewer)
                return "auto - no model is bound to Review";
            static string N(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            var provider = _working.Providers.FirstOrDefault(p => p.Id == reviewer.ProviderId);
            // The typed window, or the one the reviewer's server stated for this model, once a run has asked it
            // (ServerWindow) - read, never asked: a settings screen does not wait on a server. Without it the hint said
            // "declares no context window" and 6,000 for a reviewer whose reviews were getting the server's window.
            var stated = provider is { ContextWindowTokens: null }
                ? ServerWindow.Known(provider.Id, provider.Kind, provider.BaseUrl, reviewer.Model)
                : null;
            var window = provider?.ContextWindowTokens ?? stated;
            // The same inputs the review's own budget reads from the provider (ReviewEvidence.For): the window, and the
            // working size and handover share that hold a review below it.
            var upTo = ReviewEvidence.For(null, window, 0, 0, provider?.WorkingContextTokens, provider?.HandoverAtPercent);
            return window is { } tokens
                ? $"auto - up to {N(upTo.Chars)} characters, from {reviewer.ProviderId}/{reviewer.Model}'s {N(tokens)}-token window"
                  + (stated is not null ? ", as its server states it" : "")
                  + (upTo.WorkingTokens is { } worked ? $", worked at {N(worked)} tokens" : "")
                : $"auto - {N(upTo.Chars)} characters: {reviewer.ProviderId} declares no context window for {reviewer.Model}";
        }
    }
    public string LogRetentionDaysText { get => _logRetentionDaysText; set => Set(ref _logRetentionDaysText, value); }
    public bool LogPromptBodies { get => _logPromptBodies; set => Set(ref _logPromptBodies, value); }
    public int ShellCommandsIndex { get => _shellCommandsIndex; set => Set(ref _shellCommandsIndex, value); }

    /// <summary>
    /// What the main window's close button does. Two mutually exclusive options, so the pair moves
    /// together: setting one clears the other, and the view binds a radio to each rather than
    /// asking the user to read a checkbox and work out what "unchecked" means.
    /// </summary>
    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (Set(ref _closeToTray, value))
                OnPropertyChanged(nameof(ExitOnClose));
        }
    }

    /// <summary>
    /// Whether the computer starts Enactive at login. Applied on Save after the rest is written, and
    /// re-read from the system afterwards - if the registry refused, the box goes back to the truth
    /// rather than claiming something that did not happen.
    /// </summary>
    public bool RunAtStartup { get => _runAtStartup; set => Set(ref _runAtStartup, value); }

    public bool StartupSupported => _startup.Supported && _startupLoaded;

    /// <summary>Empty when there is nothing to say - a note that is always there is not read.</summary>
    public string StartupNote
    {
        get => _startupNote;
        set
        {
            if (Set(ref _startupNote, value))
                OnPropertyChanged(nameof(HasStartupNote));
        }
    }

    public bool HasStartupNote => _startupNote.Length > 0;

    // ── About ─────────────────────────────────────────────────────────────────
    public string AppVersion => BuildInfo.AppVersion;
    public string BuiltAt => BuildInfo.BuiltAt;
    public string RuntimeInfo => BuildInfo.Runtime;

    /// <summary>
    /// Every assembly this build ships. They come from one repo and one build, so they carry the
    /// same version - and that is the point of listing them: two different numbers here mean a
    /// stale DLL is in the output folder.
    /// </summary>
    public ObservableCollection<BuildComponent> Components { get; } = new();

    public bool ExitOnClose
    {
        get => !_closeToTray;
        set
        {
            if (value)
                CloseToTray = false;
        }
    }

    // ── Providers / Team ──────────────────────────────────────────────────────
    public ObservableCollection<ProviderRow> Providers { get; } = new();
    public ObservableCollection<WorkerRow> Workers { get; } = new();

    /// <summary>Only the highlight now - the buttons that act on a row are on the row.</summary>
    public ProviderRow? SelectedProvider { get => _selectedProvider; set => Set(ref _selectedProvider, value); }

    public WorkerRow? SelectedWorker { get => _selectedWorker; set => Set(ref _selectedWorker, value); }

    // ── Phases ────────────────────────────────────────────────────────────────
    /// <summary>Every model of every provider, plus "(none)". Live: edits on other tabs land here.</summary>
    public ObservableCollection<string> ModelCatalog { get; } = new();

    public string Plan { get => _plan; set => Set(ref _plan, value); }
    public string Review
    {
        get => _review;
        set
        {
            if (Set(ref _review, value))
                OnPropertyChanged(nameof(EvidenceBudgetAuto));
        }
    }
    public string ExecuteLight { get => _executeLight; set => Set(ref _executeLight, value); }
    public string ExecuteHeavy { get => _executeHeavy; set => Set(ref _executeHeavy, value); }

    public RelayCommand AddProviderCommand { get; }
    public RelayCommand AddWorkerCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    private void AddProvider()
    {
        var config = new ProviderConfig { Kind = ProviderKind.OpenAiCompatible };
        ProviderEditRequested?.Invoke(config, () =>
        {
            _working.Providers.Add(config);
            Providers.Add(NewProviderRow(config));
            SyncCatalog();
        });
    }

    /// <summary>Rows are built here so each one is handed the two things it can do to itself.</summary>
    private ProviderRow NewProviderRow(ProviderConfig config)
        => new(config, EditProvider, row => _ = RemoveProviderAsync(row));

    private WorkerRow NewWorkerRow(WorkerConfig config)
        => new(config, EditWorker, row => _ = RemoveWorkerAsync(row));

    private void EditProvider(ProviderRow row)
    {
        ProviderEditRequested?.Invoke(row.Config, () =>
        {
            row.Refresh();
            SyncCatalog();
        });
    }

    /// <summary>
    /// Removing a provider takes its API key with it, and a key cannot be read back out of the app
    /// to retype. So it asks - and says the one thing that makes the answer easy: nothing is
    /// written anywhere until Save.
    /// </summary>
    private async Task RemoveProviderAsync(ProviderRow row)
    {
        if (!await ConfirmAsync(
                $"Remove provider \u201c{row.Name}\u201d?",
                "Its settings and API key are removed from Enactive. Nothing changes at the provider itself, and closing Settings with Cancel puts it back."))
            return;

        _working.Providers.Remove(row.Config);
        Providers.Remove(row);
        SelectedProvider = null;
        SyncCatalog();
    }

    private void AddWorker()
    {
        var config = new WorkerConfig();
        WorkerEditRequested?.Invoke(config, _working.ModelCatalog(), () =>
        {
            _working.Workers.Add(config);
            Workers.Add(NewWorkerRow(config));
        });
    }

    private void EditWorker(WorkerRow row)
        => WorkerEditRequested?.Invoke(row.Config, _working.ModelCatalog(), row.Refresh);

    /// <summary>A worker is mostly its instructions, which are typed by hand and nowhere else.</summary>
    private async Task RemoveWorkerAsync(WorkerRow row)
    {
        if (!await ConfirmAsync(
                $"Remove worker \u201c{row.Name}\u201d?",
                "Its instructions go with it. Closing Settings with Cancel puts it back."))
            return;

        _working.Workers.Remove(row.Config);
        Workers.Remove(row);
        SelectedWorker = null;
    }

    /// <summary>Asks the window, which owns the dialog. No handler attached means yes - a view model
    /// under test should not be blocked by a question nobody is there to answer.</summary>
    private async Task<bool> ConfirmAsync(string headline, string detail)
        => ConfirmRequested is null || await ConfirmRequested(headline, detail);

    private async Task SaveAsync()
    {
        // Disabled while a connection code is applied; checked here too, because a disabled button is
        // a promise the window keeps, and a key press or a second binding is not bound by it.
        if (HostPaneBusy)
            return;

        await _startupLoad;
        _working.GlobalInstructions = GlobalInstructions;
        _working.VerifyWrites = VerifyWrites;
        // The engine's switches, as one new value of the settings' own section - by the rules the service's settings
        // page uses too (EngineSwitches).
        _working.Engine = new EngineSwitches
        {
            NumCtx = NumCtxText,
            GenerationAction = GenerationActionText,
            GenerationFileWrite = GenerationFileWriteText,
            GenerationFinalAnswer = GenerationFinalAnswerText,
            GenerationHandover = GenerationHandoverText,
            GenerationPlanner = GenerationPlannerText,
            RepairModel = RepairModelText,
            RepairThreshold = RepairThresholdText,
            DisableThinking = DisableThinking,
            AllowImplicitToolCalls = AllowImplicitToolCalls,
            ReviewRetries = ReviewRetriesText,
            RevertRejectedSteps = RevertRejectedSteps,
            MaxParallelSteps = MaxParallelStepsText,
            EvidenceBudget = EvidenceBudgetText
        }.ApplyTo(_working.Engine);
        // A number that does not parse means the default, not zero: zero here is "keep everything",
        // a deliberate choice, and reaching it by typing nonsense would be the opposite of one.
        _working.LogRetentionDays = EngineSwitches.NumberOr(LogRetentionDaysText, FileLogSink.DefaultRetentionDays, v => v >= 0);
        _working.LogPromptBodies = LogPromptBodies;
        _working.ShellCommands = Enum.IsDefined((ShellCommandPolicy)ShellCommandsIndex)
            ? (ShellCommandPolicy)ShellCommandsIndex
            : ShellCommandPolicy.Follow;
        _working.CloseToTray = CloseToTray;

        _hostPane?.Save(_working);
        SaveSmtp();
        SaveWeb();

        _working.Bindings.Plan = FromSelection(Plan);
        _working.Bindings.Review = FromSelection(Review);
        _working.Bindings.ExecuteLight = FromSelection(ExecuteLight);
        _working.Bindings.ExecuteHeavy = FromSelection(ExecuteHeavy);

        // Validate, write, and only then change start-up: the order is SettingsSave's, where it is
        // tested on their own, apart from this view model and whatever host it is in.
        var result = await SettingsSave.RunAsync(_working, _onSaved, _startup, RunAtStartup);

        if (result.Note.Length > 0)
            StartupNote = result.Note;

        if (!result.Saved)
            return;

        // The settings are saved either way. The window stays open only when there is something
        // the user has not seen yet - a note nobody reads because the window closed on top of it is
        // the same as no note at all.
        if (result.StartupRefused)
        {
            // Show what actually happened rather than a tick that means nothing.
            RunAtStartup = result.StartupEnabled;
            return;
        }

        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Brings the catalog in line with the providers, EDITING IT IN PLACE. Clearing and refilling
    /// would momentarily empty the pickers, and each one would write its now-null selection back
    /// through its binding - the settings would lose their phase bindings just by opening the tab.
    /// </summary>
    private void SyncCatalog()
    {
        var wanted = new List<string> { NoneLabel };
        wanted.AddRange(_working.ModelCatalog());

        // A bound model that no provider offers any more is kept, so a binding is never silently
        // dropped just because its provider was renamed or removed.
        foreach (var bound in new[] { Plan, Review, ExecuteLight, ExecuteHeavy })
            if (!string.IsNullOrEmpty(bound) && !wanted.Contains(bound))
                wanted.Add(bound);

        for (var i = ModelCatalog.Count - 1; i >= 0; i--)
            if (!wanted.Contains(ModelCatalog[i]))
                ModelCatalog.RemoveAt(i);

        for (var i = 0; i < wanted.Count; i++)
            if (!ModelCatalog.Contains(wanted[i]))
                ModelCatalog.Insert(Math.Min(i, ModelCatalog.Count), wanted[i]);

        // Run after every change to the providers: the reviewer's declared window is what the automatic budget follows.
        OnPropertyChanged(nameof(EvidenceBudgetAuto));
    }

    private static string ToSelection(string bound) => string.IsNullOrWhiteSpace(bound) ? NoneLabel : bound;

    private static string FromSelection(string selection)
        => string.IsNullOrWhiteSpace(selection) || selection == NoneLabel ? string.Empty : selection;
}
