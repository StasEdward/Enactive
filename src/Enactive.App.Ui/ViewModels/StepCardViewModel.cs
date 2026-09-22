namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.History;

/// <summary>
/// One line of a step's action log: an icon, a short label, and an optional muted one-line result
/// attached afterwards when the tool comes back.
/// </summary>
internal sealed class StepEntry : ObservableObject
{
    private string _detail = string.Empty;

    public StepEntry(string icon, IBrush iconBrush, string label, IBrush labelBrush, bool bold)
    {
        Icon = icon;
        IconBrush = iconBrush;
        Label = label;
        LabelBrush = labelBrush;
        LabelWeight = bold ? FontWeight.SemiBold : FontWeight.Normal;
    }

    public string Icon { get; }
    public IBrush IconBrush { get; }
    public string Label { get; }
    public IBrush LabelBrush { get; }
    public FontWeight LabelWeight { get; }

    /// <summary>Set once, when the tool this row describes returns. Empty until then.</summary>
    public string Detail
    {
        get => _detail;
        set
        {
            if (Set(ref _detail, value))
                OnPropertyChanged(nameof(HasDetail));
        }
    }

    public bool HasDetail => _detail.Length > 0;
}

/// <summary>
/// One plan step, as the UI sees it. Mirrors a tool-call log, not a chat transcript: a short
/// "what's happening now" line stays visible, and everything the agent actually DID - commands run,
/// files touched, brief remarks - collapses behind a single summary toggle, one click away when
/// something needs checking. The model's raw streamed prose is never shown verbatim; short remarks
/// are folded into one-line notes instead.
/// </summary>
internal sealed class StepCardViewModel : ObservableObject
{
    private const string CommandIcon = "⌘";
    private const string FileIcon = "📄";
    private const string ToolIcon = "🛠";
    private const string NoteIcon = "🔔";
    private const string RefusedIcon = "🛇";

    private readonly StringBuilder _noteBuffer = new();

    private string _statusWord = "pending";
    private IBrush _statusBrush = Brand.StepPending;
    private string _activity = "Waiting…";
    private string _toggleLabel = string.Empty;
    private bool _isExpanded;
    private bool _userExpanded;   // once the user toggles it, stop overriding their choice
    private StepEntry? _lastEntry;
    private int _toolCount;
    private int _commandCount;
    private int _fileCount;
    private int _noteCount;
    private int _refusedCount;

    public StepCardViewModel(string title)
    {
        Title = title;
        ToggleCommand = new RelayCommand(() =>
        {
            _userExpanded = true;
            IsExpanded = !IsExpanded;
        });
    }

    public string Title { get; }

    public ObservableCollection<StepEntry> Entries { get; } = new();

    /// <summary>"pending" / "running" / "done" / "failed" - and the card's left edge takes its colour.</summary>
    public string StatusWord { get => _statusWord; set => Set(ref _statusWord, value); }

    public IBrush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    /// <summary>The short "what's happening now" line - e.g. "Thinking…", "Writing report.md…".</summary>
    public string Activity { get => _activity; set => Set(ref _activity, value); }

    public string ToggleLabel { get => _toggleLabel; set => Set(ref _toggleLabel, value); }

