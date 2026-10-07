namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
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
/// something needs checking.
///
/// <para>A view of a <see cref="FeedCard"/>, and nothing more. What a card says and where it stands is
/// decided by <see cref="RunFeed"/>, in Core, the same fold for a run happening in front of you and for
/// one reopened from the history; this only turns its status into a word and a colour and its lines into
/// rows. It used to decide all of it here, in a WinExe no test could reach, and the replay had a copy of
/// its own that had drifted.</para>
/// </summary>
internal sealed class StepCardViewModel : ObservableObject
{
    private readonly FeedCard _card;
    private int _syncedVersion = -1;
    private string _statusWord = "pending";
    private IBrush _statusBrush = Brand.StepPending;
    private string _activity = "Waiting…";
    private string _toggleLabel = string.Empty;
    private bool _isExpanded;

    public StepCardViewModel(FeedCard card)
    {
        _card = card;
        Title = card.Title;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        Sync();
    }

    public string Title { get; }

    public ObservableCollection<StepEntry> Entries { get; } = new();

    public string StatusWord { get => _statusWord; private set => Set(ref _statusWord, value); }

    public IBrush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    public string Activity { get => _activity; private set => Set(ref _activity, value); }

    public string ToggleLabel { get => _toggleLabel; private set => Set(ref _toggleLabel, value); }

    public bool HasEntries => Entries.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (Set(ref _isExpanded, value))
                // "›" collapsed, "⌄" open - a glyph swap rather than a rotate transform keeps the
                // template simple and behaves the same on every platform.
                OnPropertyChanged(nameof(Chevron));
        }
    }

    public string Chevron => IsExpanded ? "⌄" : "›";

    public RelayCommand ToggleCommand { get; }

    /// <summary>Draws what the card has become since it was last drawn. Cheap when nothing has: one comparison.</summary>
    public void Sync()
    {
        if (_card.Version == _syncedVersion)
            return;
        _syncedVersion = _card.Version;

        StatusWord = Word(_card.Status);
        // Amber while a question waits for a person - the step has not failed, it is waiting.
        StatusBrush = _card.WaitingForYou ? Brand.Warning : BrushOf(_card.Status);
        Activity = _card.Activity;
        ToggleLabel = _card.Tally;

        // Lines are only ever added, and only the newest one's result can still arrive - so the rows from the last
        // drawn one on are all that can have changed.
        for (var i = Math.Max(0, Entries.Count - 1); i < _card.Entries.Count; i++)
        {
            var entry = _card.Entries[i];
            if (i < Entries.Count)
                Entries[i].Detail = entry.Detail;
            else
            {
                Entries.Add(Row(entry));
                OnPropertyChanged(nameof(HasEntries));
            }
        }
    }

    private static string Word(FeedCardStatus status) => status switch
    {
        FeedCardStatus.Running => "running…",
        FeedCardStatus.Done => "done",
        FeedCardStatus.Failed => "failed",
        FeedCardStatus.Skipped => "skipped",
        FeedCardStatus.Unverified => "not verified",
        FeedCardStatus.Blocked => "blocked",
        _ => "pending"
    };

    private static IBrush BrushOf(FeedCardStatus status) => status switch
    {
        FeedCardStatus.Running => Brand.StepRunning,
        FeedCardStatus.Done => Brand.StepDone,
        FeedCardStatus.Failed => Brand.StepFailed,
        FeedCardStatus.Skipped => Brand.StepSkipped,
        FeedCardStatus.Unverified => Brand.StepUnverified,
        // Amber, like waiting for an answer: nothing went wrong in it, and it is done again once the cause is put right.
        FeedCardStatus.Blocked => Brand.Warning,
        _ => Brand.StepPending
    };

    private static StepEntry Row(FeedEntry entry)
    {
        var row = entry.Kind switch
        {
            FeedEntryKind.Command => new StepEntry("⌘", Brand.Info, entry.Label, Brand.TextBody, bold: false),
            FeedEntryKind.File => new StepEntry("📄", Brand.Success, entry.Label, Brand.TextBody, bold: false),
            FeedEntryKind.Note => new StepEntry("🔔", Brand.Warning, entry.Label, Brand.TextBody, bold: true),
            FeedEntryKind.Refusal => new StepEntry("🛇", Brand.Danger, entry.Label, Brand.TextBody, bold: false),
            _ => new StepEntry("🛠", Brand.Info, entry.Label, Brand.TextBody, bold: false)
        };
        row.Detail = entry.Detail;
        return row;
    }
}
