namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Enactive.Core.Artifacts;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Templates;

/// <summary>
/// One finished run, as a CARD in the context column: a coloured edge for how it ended, the title
/// the planner gave it, and one line saying when and what came of it. The edge is part of the card,
/// not part of being selected - you have to be able to spot the failed run without clicking it.
///
/// <para>Built from a <see cref="RunSummary"/>, not a record. Everything on a card - the title, the
/// status, when it started, how long it took, what it produced - is in the header; the transcript
/// is not, and a list of two hundred rows used to read two hundred transcripts to draw them.</para>
/// </summary>
internal sealed class RunListItemViewModel
{
    public RunListItemViewModel(
        RunSummary record, Action<RunListItemViewModel>? remove = null,
        int attempts = 1, bool isLead = true, bool expanded = false,
        Action<RunListItemViewModel>? toggle = null)
    {
        Record = record;
        RemoveCommand = new RelayCommand(() => remove?.Invoke(this), () => remove is not null);

        Attempts = attempts;
        IsLead = isLead;
        IsExpanded = expanded;

        // Only a group of more than one gets an expander. "1 attempt" on every ordinary run is
        // noise on the common case to serve the rare one.
        HasAttempts = isLead && attempts > 1;
        AttemptsLabel = HasAttempts ? attempts.ToString() : string.Empty;
        Chevron = expanded ? "\u25be" : "\u25b8";
        ToggleCommand = new RelayCommand(() => toggle?.Invoke(this), () => HasAttempts);

        // Indented, so an older attempt reads as belonging to the row above rather than as its own
        // piece of work - which is the whole thing being fixed.
        //
        // The WHOLE margin, not the indent alone. Binding Margin on the card replaces the value
        // Border.card sets in the style, gap included - so an indent expressed as a left inset and
        // nothing else silently took the 6px between every run row away with it, and the list of
        // cards became one block with lines drawn on it. Anything bound here has to carry the gap.
        CardMargin = isLead ? new Thickness(0, 0, 0, 6) : new Thickness(14, 0, 0, 6);

        // Repaired on the way OUT, not only on the way in: history recorded before titles were
        // one line still has to read properly, and rewriting somebody's stored runs in place to fix
        // a display problem is the wrong trade.
        Title = RunTitle.For(record);
        Meta = $"{Ago(record.StartedAt)} · {Outcome(record)}";
        StatusBrush = BrushFor(record.Status);

        var elapsed = record.FinishedAt - record.StartedAt;
        Tooltip = $"{Title}\n{record.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {record.Status} · {Duration(elapsed)}";
    }

    public RunSummary Record { get; }
    public string Title { get; }

    /// <summary>How many attempts this task has. On the lead row only; see RunHousekeeping.Rows.</summary>
    public int Attempts { get; }

    public bool IsLead { get; }
    public bool IsExpanded { get; }
    public bool HasAttempts { get; }
    public string AttemptsLabel { get; }
    public string Chevron { get; }
    /// <summary>The card's margin: the indent of an older attempt, and the gap under every row.</summary>
    public Thickness CardMargin { get; }
    public RelayCommand ToggleCommand { get; }

    /// <summary>Forgets this run. The window asks first; the row only reports the click.</summary>
    public RelayCommand RemoveCommand { get; }
    public string Meta { get; }
    public string Tooltip { get; }
    public IBrush StatusBrush { get; }

    // Which kind of ending a stored status is, is decided once, in Core (RunStanding), where it is tested against
    // every outcome the engine records - this used to be a second copy of that classification. Only the colours
    // are here.
    internal static IBrush BrushFor(string status) => RunStanding.Of(status) switch
    {
        RunStandingKind.Done => Brand.Success,
        RunStandingKind.Failed => Brand.Danger,
        RunStandingKind.Idle => Brand.TextMuted,
        // Open - blocked, waiting for an answer, incomplete - and, in the history, any word that is not an end:
        // a run that never wrote a final status. A person's problem to look at, which is what amber means here.
        _ => Brand.Amber
    };