    /// <summary>The disclosure row appears only once the step has something to disclose.</summary>
    public bool HasEntries => Entries.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                // "›" collapsed, "⌄" open - a glyph swap rather than a rotate transform keeps the
                // template simple and behaves the same on every platform.
                OnPropertyChanged(nameof(Chevron));
        }
    }

    public string Chevron => IsExpanded ? "⌄" : "›";

    public RelayCommand ToggleCommand { get; }

    /// <summary>
    /// The one status that is not a verdict but a state, and the ellipsis says so: "running" reads
    /// like a label, "running…" reads like something still happening — the same punctuation the
    /// activity line under it already uses.
    /// </summary>
    public void SetRunning() => SetStatus("running…", Brand.StepRunning);

    public void SetDone()
    {
        FlushPendingNote();
        SetStatus("done", Brand.StepDone);
    }

    public void SetFailed()
    {
        FlushPendingNote();
        SetStatus("failed", Brand.StepFailed);
    }

    /// <summary>
    /// A step that never ran because something it depended on failed. Not red: nothing went wrong
    /// HERE, and painting it like a failure sends you looking for a fault in the wrong step.
    /// </summary>
    public void SetSkipped()
    {
        FlushPendingNote();
        SetStatus("skipped", Brand.StepSkipped);
    }

    public void SetActivity(string text) => Activity = text;

    /// <summary>
    /// Pops the card open so something that needs the user's attention - a recovered implicit tool
    /// call, an error - is not hidden behind the collapsed default. Only auto-expands once: if the
    /// user has already collapsed it back by hand, that choice stands.
    ///
    /// <para>It EXPANDS and nothing else. It used to also repaint the status amber, which is how a
    /// failed step came out amber instead of red: the failure path sets the status and THEN asks for
    /// the card to be opened, so the second call quietly undid the first. A method named for
    /// expanding had no business deciding what colour the step was, and the bug was invisible in the
    /// code because both lines read as if they were doing different things.</para>
    /// </summary>
    public void ExpandForAttention()
    {
        if (!_userExpanded)
            IsExpanded = true;
    }

    /// <summary>
    /// The step is BLOCKED on the person: a question has been asked and nothing moves until it is
    /// answered. Amber, not red - the step has not failed, it is waiting.
    ///
    /// <para>Whatever status the step ends with overwrites this, because that one is the truth about
    /// how it finished and this is only the truth about right now. <see cref="SetRunning"/> puts the
    /// colour back when the answer arrives.</para>
    ///
    /// <para><b>This used to fire on every warning as well</b>, and that is what it looked like: a
    /// run reported nine advisory notes - an MCP server nobody had granted, a check that already
    /// passed - and its card sat amber for the whole eleven minutes it was working. A warning is
    /// worth READING, which is why the card still opens itself and shows the note; it is not the
    /// step being stuck, and the edge of a card says what the step is doing.</para>
    /// </summary>
    public void SetWaitingForYou()
    {
        ExpandForAttention();
        StatusBrush = Brand.Warning;
    }

    /// <summary>
    /// Buffers a chunk of the assistant's streamed reply. It is never shown verbatim - it is folded
    /// into a single short note the next time a tool runs or the step ends.
    /// </summary>
    public void AppendAssistantText(string delta) => _noteBuffer.Append(delta);

    public void AddCommand(string commandText)
    {
        FlushPendingNote();
        _toolCount++;
        _commandCount++;
        AddEntry(CommandIcon, Brand.Info, "Ran: " + Truncate(commandText, 90), Brand.TextBody, bold: false);
        UpdateSummary();
    }

    /// <summary>Logs a file operation - verb is a short past-tense word like "Wrote", "Read", "Listed".</summary>
    public void AddFileOp(string verb, string path)
    {
        FlushPendingNote();
        _toolCount++;
        if (string.Equals(verb, "Wrote", StringComparison.Ordinal))
            _fileCount++;
        AddEntry(FileIcon, Brand.Success, $"{verb} {path}", Brand.TextBody, bold: false);
        UpdateSummary();
    }

    public void AddGenericTool(string label)
    {
        FlushPendingNote();
        _toolCount++;
        AddEntry(ToolIcon, Brand.Info, label, Brand.TextBody, bold: false);
        UpdateSummary();
    }

    /// <summary>Attaches a short one-line result under the most recently logged tool entry.</summary>
    public void AppendEntryDetail(string text)
    {
        if (_lastEntry is null)
            return;
        var flat = Truncate(text.Replace('\n', ' ').Replace('\r', ' ').Trim(), 160);
        if (flat.Length > 0)
            _lastEntry.Detail = flat;
    }

    /// <summary>Logs an explicit short note - a warning, a review or decision remark, an artifact
    /// mention - always in order relative to any buffered streamed text.</summary>
    public void AddNote(string text)
    {
        FlushPendingNote();
        AddNoteEntry(text);
        UpdateSummary();
    }

    /// <summary>
    /// A call the policy — or whoever answers for it — did not let through.
    ///
    /// <para>Counted apart from tools, and not as a tool: it never ran, and saying it did would be
    /// the opposite of the lie this fixes. Until 2026-09-11 a refusal was logged with
    /// <see cref="AddNote"/>, so a step stopped six times reported "14 notes" and nothing else — the
    /// summary had no word for it.</para>
    /// </summary>
    public void AddRefusal(string text)
    {
        FlushPendingNote();
        _refusedCount++;
        AddEntry(RefusedIcon, Brand.Danger, text, Brand.TextBody, bold: false);
        UpdateSummary();
    }

    /// <summary>Commits any buffered streamed text as a single note, if there is any.</summary>
    public void FlushPendingNote()
    {
        if (_noteBuffer.Length == 0)
            return;
        var text = _noteBuffer.ToString();
        _noteBuffer.Clear();
        AddNoteEntry(text);
        UpdateSummary();
    }

    private void AddNoteEntry(string text)
    {
        var flat = Truncate(text.Replace('\n', ' ').Replace('\r', ' ').Trim(), 220);
        if (flat.Length == 0)
            return;
        _noteCount++;
        AddEntry(NoteIcon, Brand.Warning, flat, Brand.TextBody, bold: true);
    }

    private void AddEntry(string icon, IBrush iconBrush, string label, IBrush labelBrush, bool bold)
    {
        _lastEntry = new StepEntry(icon, iconBrush, label, labelBrush, bold);
        Entries.Add(_lastEntry);
        OnPropertyChanged(nameof(HasEntries));
    }

    // The counting happens here because it is incremental; the SENTENCE is StepTally's, in Core,
    // where a test can read it. It was a private method on this class - in a WinExe no test project
    // references - which is why the one line a person reads to decide whether to open a step was
    // the one thing about a step nothing checked.
    private void UpdateSummary()
        => ToggleLabel = new StepTally(_toolCount, _commandCount, _fileCount, _refusedCount, _noteCount)
            .Words();

    private void SetStatus(string word, IBrush brush)
    {
        StatusWord = word;
        StatusBrush = brush;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
