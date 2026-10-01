namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Media;
using Enactive.Agents;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Enactive.Core.Schedules;
using Enactive.Core.Templates;
using Enactive.Settings;
using Enactive.Workspace;

/// <summary>One schedule in the list.</summary>
internal sealed class ScheduleListItem
{
    public ScheduleListItem(Schedule schedule, DateTimeOffset now)
    {
        Schedule = schedule;
        When = ScheduleWords.When(schedule.Timing);
        Next = ScheduleWords.NextRun(schedule, now);

        // Amber for a schedule that is off, red for one that cannot be timed at all, green for one
        // with a next run. The colour says the same thing the second line does, for the glance.
        EdgeBrush = !schedule.Enabled ? Brand.TextMuted
                  : ScheduleClock.Next(schedule, now) is null ? Brand.Danger
                  : Brand.Success;
    }

    public Schedule Schedule { get; }
    public string Name => Schedule.Name;
    public string When { get; }
    public string Next { get; }
    public IBrush EdgeBrush { get; }
}

/// <summary>One of this schedule's past outcomes, as the Inbox recorded it.</summary>
internal sealed class ScheduleOutcomeLine
{
    public ScheduleOutcomeLine(InboxItem item)
    {
        When = item.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        Summary = item.Summary;
        Brush = item.Kind.ToLowerInvariant() switch
        {
            "result" => Brand.Success,
            "decision" => Brand.Amber,
            "error" => Brand.Danger,
            _ => Brand.TextMuted
        };
    }

    public string When { get; }
    public string Summary { get; }
    public IBrush Brush { get; }
}

/// <summary>Something a schedule can be set to run: a template, or a past run to repeat.</summary>
internal sealed class WorkChoice
{
    private WorkChoice(string label, string? templateId, string? snapshot)
    {
        Label = label;
        TemplateId = templateId;
        Snapshot = snapshot;
    }

    public string Label { get; }
    public string? TemplateId { get; }
    public string? Snapshot { get; }

    public static WorkChoice Template(TaskTemplate template)
        => new($"Template · {template.Name}", template.Id, null);

    /// <summary>A finished run, repeated exactly as it ran. See ScheduledWork.FromPastRun.</summary>
    public static WorkChoice PastRun(RunSummary run)
        => new($"Past run · {run.Title} ({run.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm})", null, run.Spec);

    public ScheduledWork ToWork(IReadOnlyDictionary<string, string>? parameters = null)
        => TemplateId is { Length: > 0 }
            ? ScheduledWork.FromTemplate(TemplateId, parameters)
            : ScheduledWork.FromPastRun(Snapshot ?? "");
}

/// <summary>One entry in the time-zone list.</summary>
internal sealed record ZoneChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Managing schedules where the person is: the list, what each one is
/// allowed to do in words, its last few outcomes, and the form for adding or editing one.
///
/// <para><b>It decides nothing.</b> What a schedule says about itself is
/// <see cref="ScheduleWords"/>, and whether a draft can be saved is <see cref="ScheduleDrafts"/> —
/// both in Core, both tested. This project is a WinExe no test project references, so a rule that
/// ended up here would be a rule nothing checks; and the check that matters most is that the form
/// and the RUNNER agree about what can run, which is only true if they call the same code.</para>
///
/// <para>The form cannot hold an invalid time: hours and minutes are spinners rather than a text
/// box, so there is no "that is not a time" to report and no parsing rule living out here.</para>
/// </summary>
internal sealed class SchedulesViewModel : ObservableObject
{
    private readonly ScheduleStore _store;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// What the person configured, for the one thing this form decides on their behalf: the policy
    /// a scheduled run will hold. The tier comes from the picker; the shell rule comes from here,
    /// and it has to, because a schedule saved with the tier alone would run commands on a machine
    /// whose settings say never to.
    /// </summary>
    private readonly AppSettings _settings;

    private WorkspaceInfo _workspace;
    private TemplateStore _templates;
    private IRunStore _runs;
    private IInboxStore _inbox;