    /// <summary>
    /// How long ago, the way a person would say it. Days are counted on the CALENDAR, not in
    /// 24-hour blocks: a run at 23:50 was "yesterday" by breakfast, not "9 h ago".
    /// </summary>
    private static string Ago(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var days = (DateTime.Today - local.Date).Days;

        if (days == 1)
            return "yesterday";
        if (days > 1 && days < 7)
            return $"{days} days ago";
        if (days >= 7)
            return local.ToString("MMM d");

        var span = DateTimeOffset.Now - at;
        if (span.TotalMinutes < 1)
            return "just now";
        if (span.TotalMinutes < 60)
            return $"{(int)span.TotalMinutes} min ago";
        return $"{(int)span.TotalHours} h ago";
    }

    /// <summary>
    /// What came of it, in two or three words. A run that did not complete says how - failed,
    /// blocked, waiting, not finished (RunStanding, in Core, where the words are tested); otherwise
    /// what it produced is more use than how long it took, and the duration is in the run's own
    /// header anyway.
    /// </summary>
    private static string Outcome(RunSummary record)
    {
        if (RunStanding.Word(record.Status) is { } word)
            return word;

        if (record.Artifacts.Count > 0)
            return $"{record.Artifacts.Count} artifact{(record.Artifacts.Count == 1 ? "" : "s")}";

        return Duration(record.FinishedAt - record.StartedAt);
    }

    private static string Duration(TimeSpan span)
    {
        var seconds = span.TotalSeconds;
        if (seconds < 0)
            seconds = 0;
        return seconds >= 60 ? $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s" : $"{seconds:0}s";
    }
}

/// <summary>
/// A run that is happening right now, as a row at the TOP of the column.
///
/// <para>Above the history because it is not history, and above UNFINISHED because a run in flight
/// is more current than one that was abandoned. It exists so the column can answer "what is
/// happening here", which it could not: a run had no row anywhere until it ENDED, so starting one
/// and then opening an older run to compare left the new run findable nowhere.</para>
///
/// <para>Pressing it goes back to the live feed - the same thing the "← Back" in the past-run
/// header does, in the place people look for it. A headless run has no feed, so it is not
/// pressable and its row says where its answer will be instead.</para>
/// </summary>
internal sealed class LiveRunViewModel
{
    public LiveRunViewModel(LiveRun run, Action open)
    {
        Run = run;
        Title = RunTitle.OneLine(run.Title);
        Meta = RunColumn.Meta(run);
        CanOpen = !run.Headless;
        Tooltip = run.Headless
            ? "Running in the background. It reports to the Inbox when it is done; there is no live "
              + "view to open."
            : "Back to this run";
        OpenCommand = new RelayCommand(open, () => CanOpen);
    }

    public LiveRun Run { get; }
    public string Title { get; }
    public string Meta { get; }
    public string Tooltip { get; }
    public bool CanOpen { get; }
    public RelayCommand OpenCommand { get; }

    /// <summary>Ember: this is the thing that is happening, which is what ember means here.</summary>
    public IBrush StatusBrush => Brand.Accent;
}

/// <summary>
/// A run that never reached an end: the process it was in is gone, and what it had done is on disk
/// with a checkpoint beside it.
///
/// <para>Its own row above the history rather than a row IN it, because it is not a past run. A past
/// run is a record of something that happened; this is an offer to carry on. It also usually has no
/// record at all - a process that was killed never wrote one - so there is nothing in the history
/// for it to be a row of.</para>
/// </summary>
internal sealed class ResumableRunViewModel
{
    public ResumableRunViewModel(RunCheckpoint checkpoint, Action<RunCheckpoint> resume)
    {
        Checkpoint = checkpoint;
        Title = RunTitle.OneLine(checkpoint.Title);
        Meta = $"stopped {checkpoint.At.ToLocalTime():MMM d, HH:mm} · "
               + $"{checkpoint.Finished} of {checkpoint.Steps.Count} steps done";
        Tooltip = $"{checkpoint.Request}\n\nResume runs the {checkpoint.Remaining} step(s) that are left, "
                  + "under the permissions this run started with. A step that was in progress when it "
                  + "stopped is done again from its beginning.";
        ResumeCommand = new RelayCommand(() => resume(checkpoint));
    }

    public RunCheckpoint Checkpoint { get; }
    public string Title { get; }
    public string Meta { get; }
    public string Tooltip { get; }
    public RelayCommand ResumeCommand { get; }

