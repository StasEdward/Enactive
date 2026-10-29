namespace Enactive.Application;

using System.Collections.ObjectModel;
using Enactive.Application.Mvvm;
using Enactive.Core.Rules;
using Enactive.Workspace;

/// <summary>
/// How a host reads a workspace's lessons and decides on one (Plan Phase 5.2): the window straight through the stores
/// (<see cref="BookLessons"/>); the service as the person, after its own checks of the role, and audited (ServerLessons).
/// A change says why it was not made, or null when it was - what it says is what the panel shows.
/// </summary>
internal interface ILessonChanges
{
    Task<IReadOnlyList<RuleEntry>> ListAsync(CancellationToken ct);

    /// <summary>Why no change to <paramref name="scope"/> may be made by this person, or null.</summary>
    string? WhyNot(RuleScope scope);

    Task<string?> KeepAsync(RuleEntry entry, string? text, RuleScope scope, CancellationToken ct);
    Task<string?> DismissAsync(RuleEntry entry, CancellationToken ct);
    Task<string?> DeleteAsync(RuleEntry entry, CancellationToken ct);
    Task<string?> AddAsync(string text, RuleScope scope, CancellationToken ct);
}

/// <summary>The window's: the workspace's store and the machine's Global file, through the one <see cref="RuleBook"/>.</summary>
internal sealed class BookLessons(RuleBook book) : ILessonChanges
{
    public Task<IReadOnlyList<RuleEntry>> ListAsync(CancellationToken ct) => book.ListAsync(ct);

    public string? WhyNot(RuleScope scope) => null;

    public Task<string?> KeepAsync(RuleEntry entry, string? text, RuleScope scope, CancellationToken ct)
        => Said(() => book.KeepAsync(entry, text, scope, ct), "Not kept");

    public Task<string?> DismissAsync(RuleEntry entry, CancellationToken ct) => Said(() => book.DismissAsync(entry, ct), "Not dismissed");

    public Task<string?> DeleteAsync(RuleEntry entry, CancellationToken ct) => Said(() => book.DeleteAsync(entry, ct), "Not deleted");

    public Task<string?> AddAsync(string text, RuleScope scope, CancellationToken ct) => Said(() => book.AddAsync(text, scope, ct), "Not added");

    /// <summary>A store that refused is said, never thrown into a button's click.</summary>
    internal static async Task<string?> Said(Func<Task> change, string what)
    {
        try
        {
            await change();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or ArgumentException or TimeoutException or System.Data.Common.DbException)
        {
            return $"{what} - {ex.Message}";
        }
    }
}

/// <summary>
/// The lessons of one workspace (Plan Phase 5.2): the candidates its runs proposed, waiting on a person, and the rules a
/// person kept, given to every new run. A candidate is kept as it is or in the person's own words, here or for every
/// workspace - Workspace by default, the narrowest - or dismissed; a rule is changed or deleted; a person writes their own.
/// One list for the window's panel and the service's page.
/// </summary>
internal sealed class LessonsViewModel : ObservableObject
{
    private readonly ILessonChanges _changes;
    private string? _said;
    private string _newText = "";
    private bool _newGlobal;

    public LessonsViewModel(ILessonChanges changes, string heading)
    {
        _changes = changes;
        Heading = heading;
        AddCommand = new AsyncRelayCommand(AddAsync, () => !string.IsNullOrWhiteSpace(_newText));
    }

    public string Heading { get; }

    /// <summary>What the runs proposed and nobody has decided: given to no run.</summary>
    public ObservableCollection<LessonRow> Candidates { get; } = [];

    /// <summary>What a person kept or wrote: given to every new run, Global first.</summary>
    public ObservableCollection<LessonRow> Rules { get; } = [];

    public bool HasCandidates => Candidates.Count > 0;
    public bool HasRules => Rules.Count > 0;
    public bool IsEmpty => !HasCandidates && !HasRules;

    /// <summary>"Lessons (2)": the candidates waiting, for a tab or a link.</summary>
    public string TabLabel => Candidates.Count > 0 ? $"Lessons ({Candidates.Count})" : "Lessons";

    /// <summary>Confirmed rules a run would not be given: over the set's cap (AppliedRules.MaxChars), newest first out.</summary>
    public string? LeftOut { get; private set; }
    public bool HasLeftOut => LeftOut is not null;

    /// <summary>What the last change said - why it was not made - or why the list could not be read.</summary>
    public string? Said
    {
        get => _said;
        private set
        {
            if (Set(ref _said, value))
                OnPropertyChanged(nameof(HasSaid));
        }
    }

    public bool HasSaid => !string.IsNullOrEmpty(_said);