    private ScheduleListItem? _selected;
    private bool _isEditing;
    private string _problems = string.Empty;
    private string _status = string.Empty;
    private string _tickWarning = string.Empty;

    /// <summary>
    /// How often the tick is expected. Only used to decide when silence has gone on long enough to
    /// be worth reporting - see Heartbeat.Check.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    // ── the draft ───────────────────────────────────────────────────────────
    private Guid? _draftId;
    private string _draftName = string.Empty;
    private WorkChoice? _draftWork;
    private string _draftRepeat = "Daily";
    private string _draftDay = nameof(DayOfWeek.Monday);
    // Decimal because that is what NumericUpDown binds: an int here compiles and then throws when
    // the control writes back, which is a failure that only a person opening the form would find.
    private decimal _draftHour = 3;
    private decimal _draftMinute = 0;
    // Nullable because DatePicker.SelectedDate is, and a control writing null into a value type
    // throws at the moment somebody uses the form - the one place nothing here can test.
    private DateTimeOffset? _draftDate = DateTimeOffset.Now.Date.AddDays(1);
    private ZoneChoice _draftZone;
    private bool _draftRunLate;
    private string _draftTier = "execute";

    public SchedulesViewModel(
        string workspaceRoot,
        AppSettings settings,
        ScheduleStore? store = null,
        Func<DateTimeOffset>? now = null)
    {
        _store = store ?? ScheduleStore.Default;
        _now = now ?? (() => DateTimeOffset.Now);
        _settings = settings;

        _workspace = WorkspaceInfo.For(workspaceRoot);
        _templates = new TemplateStore(workspaceRoot);
        _runs = RunStoreFactory.Create(_workspace);
        _inbox = InboxStoreFactory.Create(_workspace);

        Zones = new ObservableCollection<ZoneChoice>(
            TimeZoneInfo.GetSystemTimeZones()
                .Select(z => new ZoneChoice(z.Id, z.DisplayName))
                .OrderBy(z => z.Label, StringComparer.CurrentCulture));

        _draftZone = Zones.FirstOrDefault(z => z.Id == TimeZoneInfo.Local.Id) ?? Zones[0];

        NewCommand = new RelayCommand(BeginNew);
        EditCommand = new RelayCommand(BeginEdit, () => _selected is not null);
        SaveCommand = new RelayCommand(Save);
        CancelEditCommand = new RelayCommand(() => IsEditing = false);
        ToggleEnabledCommand = new RelayCommand(ToggleEnabled, () => _selected is not null);
        DeleteCommand = new RelayCommand(Delete, () => _selected is not null);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public ObservableCollection<ScheduleListItem> Schedules { get; } = new();
    public ObservableCollection<string> Approval { get; } = new();
    public ObservableCollection<ScheduleOutcomeLine> Outcomes { get; } = new();

    /// <summary>
    /// Whether this schedule has ever reported back. A property rather than a negated count in the
    /// view: the count is an int, and what a binding does with one where a bool is wanted is a
    /// question only answered when somebody opens the window.
    /// </summary>
    public bool HasNoOutcomes => Outcomes.Count == 0;
    public ObservableCollection<WorkChoice> WorkChoices { get; } = new();
    public ObservableCollection<ZoneChoice> Zones { get; }

    public IReadOnlyList<string> Repeats { get; } = new[] { "Once", "Daily", "Weekly" };
    public IReadOnlyList<string> Days { get; } = Enum.GetNames<DayOfWeek>();
    public IReadOnlyList<string> Tiers { get; } = AutonomyTiers.Names.ToArray();

    public string WorkspaceRoot => _workspace.RootPath;

    public RelayCommand NewCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelEditCommand { get; }
    public RelayCommand ToggleEnabledCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public ScheduleListItem? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(EnableLabel));
            EditCommand.RaiseCanExecuteChanged();
            ToggleEnabledCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            ShowSelected();
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>The button says what pressing it will do, not what is currently true.</summary>
    public string EnableLabel => _selected?.Schedule.Enabled == false ? "Enable" : "Disable";

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (Set(ref _isEditing, value))
                OnPropertyChanged(nameof(IsNotEditing));
        }
    }

    public bool IsNotEditing => !_isEditing;

    /// <summary>Every reason it cannot be saved, at once. See ScheduleDrafts.</summary>
    public string Problems
    {
        get => _problems;
        private set
        {
            if (Set(ref _problems, value))
                OnPropertyChanged(nameof(HasProblems));
        }
    }

    public bool HasProblems => _problems.Length > 0;

    /// <summary>
    /// Said before anything else, because it makes everything else beside it untrue.
    ///
    /// <para>On 2026-09-10 two schedules were created here, saved correctly, and did not run: no
    /// tick had ever been registered on the machine, so nothing had ever asked them. This window
    /// showed "Next run: ..." throughout. A list of schedules with confident next-run times, on a
    /// machine where none of them can fire, is the same failure as a permission gate that gates
    /// nothing.</para>
    /// </summary>
    public string TickWarning
    {
        get => _tickWarning;
        private set
        {
            if (Set(ref _tickWarning, value))
                OnPropertyChanged(nameof(HasTickWarning));
        }
    }

    public bool HasTickWarning => _tickWarning.Length > 0;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string DraftName { get => _draftName; set => Set(ref _draftName, value); }
    public WorkChoice? DraftWork { get => _draftWork; set => Set(ref _draftWork, value); }
    public decimal DraftHour { get => _draftHour; set => Set(ref _draftHour, value); }
    public decimal DraftMinute { get => _draftMinute; set => Set(ref _draftMinute, value); }

    private int Hour => (int)Math.Clamp(_draftHour, 0, 23);
    private int Minute => (int)Math.Clamp(_draftMinute, 0, 59);
    public DateTimeOffset? DraftDate { get => _draftDate; set => Set(ref _draftDate, value); }

    /// <summary>The chosen day, or tomorrow when the picker has been emptied.</summary>
    private DateTime ChosenDate => (_draftDate ?? _now().AddDays(1)).Date;
    public ZoneChoice DraftZone { get => _draftZone; set => Set(ref _draftZone, value); }
    public bool DraftRunLate { get => _draftRunLate; set => Set(ref _draftRunLate, value); }
    public string DraftTier { get => _draftTier; set => Set(ref _draftTier, value); }
    public string DraftDay { get => _draftDay; set => Set(ref _draftDay, value); }

    public string DraftRepeat
    {
        get => _draftRepeat;
        set
        {
            if (!Set(ref _draftRepeat, value))
                return;

            OnPropertyChanged(nameof(IsWeekly));
            OnPropertyChanged(nameof(IsOnce));
        }
    }

    public bool IsWeekly => string.Equals(_draftRepeat, "Weekly", StringComparison.OrdinalIgnoreCase);
    public bool IsOnce => string.Equals(_draftRepeat, "Once", StringComparison.OrdinalIgnoreCase);

    // ── loading ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Points an already-open window at another workspace. Schedules are per-workspace, and a list
    /// left showing the previous project's while the buttons act on this one is the disagreement
    /// this codebase keeps coming back to.
    /// </summary>
    public async Task SetWorkspaceAsync(string workspaceRoot)
    {
        _workspace = WorkspaceInfo.For(workspaceRoot);
        _templates = new TemplateStore(workspaceRoot);
        _runs = RunStoreFactory.Create(_workspace);
        _inbox = InboxStoreFactory.Create(_workspace);

        IsEditing = false;
        OnPropertyChanged(nameof(WorkspaceRoot));
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var wanted = _selected?.Schedule.Id;

        LoadList();
        await LoadWorkChoicesAsync();

        Selected = Schedules.FirstOrDefault(s => s.Schedule.Id == wanted) ?? Schedules.FirstOrDefault();
        await ShowOutcomesAsync();
    }

    private void LoadList()
    {
        var now = _now();

        // Before the list, because it is about whether the list means anything.
        TickWarning = Heartbeat.Check(Heartbeat.LastSeen(), now, TickInterval).Words;

        Schedules.Clear();
        foreach (var schedule in _store.For(_workspace.RootPath))
            Schedules.Add(new ScheduleListItem(schedule, now));

        Status = Schedules.Count == 0
            ? "No schedules in this workspace yet."
            : $"{Schedules.Count} schedule(s).";
    }

    /// <summary>
    /// What a schedule can be set to run: the template library, and the past runs that were started
    /// from a specification. A run that was TYPED has no specification to repeat, so it is not
    /// offered - "run that again" has to mean something exact.
    /// </summary>
    private async Task LoadWorkChoicesAsync()
    {
        var kept = DraftWork;
        WorkChoices.Clear();

        foreach (var template in _templates.Load())
            WorkChoices.Add(WorkChoice.Template(template));

        try
        {
            var runs = await _runs.LoadSummariesAsync(CancellationToken.None);
            foreach (var run in runs.Where(r => r.Spec is { Length: > 0 }).Take(30))
                WorkChoices.Add(WorkChoice.PastRun(run));
        }
        catch { /* the library alone is still a usable form */ }

        DraftWork = WorkChoices.FirstOrDefault(c => c.Label == kept?.Label) ?? WorkChoices.FirstOrDefault();
    }

    private void ShowSelected()
    {
        Approval.Clear();
        if (_selected is null)
            return;

        // Verbatim from Core. The window's job is to show these, not to choose which of them fit.
        foreach (var line in ScheduleWords.Approval(_selected.Schedule, _now()))
            Approval.Add(line);

        _ = ShowOutcomesAsync();
    }

    private async Task ShowOutcomesAsync()
    {
        Outcomes.Clear();
        if (_selected is null)
        {
            OnPropertyChanged(nameof(HasNoOutcomes));
            return;
        }

        try
        {
            var items = await _inbox.LoadAllAsync(CancellationToken.None);
            foreach (var item in ScheduledOutcome.Of(items, _selected.Schedule.Id))
                Outcomes.Add(new ScheduleOutcomeLine(item));
        }
        catch { /* the inbox is never load-bearing */ }
        finally { OnPropertyChanged(nameof(HasNoOutcomes)); }
    }

    // ── the form ────────────────────────────────────────────────────────────

    private void BeginNew()
    {
        _draftId = null;
        DraftName = string.Empty;
        DraftRepeat = "Daily";
        DraftHour = 3;
        DraftMinute = 0;
        DraftDate = _now().Date.AddDays(1);
        DraftZone = Zones.FirstOrDefault(z => z.Id == TimeZoneInfo.Local.Id) ?? Zones[0];
        DraftRunLate = false;
        DraftTier = "execute";
        Problems = string.Empty;
        IsEditing = true;
    }

    private void BeginEdit()
    {
        if (_selected is null)
            return;

        var schedule = _selected.Schedule;

        // The ID travels with the draft. A rename that minted a new id would leave this schedule's
        // outcomes behind while the list showed what looks like the same schedule.
        _draftId = schedule.Id;
        DraftName = schedule.Name;
        DraftRepeat = schedule.Timing.Repeat.ToString();
        DraftDay = (schedule.Timing.OnDay ?? DayOfWeek.Monday).ToString();
        DraftHour = schedule.Timing.AtLocal.Hour;
        DraftMinute = schedule.Timing.AtLocal.Minute;
        DraftDate = schedule.Timing.OnceAt?.ToLocalTime() ?? _now().Date.AddDays(1);
        DraftZone = Zones.FirstOrDefault(z => z.Id == schedule.Timing.TimeZoneId)
                    ?? new ZoneChoice(schedule.Timing.TimeZoneId, schedule.Timing.TimeZoneId);
        DraftRunLate = schedule.Missed == MissedRun.RunLate;
        DraftTier = TierNameFor(schedule.Permissions);

        DraftWork = WorkChoices.FirstOrDefault(c =>
            (c.TemplateId is { } id && id == schedule.Work.TemplateId)
            || (c.Snapshot is { } snap && snap == schedule.Work.SpecSnapshot))
            ?? WorkChoices.FirstOrDefault();

        Problems = string.Empty;
        IsEditing = true;
    }

    private void Save()
    {
        if (DraftWork is null)
        {
            Problems = "There is nothing in this workspace to schedule yet — add a template first.";
            return;
        }

        var tier = AutonomyTiers.Parse(DraftTier) ?? 2;
        var draft = new ScheduleDraft(
            DraftName, _workspace.RootPath,
            DraftWork.ToWork(),
            BuildTiming(),
            EngineComposition.PolicyFor(_settings, tier),
            DraftRunLate ? MissedRun.RunLate : MissedRun.Skip,
            Enabled: true,
            Id: _draftId);

        // The runner's own resolution, through Core. A form with its own idea of valid accepts
        // schedules the runner refuses at three in the morning.
        var checkedDraft = ScheduleDrafts.Check(draft, _workspace, _now(), id => _templates.Find(id));

        if (checkedDraft.Schedule is not { } schedule)
        {
            Problems = string.Join(Environment.NewLine, checkedDraft.Problems);
            return;
        }

        _store.Save(schedule);
        Problems = string.Empty;
        IsEditing = false;
        _ = ReloadAndSelectAsync(schedule.Id);
    }

    private ScheduleTiming BuildTiming()
    {
        var at = new TimeOnly(Hour, Minute);

        if (IsOnce)
        {
            // The date from the picker, the time from the spinners, read in the chosen zone - not in
            // this machine's. "Nine o'clock" means nine o'clock where the schedule says it lives.
            var wall = ChosenDate.AddHours(Hour).AddMinutes(Minute);
            var zone = ScheduleClock.Zone(DraftZone.Id) ?? TimeZoneInfo.Local;
            var offset = zone.IsInvalidTime(wall) ? zone.BaseUtcOffset : zone.GetUtcOffset(wall);

            return new ScheduleTiming(
                ScheduleRepeat.Once, at, DraftZone.Id, null, new DateTimeOffset(wall, offset));
        }

        return IsWeekly
            ? ScheduleTiming.Weekly(Enum.Parse<DayOfWeek>(DraftDay), at, DraftZone.Id)
            : ScheduleTiming.Daily(at, DraftZone.Id);
    }

    private async Task ReloadAndSelectAsync(Guid id)
    {
        LoadList();
        Selected = Schedules.FirstOrDefault(s => s.Schedule.Id == id) ?? Schedules.FirstOrDefault();
        await ShowOutcomesAsync();
    }

    // ── the two buttons that change a saved schedule ────────────────────────

    private void ToggleEnabled()
    {
        if (_selected is null)
            return;

        var schedule = _selected.Schedule;
        _store.Save(schedule with { Enabled = !schedule.Enabled });
        _ = ReloadAndSelectAsync(schedule.Id);
    }

    private void Delete()
    {
        if (_selected is null)
            return;

        _store.Remove(_workspace.RootPath, _selected.Schedule.Id);
        _ = RefreshAsync();
    }

    /// <summary>
    /// Which slider position a saved policy came from. By its LEVEL, not by comparing the whole
    /// policy: a schedule saved by an older build, or edited by hand, still opens on the nearest
    /// tier rather than silently on the default.
    /// </summary>
    private static string TierNameFor(Enactive.Core.Permissions.PermissionPolicy policy)
        => policy.Level switch
        {
            Enactive.Core.Permissions.PermissionLevel.Observe => "observe",
            Enactive.Core.Permissions.PermissionLevel.Suggest => "suggest",
            Enactive.Core.Permissions.PermissionLevel.Autonomous => "autonomous",
            _ => "execute"
        };
}