    /// <summary>Amber, like everywhere else here: nobody knows how this ended.</summary>
    public IBrush StatusBrush => Brand.Amber;
}

/// <summary>
/// A finished run, opened read-only - in the SAME frame as a live one: a header with what it was
/// called and how it ended, tabs over the run, and the status column beside them. What changes is
/// the content, not the shape, because it is the same thing at a different time.
///
/// The first tab is the timeline rather than step cards. A stored event is (when, kind, summary):
/// the step number the live view sorts cards by is not recorded, and guessing it from the order
/// would put a tool call under the wrong step the moment two steps ran at once - which is a bug
/// this app has already had once.
///
/// There is nothing to click on purpose. This run finished, and history is not re-run by pressing
/// Apply on a diff from last Tuesday.
/// </summary>
internal sealed class PastRunViewModel : ObservableObject
{
    private int _selectedTab;

    /// <summary>Which attempt of its task this is, or empty when it is the only one.</summary>
    public string AttemptLabel { get; } = string.Empty;
    public bool HasAttempts { get; }

    /// <summary>The specification this run was started from, when it came from a template.</summary>
    public ResolvedTaskSpec? Spec { get; }

    /// <summary>What was originally asked for, when the run is new enough to have recorded it.</summary>
    public string? Request { get; }

    /// <summary>
    /// The request on one line, under the title. It used to BE the title - four paragraphs of a
    /// template's goal, which made every card in the list look the same. It is information worth
    /// keeping and worth keeping in its place.
    /// </summary>
    public string Subtitle { get; } = string.Empty;
    public bool HasSubtitle => Subtitle.Length > 0;

    public bool CanRetry { get; }
    public bool CanRunAgain { get; }
    public string RunAgainTooltip { get; } = string.Empty;

    public RelayCommand RetryCommand { get; }
    public RelayCommand RunAgainCommand { get; }

    /// <summary>Run this again, exactly as it was.</summary>
    public event Action<PastRunViewModel>? RetryRequested;

    /// <summary>Run it again from the template as it stands now.</summary>
    public event Action<PastRunViewModel>? RunAgainRequested;

