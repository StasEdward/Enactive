namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Media;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.Providers;

/// <summary>One row of the Providers list. Wraps the config so the row can be told to re-read it
/// after the edit dialog writes back, instead of rebuilding the whole list.</summary>
internal sealed class ProviderRow : ObservableObject
{
    public ProviderRow(ProviderConfig config) => Config = config;

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
    public WorkerRow(WorkerConfig config) => Config = config;

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
internal sealed class SettingsViewModel : ObservableObject
{
    public const string NoneLabel = "(none)";

    private readonly AppSettings _working;
    private readonly Action<AppSettings> _onSaved;

    private string _numCtxText;
    private string _globalInstructions;
    private bool _disableThinking;
    private bool _verifyWrites;
    private string _maxParallelStepsText;
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

    public SettingsViewModel(AppSettings settings, Action<AppSettings> onSaved)
    {
        _working = settings.Clone();
        _onSaved = onSaved;

        _numCtxText = _working.NumCtx?.ToString() ?? string.Empty;
        _globalInstructions = _working.GlobalInstructions;
        _disableThinking = _working.DisableThinking;
        _verifyWrites = _working.VerifyWrites;
        _maxParallelStepsText = _working.MaxParallelSteps.ToString();
        _closeToTray = _working.CloseToTray;

        // Read from the system, not from settings.json: the Run key is the truth, and a copy would
        // drift the first time the user removed it in Task Manager.
        _runAtStartup = StartupEntry.IsEnabled();
        _startupNote = StartupEntry.Supported
            ? string.Empty
            : "Only Windows starts programs this way; this desktop does not.";

        foreach (var p in _working.Providers)
            Providers.Add(new ProviderRow(p));
        foreach (var w in _working.Workers)
            Workers.Add(new WorkerRow(w));

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
        EditProviderCommand = new RelayCommand(EditProvider, () => SelectedProvider is not null);
        RemoveProviderCommand = new RelayCommand(RemoveProvider, () => SelectedProvider is not null);

        AddWorkerCommand = new RelayCommand(AddWorker);
        EditWorkerCommand = new RelayCommand(EditWorker, () => SelectedWorker is not null);
        RemoveWorkerCommand = new RelayCommand(RemoveWorker, () => SelectedWorker is not null);

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public event Action? CloseRequested;

    /// <summary>Asks the view to open the provider editor: (config, what to do once it saved).</summary>
    public event Action<ProviderConfig, Action>? ProviderEditRequested;

    /// <summary>Asks the view to open the worker editor: (config, model catalog, what to do once it saved).</summary>
    public event Action<WorkerConfig, IReadOnlyList<string>, Action>? WorkerEditRequested;

    // ── General ───────────────────────────────────────────────────────────────
    public string NumCtxText { get => _numCtxText; set => Set(ref _numCtxText, value); }
    public string GlobalInstructions { get => _globalInstructions; set => Set(ref _globalInstructions, value); }
    public bool DisableThinking { get => _disableThinking; set => Set(ref _disableThinking, value); }
    public bool VerifyWrites { get => _verifyWrites; set => Set(ref _verifyWrites, value); }
    public string MaxParallelStepsText { get => _maxParallelStepsText; set => Set(ref _maxParallelStepsText, value); }

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

    public ProviderRow? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!Set(ref _selectedProvider, value))
                return;
            EditProviderCommand.RaiseCanExecuteChanged();
            RemoveProviderCommand.RaiseCanExecuteChanged();
        }
    }

    public WorkerRow? SelectedWorker
    {
        get => _selectedWorker;
        set
        {
            if (!Set(ref _selectedWorker, value))
                return;
            EditWorkerCommand.RaiseCanExecuteChanged();
            RemoveWorkerCommand.RaiseCanExecuteChanged();
        }
    }

    // ── Phases ────────────────────────────────────────────────────────────────
    /// <summary>Every model of every provider, plus "(none)". Live: edits on other tabs land here.</summary>
    public ObservableCollection<string> ModelCatalog { get; } = new();

    public string Plan { get => _plan; set => Set(ref _plan, value); }
    public string Review { get => _review; set => Set(ref _review, value); }
    public string ExecuteLight { get => _executeLight; set => Set(ref _executeLight, value); }
    public string ExecuteHeavy { get => _executeHeavy; set => Set(ref _executeHeavy, value); }

    public RelayCommand AddProviderCommand { get; }
    public RelayCommand EditProviderCommand { get; }
    public RelayCommand RemoveProviderCommand { get; }
    public RelayCommand AddWorkerCommand { get; }
    public RelayCommand EditWorkerCommand { get; }
    public RelayCommand RemoveWorkerCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    private void AddProvider()
    {
        var config = new ProviderConfig { Kind = ProviderKind.OpenAiCompatible };
        ProviderEditRequested?.Invoke(config, () =>
        {
            _working.Providers.Add(config);
            Providers.Add(new ProviderRow(config));
            SyncCatalog();
        });
    }

    private void EditProvider()
    {
        if (SelectedProvider is not { } row)
            return;
        ProviderEditRequested?.Invoke(row.Config, () =>
        {
            row.Refresh();
            SyncCatalog();
        });
    }

    private void RemoveProvider()
    {
        if (SelectedProvider is not { } row)
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
            Workers.Add(new WorkerRow(config));
        });
    }

    private void EditWorker()
    {
        if (SelectedWorker is not { } row)
            return;
        WorkerEditRequested?.Invoke(row.Config, _working.ModelCatalog(), row.Refresh);
    }

    private void RemoveWorker()
    {
        if (SelectedWorker is not { } row)
            return;
        _working.Workers.Remove(row.Config);
        Workers.Remove(row);
        SelectedWorker = null;
    }

    private void Save()
    {
        _working.NumCtx = int.TryParse(NumCtxText.Trim(), out var n) ? n : null;
        _working.GlobalInstructions = GlobalInstructions;
        _working.DisableThinking = DisableThinking;
        _working.VerifyWrites = VerifyWrites;
        _working.MaxParallelSteps = int.TryParse(MaxParallelStepsText.Trim(), out var p) && p > 0 ? p : 1;
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

        _working.Bindings.Plan = FromSelection(Plan);
        _working.Bindings.Review = FromSelection(Review);
        _working.Bindings.ExecuteLight = FromSelection(ExecuteLight);
        _working.Bindings.ExecuteHeavy = FromSelection(ExecuteHeavy);

        _onSaved(_working);

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