    /// <summary>A rule of the person's own, being written.</summary>
    public string NewText
    {
        get => _newText;
        set
        {
            if (Set(ref _newText, value))
                AddCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The new rule is for every workspace, not this one.</summary>
    public bool NewGlobal
    {
        get => _newGlobal;
        set => Set(ref _newGlobal, value);
    }

    /// <summary>Whether this person may write a rule here at all - the service's roles; the window always may.</summary>
    public bool CanAdd => _changes.WhyNot(RuleScope.Workspace) is null;

    public AsyncRelayCommand AddCommand { get; }

    /// <summary>Reads both layers again. A store that cannot be read is said, and the list left as it was.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IReadOnlyList<RuleEntry> all;
        try
        {
            all = await _changes.ListAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or TimeoutException or System.Data.Common.DbException)
        {
            Said = "The lessons could not be read - " + ex.Message;
            return;
        }

        Candidates.Clear();
        Rules.Clear();
        foreach (var entry in all.Where(e => e.Status == RuleStatus.Candidate).OrderByDescending(e => e.At))
            Candidates.Add(Row(entry));
        foreach (var entry in all.Where(e => e.Status == RuleStatus.Confirmed))
            Rules.Add(Row(entry));
        var applied = AppliedRules.Compose(all.Where(e => e.Scope == RuleScope.Global), all.Where(e => e.Scope == RuleScope.Workspace));
        LeftOut = applied.Left > 0
            ? $"{applied.Left} rule(s) do not fit the {AppliedRules.MaxChars}-character limit a run is given and are left out, newest first. Delete or shorten some."
            : null;
        foreach (var name in new[] { nameof(HasCandidates), nameof(HasRules), nameof(IsEmpty), nameof(TabLabel), nameof(LeftOut), nameof(HasLeftOut), nameof(CanAdd) })
            OnPropertyChanged(name);
    }

    private LessonRow Row(RuleEntry entry)
        => new(entry, _changes.WhyNot(entry.Scope) is null, Act);

    private async Task Act(LessonRow row, LessonAction action)
    {
        var scope = row.Global ? RuleScope.Global : RuleScope.Workspace;
        // Moving a rule between layers is a change to both.
        if ((_changes.WhyNot(scope) ?? _changes.WhyNot(row.Entry.Scope)) is { } why)
        {
            Said = why;
            return;
        }
        var said = action switch
        {
            LessonAction.Keep => await _changes.KeepAsync(row.Entry, row.Text, scope, CancellationToken.None),
            LessonAction.Dismiss => await _changes.DismissAsync(row.Entry, CancellationToken.None),
            _ => await _changes.DeleteAsync(row.Entry, CancellationToken.None)
        };
        Said = said;
        if (said is null)
            await RefreshAsync();
    }

    private async Task AddAsync()
    {
        var scope = _newGlobal ? RuleScope.Global : RuleScope.Workspace;
        var said = _changes.WhyNot(scope) ?? await _changes.AddAsync(_newText, scope, CancellationToken.None);
        Said = said;
        if (said is not null)
            return;
        NewText = "";
        NewGlobal = false;
        await RefreshAsync();
    }
}

internal enum LessonAction { Keep, Dismiss, Delete }

/// <summary>
/// One candidate or rule: its text, editable before it is kept (a candidate is the observation as the engine made it; the
/// person turns it into an instruction), the layer it is for, where it came from, and what may be done with it.
/// </summary>
internal sealed class LessonRow : ObservableObject
{
    private string _text;
    private bool _global;

    public LessonRow(RuleEntry entry, bool canChange, Func<LessonRow, LessonAction, Task> act)
    {
        Entry = entry;
        CanChange = canChange;
        _text = entry.Text;
        _global = entry.Scope == RuleScope.Global;
        KeepCommand = new AsyncRelayCommand(() => act(this, LessonAction.Keep), () => CanChange && !string.IsNullOrWhiteSpace(_text));
        DismissCommand = new AsyncRelayCommand(() => act(this, LessonAction.Dismiss), () => CanChange);
        DeleteCommand = new AsyncRelayCommand(() => act(this, LessonAction.Delete), () => CanChange);
    }

    public RuleEntry Entry { get; }
    public bool CanChange { get; }
    public bool IsCandidate => Entry.Status == RuleStatus.Candidate;

    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value))
                KeepCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>For every workspace rather than this one. Applied when the row is kept (saved, for a rule).</summary>
    public bool Global
    {
        get => _global;
        set => Set(ref _global, value);
    }

    /// <summary>"from run "...", step 2: [2] FAIL (step review)" - for the person, never for a model.</summary>
    // "from" only for what a run proposed: a rule a person wrote says so as it is ("from written by a person", on screen).
    public string Origin => Entry.Origin is { Length: > 0 } origin ? Entry.SourceRunId is null ? origin : "from " + origin : "";
    public bool HasOrigin => Entry.Origin is { Length: > 0 };

    public string ScopeText => Entry.Scope == RuleScope.Global ? "every workspace" : "this workspace";

    /// <summary>A rule's line under its text: "this workspace · written by a person".</summary>
    public string Meta => HasOrigin ? $"{ScopeText} · {Origin}" : ScopeText;

    /// <summary>"Keep" for a candidate, "Save" for a rule whose words or layer are being changed.</summary>
    public string KeepText => IsCandidate ? "Keep" : "Save";

    public AsyncRelayCommand KeepCommand { get; }
    public AsyncRelayCommand DismissCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
}