    public PastRunViewModel(
        RunRecord record,
        Action<ArtifactItemViewModel> open,
        Action<ArtifactItemViewModel> remove,
        // Passed in rather than looked up here: only the window knows the provider list, and a past
        // run must be classified by the same rule as a live one.
        Func<string?, ModelWorkSplit.Reach>? reachOf = null,
        // Every attempt at the same task, newest first. Passed in because only the window has the
        // whole history; a run cannot know its siblings from inside itself. Headers, because all
        // this needs of a sibling is that it exists and when it started.
        IReadOnlyList<IRunHeader>? attempts = null,
        Func<TaskTemplate?>? currentTemplate = null)
    {
        Record = record;
        Title = RunTitle.For(record);

        var siblings = attempts is { Count: > 0 } ? attempts : new IRunHeader[] { record };
        var attemptNo = RunHistory.AttemptNumber(siblings, record.RunId);
        AttemptLabel = siblings.Count > 1
            ? $"attempt {attemptNo} of {siblings.Count}"
            : string.Empty;
        HasAttempts = AttemptLabel.Length > 0;

        Spec = ResolvedTaskSpec.Parse(record.Spec);
        Request = RunHistory.RequestOf(record);

        // Only when it says something the title does not: a typed request IS the title, and
        // repeating it underneath is noise.
        var asked = RunTitle.OneLine(Request, 160);
        Subtitle = asked.StartsWith(Title, StringComparison.Ordinal) ? string.Empty : asked;

        // Retry re-runs THIS specification, exactly as it was. For a run that was typed rather than
        // started from a template there is no specification, so it re-runs the request instead -
        // which is why the request is recorded as a value.
        CanRetry = Spec is not null || !string.IsNullOrWhiteSpace(Request);

        // Run again re-resolves the template as it stands NOW. Only offered when the template is
        // still there: a template that has been deleted or renamed has no current version, and a
        // button that fails when pressed is worse than one that is not there.
        var live = Spec is null ? null : currentTemplate?.Invoke();
        CanRunAgain = live is not null;
        RunAgainTooltip = Spec is null
            ? "This run did not come from a template."
            : live is null
                ? $"Template '{Spec.TemplateId}' is no longer in this library."
                : $"Re-resolve '{live.Name}' v{live.Version} — this run used v{Spec.TemplateVersion}.";

        RetryCommand = new RelayCommand(() => RetryRequested?.Invoke(this), () => CanRetry);
        RunAgainCommand = new RelayCommand(() => RunAgainRequested?.Invoke(this), () => CanRunAgain);
        Status = record.Status;
        // The pill is a tint with the colour in the word - see Brand. The 3px edge on a run
        // CARD stays saturated: an edge that thin has nowhere to put a tint.
        StatusBrush = Brand.PhaseFill(record.Status);
        StatusTextBrush = Brand.PhaseText(record.Status);

        var elapsed = record.FinishedAt - record.StartedAt;
        var model = string.IsNullOrWhiteSpace(record.Model) ? "unknown model" : record.Model;
        Meta = $"{record.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {model} · run {record.RunId:N}";

        // Folded from the record exactly as the live window folds the events as they arrive (RunFeed).
        foreach (var card in RunFeed.Replay(record).Cards)
            Steps.Add(new StepCardViewModel(card));
        foreach (var row in RunTimeline.Fold(record))
            Events.Add(row);
        foreach (var a in record.Artifacts)
            Artifacts.Add(new ArtifactItemViewModel(PathOf(a), open, remove));
        foreach (var d in record.Decisions)
            Decisions.Add(d);

        // The same three tiles the live run shows, off what was actually recorded.
        Routing = RunRouting.From(record);
        ModelWork = ModelWorkSplit.From(record, reachOf ?? (_ => ModelWorkSplit.Reach.Unknown));

        // Null usage means the run predates the counting, or the provider never reported - which is
        // not the same as zero and does not get to look like it.
        TokensText = record.Usage is { } u ? MainWindowViewModel.Compact(u.Total) : "—";
        TokensDetail = record.Usage is { } u2
            ? $"{MainWindowViewModel.Compact(u2.PromptTokens)} in · {MainWindowViewModel.Compact(u2.CompletionTokens)} out"
            : "nothing reported";

        ArtifactCountText = Artifacts.Count.ToString();
        ToolCallsText = record.Events.Count(e => e.Kind == nameof(EventKind.ToolInvoked)).ToString();
        ElapsedText = Duration(elapsed);

        var done = Steps.Count(c => c.StatusWord is "done" or "skipped");
        StepsText = Steps.Count == 0 ? "—" : $"{done} / {Steps.Count} steps";

        // What it was ALLOWED to do, as recorded. Runs from before this was kept say so rather
        // than showing today's slider position and passing it off as history.
        if (record.Settings is { } set)
        {
            HasSettings = true;
            AutonomyText = set.AutonomyName;
            AutonomyBrush = Brand.Autonomy(set.Autonomy);
            WorkerText = string.IsNullOrWhiteSpace(set.Worker) ? "default worker" : set.Worker;
            StagingText = set.Staged ? "changes were staged for review" : "changes were applied directly";
        }
        else
        {
            AutonomyText = WorkerText = StagingText = string.Empty;
            AutonomyBrush = Brand.TextMuted;
        }

        ShowExecutionCommand = new RelayCommand(() => SelectedTab = 0);
        ShowArtifactsCommand = new RelayCommand(() => SelectedTab = 1);
        ShowTimelineCommand = new RelayCommand(() => SelectedTab = 2);

        // A record that rebuilds no cards would show Execution as an empty tab claiming the run did
        // nothing, so it opens on the timeline instead - which is what such a record actually holds.
        // WHY there are none is Core's answer, not this window's: there are two reasons and they
        // send a person to two different places.
        NoStepsReason = RunReport.WhyNoSteps(record);
        if (Steps.Count == 0)
            SelectedTab = 2;
    }

    public RunRecord Record { get; }
    public string Title { get; }
    public string Meta { get; }
    public string Status { get; }
    public IBrush StatusBrush { get; }
    public IBrush StatusTextBrush { get; }

