namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Media;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.Execution;
using Enactive.Workspace;
using Enactive.Core.Providers;

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

    /// <summary>
    /// Green when the provider is on this machine, blue when it is not. That is the one thing about
    /// a provider worth seeing without reading: whether your code leaves the box to reach it.
    /// </summary>
    public IBrush EdgeBrush => IsLocal ? Brand.Success : Brand.Info;

    public string Reach => IsLocal ? "local" : "remote";

    private bool IsLocal =>
        Config.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
        || Config.BaseUrl.Contains("127.0.0.1", StringComparison.Ordinal)
        || Config.BaseUrl.Contains("[::1]", StringComparison.Ordinal);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Meta));
        OnPropertyChanged(nameof(EdgeBrush));
        OnPropertyChanged(nameof(Reach));
    }
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
    public string Meta => $"{Config.Model}  ·  {Config.Level}";

    /// <summary>
    /// The worker's permission level on the same green-blue-yellow-red scale the autonomy slider
    /// uses, because it is the same question: how much this one may do without asking.
    /// </summary>
    public IBrush EdgeBrush => Brand.Autonomy((int)Config.Level);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Meta));
        OnPropertyChanged(nameof(EdgeBrush));
    }
}

/// <summary>
/// Settings over the universal team schema (Docs/MODELS.md): General, Providers, Team and Phases.
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

    private string _numCtxText;
    private string _globalInstructions;
    private bool _disableThinking;
    private bool _verifyWrites;
    private bool _allowImplicitToolCalls;
    private bool _reviewContent;
    private bool _checkSoundness;
    private string _reviewRetriesText = "1";
    private bool _revertRejectedSteps;
    private string _maxParallelStepsText;
    private string _evidenceBudgetText;
    private string _logRetentionDaysText;
    private bool _logPromptBodies;
    private int _shellCommandsIndex;
    private bool _closeToTray;
    private bool _runAtStartup;
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
            OnPropertyChanged(nameof(IsRemote));
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
    public SettingsViewModel(
        AppSettings settings, Action<AppSettings> onSaved,
        string? workspaceRoot = null, IReadOnlyList<string>? toolNames = null)
    {
        _working = settings.Clone();
        _onSaved = onSaved;
        ToolNames = toolNames ?? Array.Empty<string>();
        InitializeMcp();
        InitializeTemplates(workspaceRoot);
        InitializeRemote();

        _numCtxText = _working.NumCtx?.ToString() ?? string.Empty;
        _globalInstructions = _working.GlobalInstructions;
        _disableThinking = _working.DisableThinking;
        _verifyWrites = _working.VerifyWrites;
        _allowImplicitToolCalls = _working.AllowImplicitToolCalls;
        _reviewContent = _working.ReviewContent;
        _checkSoundness = _working.CheckSoundness;
        _reviewRetriesText = _working.ReviewRetries.ToString();
        _revertRejectedSteps = _working.RevertRejectedSteps;
        _maxParallelStepsText = _working.MaxParallelSteps.ToString();
        _evidenceBudgetText = _working.EvidenceBudget.ToString();
        _logRetentionDaysText = _working.LogRetentionDays.ToString();
        _logPromptBodies = _working.LogPromptBodies;
        _shellCommandsIndex = (int)_working.ShellCommands;
        _closeToTray = _working.CloseToTray;

        // Read from the system, not from settings.json: the Run key is the truth, and a copy would
        // drift the first time the user removed it in Task Manager.
        _runAtStartup = StartupEntry.IsEnabled();
        _startupNote = StartupEntry.Supported
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

        foreach (var component in BuildInfo.Components())
            Components.Add(component);

        _plan = ToSelection(_working.Bindings.Plan);
        _review = ToSelection(_working.Bindings.Review);
        _executeLight = ToSelection(_working.Bindings.ExecuteLight);
        _executeHeavy = ToSelection(_working.Bindings.ExecuteHeavy);
        SyncCatalog();

        AddProviderCommand = new RelayCommand(AddProvider);

        AddWorkerCommand = new RelayCommand(AddWorker);

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public event Action? CloseRequested;

    /// <summary>Asks the view to open the provider editor: (config, what to do once it saved).</summary>
    public event Action<ProviderConfig, Action>? ProviderEditRequested;

    /// <summary>Asks a yes/no question. The window answers it, because the dialog needs a parent.</summary>
    public event Func<string, string, Task<bool>>? ConfirmRequested;

    /// <summary>Asks the view to open the worker editor: (config, model catalog, what to do once it saved).</summary>
    public event Action<WorkerConfig, IReadOnlyList<string>, Action>? WorkerEditRequested;

    // ── General ───────────────────────────────────────────────────────────────
    public string NumCtxText { get => _numCtxText; set => Set(ref _numCtxText, value); }
    public string GlobalInstructions { get => _globalInstructions; set => Set(ref _globalInstructions, value); }
    public bool DisableThinking { get => _disableThinking; set => Set(ref _disableThinking, value); }
    public bool VerifyWrites { get => _verifyWrites; set => Set(ref _verifyWrites, value); }
    public bool AllowImplicitToolCalls { get => _allowImplicitToolCalls; set => Set(ref _allowImplicitToolCalls, value); }
    public bool ReviewContent { get => _reviewContent; set => Set(ref _reviewContent, value); }
    public bool CheckSoundness { get => _checkSoundness; set => Set(ref _checkSoundness, value); }
    public string ReviewRetriesText { get => _reviewRetriesText; set => Set(ref _reviewRetriesText, value); }
    public bool RevertRejectedSteps { get => _revertRejectedSteps; set => Set(ref _revertRejectedSteps, value); }
    public string MaxParallelStepsText { get => _maxParallelStepsText; set => Set(ref _maxParallelStepsText, value); }
    public string EvidenceBudgetText { get => _evidenceBudgetText; set => Set(ref _evidenceBudgetText, value); }
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
    /// Whether the computer starts Enactive at login. Applied on Save with the rest, and re-read
    /// from the system afterwards - if the registry refused, the box goes back to the truth rather
    /// than claiming something that did not happen.
    /// </summary>
    public bool RunAtStartup { get => _runAtStartup; set => Set(ref _runAtStartup, value); }

    public bool StartupSupported => StartupEntry.Supported;

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
    public string Review { get => _review; set => Set(ref _review, value); }
    public string ExecuteLight { get => _executeLight; set => Set(ref _executeLight, value); }
    public string ExecuteHeavy { get => _executeHeavy; set => Set(ref _executeHeavy, value); }

    public RelayCommand AddProviderCommand { get; }
    public RelayCommand AddWorkerCommand { get; }
    public RelayCommand SaveCommand { get; }
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

    private void Save()
    {
        _working.NumCtx = int.TryParse(NumCtxText.Trim(), out var n) ? n : null;
        _working.GlobalInstructions = GlobalInstructions;
        _working.DisableThinking = DisableThinking;
        _working.VerifyWrites = VerifyWrites;
        _working.AllowImplicitToolCalls = AllowImplicitToolCalls;
        _working.ReviewContent = ReviewContent;
        _working.CheckSoundness = CheckSoundness;
        // Clamped here as well as in the orchestrator: what is saved should be what will be used, or
        // the settings window shows one number while the engine quietly runs another.
        _working.ReviewRetries = int.TryParse(ReviewRetriesText.Trim(), out var r) ? Math.Clamp(r, 0, 5) : 1;
        _working.RevertRejectedSteps = RevertRejectedSteps;
        _working.MaxParallelSteps = int.TryParse(MaxParallelStepsText.Trim(), out var p) && p > 0 ? p : 1;
        // Anything unparseable or below the floor falls back to the default rather than to the
        // number typed: an evidence block too small to hold its header is not a smaller setting,
        // it is a reviewer shown nothing.
        _working.EvidenceBudget = int.TryParse(EvidenceBudgetText.Trim(), out var e)
                                  && e >= ExecutionJournal.MinimumBudget
            ? e
            : ExecutionJournal.DefaultBudget;
        // A number that does not parse means the default, not zero: zero here is "keep everything",
        // a deliberate choice, and reaching it by typing nonsense would be the opposite of one.
        _working.LogRetentionDays = int.TryParse(LogRetentionDaysText.Trim(), out var d) && d >= 0
            ? d
            : FileLogSink.DefaultRetentionDays;
        _working.LogPromptBodies = LogPromptBodies;
        _working.ShellCommands = Enum.IsDefined((ShellCommandPolicy)ShellCommandsIndex)
            ? (ShellCommandPolicy)ShellCommandsIndex
            : ShellCommandPolicy.Follow;
        _working.CloseToTray = CloseToTray;

        var startupRefused =
            StartupEntry.Supported
            && RunAtStartup != StartupEntry.IsEnabled()
            && !StartupEntry.Set(RunAtStartup);

        if (startupRefused)
        {
            // Show what actually happened rather than a tick that means nothing.
            RunAtStartup = StartupEntry.IsEnabled();
            StartupNote = "Windows would not let that be changed. Start-up is left as it was.";
        }

        SaveRemote();

        _working.Bindings.Plan = FromSelection(Plan);
        _working.Bindings.Review = FromSelection(Review);
        _working.Bindings.ExecuteLight = FromSelection(ExecuteLight);
        _working.Bindings.ExecuteHeavy = FromSelection(ExecuteHeavy);

        // Validate BEFORE handing this over to be written. A configuration that cannot be built —
        // two providers with the same id, say — used to be saved anyway, and then took the app down
        // on every launch afterwards, because startup reads the same file and fails the same way.
        var problems = _working.Validate();
        if (problems.Count > 0)
        {
            StartupNote = "Not saved — " + string.Join(" ", problems);
            return;
        }

        try { _onSaved(_working); }
        catch (Exception ex) { StartupNote = "Not saved — " + ex.Message; return; }

        // Everything else is saved either way. The window stays open only when there is something
        // the user has not seen yet - a note nobody reads because the window closed on top of it is
        // the same as no note at all.
        if (startupRefused)
            return;

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
    }

    private static string ToSelection(string bound) => string.IsNullOrWhiteSpace(bound) ? NoneLabel : bound;

    private static string FromSelection(string selection)
        => string.IsNullOrWhiteSpace(selection) || selection == NoneLabel ? string.Empty : selection;
}