    /// <summary>The plan's steps, rebuilt from the record. Empty for a run stored before the step
    /// number was, in which case the timeline is the whole story.</summary>
    public ObservableCollection<StepCardViewModel> Steps { get; } = new();

    public bool HasSteps => Steps.Count > 0;
    public bool HasNoSteps => Steps.Count == 0;

    /// <summary>Why there are none, from Core — see RunReport.WhyNoSteps.</summary>
    public string NoStepsReason { get; }
    public string StepsText { get; }

    public ObservableCollection<RunEventViewModel> Events { get; } = new();
    public ObservableCollection<ArtifactItemViewModel> Artifacts { get; } = new();
    public ObservableCollection<string> Decisions { get; } = new();

    /// <summary>False for a run recorded before its setup was.</summary>
    public bool HasSettings { get; }
    public string AutonomyText { get; } = string.Empty;
    public IBrush AutonomyBrush { get; } = Brand.TextMuted;
    public string WorkerText { get; } = string.Empty;
    public string StagingText { get; } = string.Empty;

    public RunRouting Routing { get; }
    public ModelWorkSplit ModelWork { get; }
    public string TokensText { get; }
    public string TokensDetail { get; }

    public string ArtifactCountText { get; }
    public string ToolCallsText { get; }
    public string ElapsedText { get; }

    /// <summary>A tab carries its own count, so a run that produced nothing does not advertise "(0)".</summary>
    public string ArtifactsTabLabel => Artifacts.Count == 0 ? "Artifacts" : $"Artifacts ({Artifacts.Count})";

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value))
                return;
            OnPropertyChanged(nameof(IsExecutionTab));
            OnPropertyChanged(nameof(IsArtifactsTab));
            OnPropertyChanged(nameof(IsTimelineTab));
        }
    }

    public bool IsExecutionTab => _selectedTab == 0;
    public bool IsArtifactsTab => _selectedTab == 1;
    public bool IsTimelineTab => _selectedTab == 2;

    public RelayCommand ShowExecutionCommand { get; }
    public RelayCommand ShowArtifactsCommand { get; }
    public RelayCommand ShowTimelineCommand { get; }

    /// <summary>Empty is a fact worth stating, not a blank panel.</summary>
    public bool HasArtifacts => Artifacts.Count > 0;
    public bool HasNoArtifacts => Artifacts.Count == 0;
    public bool HasDecisions => Decisions.Count > 0;

    private static string Duration(TimeSpan span)
    {
        var seconds = span.TotalSeconds;
        if (seconds < 0)
            seconds = 0;
        return seconds >= 60 ? $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s" : $"{seconds:0}s";
    }

    /// <summary>
    /// A recorded artifact is the path. Older records kept the whole sentence — "FileSet: path" —
    /// and only a KNOWN artifact kind is stripped off the front of one, because splitting on the
    /// first ": " would eat the start of any path that happens to contain one.
    /// </summary>
    private static string PathOf(string artifact)
    {
        var colon = artifact.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0)
            return artifact;

        return Enum.TryParse<ArtifactKind>(artifact[..colon], ignoreCase: true, out _)
            ? artifact[(colon + 2)..]
            : artifact;
    }
}

/// <summary>
/// The workspace's run history in the context column. It does not own a store: which store a
/// workspace has is the window's business (SQLite, MySQL or files, per the environment), so this
/// asks for a refresh and is handed the records.
/// </summary>
internal sealed class RunsViewModel : ObservableObject
{
    private RunListItemViewModel? _selected;
    private string _status = "Not loaded yet.";

    public RunsViewModel()
    {
        RefreshCommand = new RelayCommand(() => RefreshRequested?.Invoke());
        DeleteShownCommand = new RelayCommand(
            () => DeleteShownRequested?.Invoke(Shown), () => Shown.Count > 0);
    }

    /// <summary>Asks whoever owns the stores to load this workspace's runs.</summary>
    public event Action? RefreshRequested;

    /// <summary>
    /// A row was picked. The window decides what "open" means; the list only reports it.
    ///
    /// <para>It reports the HEADER, which is all the list ever held. Opening a run is where the
    /// transcript gets read, and the window does that by id - so the cost of a whole record is paid
    /// once, by the row somebody actually clicked.</para>
    /// </summary>
    public event Action<RunSummary>? OpenRequested;

    /// <summary>
    /// A row's bin was pressed. The window owns the store and the confirmation dialog, so it does
    /// the asking and the deleting; the list only reports the click.
    /// </summary>
    public event Action<RunSummary>? DeleteRequested;

    /// <summary>Forget every run the current filter is showing. The window asks first.</summary>
    public event Action<IReadOnlyList<RunSummary>>? DeleteShownRequested;

    /// <summary>Somebody asked to carry an interrupted run on. The window owns the running.</summary>
    public event Action<RunCheckpoint>? ResumeRequested;

    public ObservableCollection<RunListItemViewModel> Items { get; } = new();

    /// <summary>Runs nobody knows the end of. Empty is the normal state and shows nothing at all.</summary>
    public ObservableCollection<ResumableRunViewModel> Resumable { get; } = new();

    public bool HasResumable => Resumable.Count > 0;

    /// <summary>Runs in flight in this workspace, newest first. See LiveRunViewModel.</summary>
    public ObservableCollection<LiveRunViewModel> Running { get; } = new();

    public bool HasRunning => Running.Count > 0;

    /// <summary>Back to the live feed: a row was pressed. The window owns what "live" means.</summary>
    public event Action? OpenLiveRequested;

    /// <summary>
    /// Replaces the live rows, unless they would come out identical.
    ///
    /// <para>The window calls this on every engine event, because an event is what changes what a
    /// row says - and most events change nothing about it. Rebuilding regardless would restyle two
    /// rows several times a second under somebody's pointer, so an unchanged list is left exactly
    /// as it is, objects included.</para>
    /// </summary>
    public void ShowRunning(IReadOnlyList<LiveRun> live)
    {
        if (Running.Count == live.Count
            && Running.Select(r => r.Run).SequenceEqual(live))
            return;

        Running.Clear();
        foreach (var run in live)
            Running.Add(new LiveRunViewModel(run, () => OpenLiveRequested?.Invoke()));

        OnPropertyChanged(nameof(HasRunning));
    }

    /// <summary>
    /// Offers the interrupted runs found in this workspace.
    ///
    /// <para>A checkpoint with nothing left to do is not offered: it belongs to a run that finished
    /// its steps and died somewhere after them, and a Resume that runs nothing is a button that
    /// lies about what pressing it does.</para>
    /// </summary>
    public void ShowResumable(IReadOnlyList<RunCheckpoint> checkpoints)
    {
        Resumable.Clear();
        foreach (var checkpoint in checkpoints.Where(c => c.IsResumable))
            Resumable.Add(new ResumableRunViewModel(checkpoint, c => ResumeRequested?.Invoke(c)));
        OnPropertyChanged(nameof(HasResumable));
    }

    public string Status { get => _status; set => Set(ref _status, value); }

    public RunListItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            if (value is not null)
                OpenRequested?.Invoke(value.Record);
        }
    }

    public RelayCommand RefreshCommand { get; }

    /// <summary>
    /// Every header behind the list. Kept because a run's SIBLINGS - the other attempts at the same
    /// task - are not visible from the run itself, and the window is the only thing that has them.
    /// </summary>
    public IReadOnlyList<RunSummary> All { get; private set; } = Array.Empty<RunSummary>();

    /// <summary>
    /// Which runs the list is showing. Changing it re-lists from what is already in hand: the
    /// filter is a question about records this window already has, not a reason to go back to the
    /// store.
    /// </summary>
    public RunFilter Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value))
                return;
            OnPropertyChanged(nameof(FilterIndex));
            Show(All);
        }
    }

    private RunFilter _filter = RunFilter.All;

    /// <summary>The combo box's own index, because Avalonia binds a selection by position.</summary>
    public int FilterIndex
    {
        get => (int)_filter;
        set
        {
            if (value >= 0 && value <= (int)RunFilter.OlderThanAWeek)
                Filter = (RunFilter)value;
        }
    }

    public IReadOnlyList<string> FilterNames { get; } =
        ["All runs", "Unfinished", "Completed", "Older than a week"];

    /// <summary>What a "delete these" would act on: exactly the rows on screen.</summary>
    public IReadOnlyList<RunSummary> Shown { get; private set; } = Array.Empty<RunSummary>();

    /// <summary>
    /// Named with the COUNT, so the button says how much it destroys before it is pressed rather
    /// than in a dialog after it.
    /// </summary>
    public string DeleteShownLabel => $"Delete these {Shown.Count}";

    public bool CanDeleteShown => Shown.Count > 0;

    public RelayCommand DeleteShownCommand { get; }

    public void Show(IReadOnlyList<RunSummary> records)
    {
        All = records;
        // Rebuilding drops the selection, so the row the user is reading is re-selected by run id
        // rather than by position - a finished run pushes everything down by one.
        var keep = _selected?.Record.RunId;

        Shown = RunHousekeeping.Where(records, _filter, DateTimeOffset.UtcNow);

        Items.Clear();
        foreach (var row in RunHousekeeping.Rows(Shown, _expanded))
            Items.Add(new RunListItemViewModel(
                row.Run,
                item => DeleteRequested?.Invoke(item.Record),
                row.Attempts, row.IsLead, _expanded.Contains(RunHousekeeping.GroupKey(row.Run)), Toggle));

        Status = records.Count == 0
            ? "No runs recorded in this workspace."
            : Items.Count == records.Count
                ? $"{Items.Count} run{(Items.Count == 1 ? "" : "s")}"
                // Both numbers when a filter is on. "3 runs" over a list that holds 37 is a true
                // sentence that reads as the whole truth.
                : $"{Items.Count} of {records.Count} runs";

        OnPropertyChanged(nameof(Shown));
        OnPropertyChanged(nameof(DeleteShownLabel));
        OnPropertyChanged(nameof(CanDeleteShown));
        DeleteShownCommand.RaiseCanExecuteChanged();

        if (keep is { } id)
            _selected = Items.FirstOrDefault(i => i.Record.RunId == id);
        else
            _selected = null;
        OnPropertyChanged(nameof(Selected));

        // Coming back to a workspace that was left reading a run. Done through the ordinary
        // selection, so returning takes exactly the path a click takes - one way to open a run, not
        // two that can come to disagree about what opening one means.
        if (Reopen is { } wanted)
        {
            Reopen = null;

            if (Items.FirstOrDefault(i => i.Record.RunId == wanted) is { } row)
                Selected = row;
            else if (records.FirstOrDefault(r => r.RunId == wanted) is { } summary)
                // In the workspace but not on screen: an older attempt whose group is collapsed, or
                // one the filter excludes. Opened anyway and left unselected, because the row
                // genuinely is not there to highlight.
                OpenRequested?.Invoke(summary);
        }
    }

    /// <summary>
    /// The run to open when this workspace's list arrives, or null for an empty middle column.
    ///
    /// <para>Set by the window from <c>OpenRunMemory</c> just before the workspace changes, because
    /// changing the workspace is what starts the list loading. It is consumed once: a workspace
    /// comes back to what it was left reading, and then behaves like any other.</para>
    /// </summary>
    public Guid? Reopen { get; set; }

    /// <summary>
    /// Opens or closes one task's attempts.
    ///
    /// <para>Kept by TASK id rather than by row, because Show rebuilds every row: a finished run
    /// re-lists the column, and expansion held on the row objects would close itself every few
    /// seconds while somebody was reading.</para>
    /// </summary>
    private void Toggle(RunListItemViewModel row)
    {
        var key = RunHousekeeping.GroupKey(row.Record);

        if (!_expanded.Remove(key))
            _expanded.Add(key);

        Show(All);
    }

    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    /// <summary>The workspace changed: what is listed belongs to the old one.</summary>
    public void Reset()
    {
        Resumable.Clear();
        OnPropertyChanged(nameof(HasResumable));
        // A background run belongs to the workspace it was started in. The window re-offers the
        // ones that belong here.
        Running.Clear();
        OnPropertyChanged(nameof(HasRunning));
        Items.Clear();
        _selected = null;
        OnPropertyChanged(nameof(Selected));
        Status = "Not loaded yet.";
    }

    public void Fail(string message) => Status = "Could not read the run store: " + message;
}
