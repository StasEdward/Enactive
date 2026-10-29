namespace Enactive.Application;

using System.Collections.ObjectModel;
using Enactive.Application.Mvvm;
using Enactive.Core.Checks;
using Enactive.Core.Storage;
using Enactive.Workspace;

/// <summary>
/// How a host changes a check manifest (Plan Phase 3.5): the window writes the file itself (<see cref="StoreCheckChanges"/>);
/// the service does it as the person, after its own checks of the role, and audits it (ServerChecks). Each says why it did
/// not, or null when it did - what it says is what the page shows.
/// </summary>
internal interface ICheckChanges
{
    /// <summary>Why no change to <paramref name="scope"/> may be made now, or null - asked before offering one, and again at it.</summary>
    string? WhyNot(CheckScope scope);

    /// <summary>Writes the manifest of <paramref name="id"/> saying only <paramref name="said"/>: what the person set.</summary>
    string? Save(string id, CheckManifestSaid said, CheckScope scope);

    string? Delete(string id, CheckScope scope);
}

/// <summary>The words a refusal of a check change is said in, the same in every host.</summary>
internal static class CheckChangeWords
{
    /// <summary>
    /// A run working in the workspace puts its checks back at its end as they were when it started (CheckManifestGuard: a
    /// run may not change the checks that judge it), and a person's edit made meanwhile is put back with them - said by
    /// nothing, and found undone later. So it is refused, with why.
    /// </summary>
    public const string RunActive = "A run is working in this workspace. Change its checks when it has ended: the run puts "
        + "the checks back as they were when it started, so a change made now would be undone.";

    /// <summary>The same, naming the other program that runs there, when it wrote its name down.</summary>
    public static string RunActiveIn(LeaseHolder holder)
        => $"A run is working in this workspace ({holder.Describe()}). Change its checks when it has ended: the run puts the "
           + "checks back as they were when it started, so a change made now would be undone.";

    /// <summary>
    /// Why a workspace's checks may not be changed now, or null: a run holds the workspace (WorkspaceLease). The lease is the
    /// one thing every run takes on every host - the window's foreground and background runs, a schedule, the phone, a
    /// console, the service - from its compose to its end, which is when it puts its checks back. Asking only the window's
    /// foreground run let a background run, or another program's, undo the edit.
    /// </summary>
    public static string? WhyNotNow(string? workspaceRoot)
    {
        if (workspaceRoot is not { Length: > 0 } root)
            return null;
        try
        {
            return WorkspaceLease.IsHeld(root, out var elsewhere) ? elsewhere is null ? RunActive : RunActiveIn(elsewhere) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A folder whose lease cannot be asked about is not one to write into blind.
            return "Whether a run is working in this workspace cannot be told (" + ex.Message + "), so its checks are not changed.";
        }
    }

    public static string NotSaved(IEnumerable<string> problems) => "Not saved - " + string.Join("; ", problems);
}

/// <summary>
/// The window's: straight to the check files, as its templates are - the store's own validation first, so nothing the store
/// would skip is written - and nothing written to a workspace while a run is working in it.
/// </summary>
/// <param name="workspaceRoot">The workspace the store's workspace checks are of; null for a store of global checks only.</param>
internal sealed class StoreCheckChanges(CheckStore store, string? workspaceRoot) : ICheckChanges
{
    /// <summary>
    /// Only a workspace's own checks are refused while a run works in it: the run puts back THAT folder (CheckManifestGuard),
    /// and it took its global checks into its snapshot when it started, so a global change is the next run's.
    /// </summary>
    public string? WhyNot(CheckScope scope) => scope == CheckScope.Workspace ? CheckChangeWords.WhyNotNow(workspaceRoot) : null;

    public string? Save(string id, CheckManifestSaid said, CheckScope scope)
    {
        if (WhyNot(scope) is { } why)
            return why;
        if (store.Validate(id, said, scope) is { Count: > 0 } problems)
            return CheckChangeWords.NotSaved(problems);
        try
        {
            store.Save(id, said, scope);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "Not saved - " + ex.Message;
        }
    }

    public string? Delete(string id, CheckScope scope)
    {
        if (WhyNot(scope) is { } why)
            return why;
        try
        {
            store.Delete(id, scope);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "Not deleted - " + ex.Message;
        }
    }
}

/// <summary>A manifest the store skipped, or a note on one it read - beside the file it is about (CheckInventory).</summary>
internal sealed record CheckFileNote(string File, string Where, string Message, bool IsProblem)
{
    /// <summary>"lint.json (this workspace): skipped - ..." for a problem, "...: note - ..." for a note.</summary>
    public string Text => $"{File} ({Where}): {(IsProblem ? "skipped" : "note")} - {Message}";
}

/// <summary>
/// One check of the merged list: where it comes from and what it shadows, how it runs, and what may be done to it from this
/// list - edited, deleted (reset, for one that changes the check below), turned on or off, or, for the engine's implicit
/// build and test, raised to blocking.
/// </summary>
internal sealed class CheckRow
{
    public CheckRow(ResolvedCheck check, CheckScope scope, bool canChange, IReadOnlyList<string> notes,
        Action<CheckRow> edit, Action<CheckRow> remove, Action<CheckRow> toggle)
    {
        Check = check;
        Notes = string.Join(" ", notes);
        IsOwn = OriginOf(scope) == check.Origin;
        // The layers in the order they shadow (CheckOrigin: Implicit, Global, Workspace). Above this list's scope - a
        // workspace check in the global list - is another list's to change.
        var below = check.Origin < OriginOf(scope);
        CanEdit = canChange && IsOwn;
        CanDelete = canChange && IsOwn;
        // A check from below is turned on or off HERE by a manifest of this scope. Not the implicit build and test nobody
        // raised: they are the engine's own build and test checks, which run whatever a manifest says (WorkspaceChecks.Firing
        // leaves them out), so "Turn off" would have turned off nothing.
        CanToggle = canChange && (IsOwn || (below && check.Origin != CheckOrigin.Implicit));
        // The implicit build and test have no command to edit: what can be changed is how they decide (Plan Phase 3.1).
        CanRaise = canChange && check.Origin == CheckOrigin.Implicit;
        EditCommand = new RelayCommand(() => edit(this), () => CanEdit || CanRaise);
        RemoveCommand = new RelayCommand(() => remove(this), () => CanDelete);
        ToggleCommand = new RelayCommand(() => toggle(this), () => CanToggle);
    }

    public ResolvedCheck Check { get; }
    public CheckDefinition Definition => Check.Definition;
    public string Id => Check.Id;
    public string? Description => Definition.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Definition.Description);
    public bool Enabled => Definition.Enabled;

    /// <summary>Its file is this list's own.</summary>
    public bool IsOwn { get; }

    /// <summary>
    /// Its own manifest says no command: it changes how the check it shadows runs (or, implicit, runs through an ecosystem).
    /// Read from what the manifest said (ResolvedCheck.Said), never guessed from the merged definition.
    /// </summary>
    public bool IsRaise => !Check.CommandSaid;

    public string Origin => Check.Origin switch
    {
        CheckOrigin.Workspace => "This workspace",
        CheckOrigin.Global => "Global",
        _ => "Implicit"
    };

    /// <summary>What the check sits on: "raises the implicit one" for a manifest without a command, "replaces" for one with.</summary>
    public string Shadows => Check.Shadows is not { } below ? ""
        : $"{(IsRaise ? "raises" : "replaces")} the {below.ToString().ToLowerInvariant()} one";

    public bool HasShadows => Check.Shadows is not null;

    /// <summary>When it fires, how it decides, what it is about, how long it may take - one line.</summary>
    public string Meta
    {
        get
        {
            var parts = new List<string>
            {
                Definition.FireOn switch
                {
                    CheckFire.Write => "after a write",
                    CheckFire.Step => "at each step's end",
                    _ => "at the run's end"
                },
                Definition.Severity == CheckSeverity.Blocking ? "blocking" : "advisory"
            };
            // Not for a run check: it runs at the run's end whatever changed, and "on <globs>" said it ran only when they
            // did. Its matches are not used, and the store's note on its file says so.
            if (Definition.Matches.Count > 0 && Definition.FireOn != CheckFire.Run)
                parts.Add("on " + string.Join(", ", Definition.Matches));
            else if (Check.Ecosystems is { Count: > 0 } ecosystems)
                parts.Add("through " + string.Join(", ", ecosystems));
            parts.Add($"{Definition.TimeoutSeconds} s");
            if (Definition.Protects is { Count: > 0 } protects)
                parts.Add("protects " + string.Join(", ", protects));
            if (!Definition.Enabled)
                parts.Add("off");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The command it runs, or what runs it.</summary>
    public string Command => !string.IsNullOrWhiteSpace(Definition.Command) ? Definition.Command!
        : Check.Ecosystems is { Count: > 0 } ecosystems ? $"the engine's own, through {string.Join(", ", ecosystems)}" : "";

    /// <summary>What the store noted on its file - read, but not as written (a write check asked to block).</summary>
    public string Notes { get; }
    public bool HasNotes => Notes.Length > 0;

    public bool CanEdit { get; }
    public bool CanDelete { get; }
    public bool CanToggle { get; }
    public bool CanRaise { get; }

    /// <summary>Edit, or - on the implicit build and test - Raise to blocking: one button, its words by the row.</summary>
    public string EditText => CanRaise ? "Raise to blocking" : "Edit";
    public bool CanEditOrRaise => CanEdit || CanRaise;

    /// <summary>A manifest that shadows another is reset - the one below comes back - any other is deleted.</summary>
    public string RemoveText => Check.Shadows is null ? "Delete" : "Reset";

    public string ToggleText => Enabled ? "Turn off" : "Turn on";

    public RelayCommand EditCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ToggleCommand { get; }

    internal static CheckOrigin OriginOf(CheckScope scope) => scope == CheckScope.Workspace ? CheckOrigin.Workspace : CheckOrigin.Global;
}

/// <summary>
/// A check being added, edited or raised: the manifest's fields as a form. A raise has no command field - it changes how the
/// check below runs and keeps that one's command (CheckStore.Resolve) - and its id is the one it raises.
///
/// <para><b>Only what the person set is saved.</b> The form shows the check as it runs, and what is saved is the manifest
/// as it said itself (<paramref name="said"/>) with the fields the person changed. A field shown from the check below and
/// left as it was stays unsaid, so it follows that check; saved, it was a copy that stayed behind when that check changed.
/// The timeout field shows only a timeout the manifest itself said: shown from the merged check, it was saved back as said
/// and a change of when the check fires kept a timeout meant for the old one.</para>
/// </summary>
internal sealed class CheckDraft : ObservableObject
{
    private readonly CheckManifestSaid _said;
    private readonly (string Description, string Command, string Matches, string FireOn, string Severity, string Timeout,
        string Protects, bool Enabled) _shown;

    private string _id;
    private string _description;
    private string _command;
    private string _matches;
    private string _fireOn;
    private string _severity;
    private string _timeout;
    private string _protects;
    private bool _enabled;
    private string _problems = "";

    /// <param name="from">The check as it runs now, shown in the form; null for a new one.</param>
    /// <param name="said">What its own manifest says now; null for a new check or a raise of one below (nothing said yet).</param>
    /// <param name="severity">The severity the form opens on, when it is not the check's (a raise opens on Blocking).</param>
    public CheckDraft(CheckDefinition? from, CheckManifestSaid? said, bool isNew, bool hasCommand, Action save, Action cancel,
        CheckSeverity? severity = null)
    {
        IsNew = isNew;
        HasCommand = hasCommand;
        _said = said ?? new CheckManifestSaid();
        _id = from?.Id ?? "";
        _shown = (
            from?.Description ?? "",
            from?.Command ?? "",
            string.Join(Environment.NewLine, from?.Matches ?? []),
            (from?.FireOn ?? CheckFire.Step).ToString(),
            (from?.Severity ?? CheckSeverity.Advisory).ToString(),
            _said.TimeoutSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            string.Join(Environment.NewLine, from?.Protects ?? []),
            from?.Enabled ?? true);
        (_description, _command, _matches, _fireOn, _severity, _timeout, _protects, _enabled) = _shown;
        if (severity is { } opensOn)
            _severity = opensOn.ToString();
        SaveCommand = new RelayCommand(save);
        CancelCommand = new RelayCommand(cancel);
    }

    public bool IsNew { get; }

    /// <summary>Only a new check's id may be typed: an id is its file's name, and another id is another check.</summary>
    public bool IdEditable => IsNew;

    public bool HasCommand { get; }

    public string Title => IsNew ? "New check" : HasCommand ? $"Edit '{_id}'" : $"Change how '{_id}' runs";

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Command { get => _command; set => Set(ref _command, value); }

    /// <summary>The paths it is about, one glob a line (or separated by commas).</summary>
    public string Matches { get => _matches; set => Set(ref _matches, value); }

    public string FireOn { get => _fireOn; set => Set(ref _fireOn, value); }
    public string Severity { get => _severity; set => Set(ref _severity, value); }

    /// <summary>Seconds; empty, the default for when it fires (or the check below's).</summary>
    public string Timeout { get => _timeout; set => Set(ref _timeout, value); }

    /// <summary>The files it protects (Plan Phase 3.4), one glob a line.</summary>
    public string Protects { get => _protects; set => Set(ref _protects, value); }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public static IReadOnlyList<string> FireChoices { get; } = Enum.GetNames<CheckFire>();
    public static IReadOnlyList<string> SeverityChoices { get; } = Enum.GetNames<CheckSeverity>();

    /// <summary>Why the last Save did not save it - the store's own words, or the service's refusal.</summary>
    public string Problems
    {
        get => _problems;
        set
        {
            if (Set(ref _problems, value))
                OnPropertyChanged(nameof(HasProblems));
        }
    }

    public bool HasProblems => _problems.Length > 0;

    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// What the manifest will say: what it said, with each field the person changed in the form - or null and why not (a
    /// timeout that is no number, a choice that is none). A field emptied is unsaid.
    /// </summary>
    public CheckManifestSaid? ToSaid(out string? problem)
    {
        problem = null;
        var said = _said;
        if (_description != _shown.Description)
            said = said with { Description = Text(_description) };
        if (HasCommand && _command != _shown.Command)
            said = said with { Command = Text(_command) };
        if (_matches != _shown.Matches)
            said = said with { Matches = Globs(_matches) is { Count: > 0 } matches ? matches : null };
        if (_protects != _shown.Protects)
            said = said with { Protects = Globs(_protects) is { Count: > 0 } protects ? protects : null };
        if (_enabled != _shown.Enabled)
            said = said with { Enabled = _enabled };
        if (_fireOn != _shown.FireOn)
        {
            if (!Enum.TryParse<CheckFire>(_fireOn, ignoreCase: true, out var fire) || !Enum.IsDefined(fire))
                problem = $"'{_fireOn}' is not when a check can fire (write, step or run).";
            else
                said = said with { FireOn = fire };
        }
        if (_severity != _shown.Severity)
        {
            if (!Enum.TryParse<CheckSeverity>(_severity, ignoreCase: true, out var severity) || !Enum.IsDefined(severity))
                problem = $"'{_severity}' is not a severity (advisory or blocking).";
            else
                said = said with { Severity = severity };
        }
        if (_timeout != _shown.Timeout)
        {
            if (_timeout.Trim() is not { Length: > 0 } seconds)
                said = said with { TimeoutSeconds = null };
            else if (int.TryParse(seconds, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
                said = said with { TimeoutSeconds = n };
            else
                problem = "The timeout is a whole number of seconds.";
        }
        return problem is null ? said : null;
    }

    private static string? Text(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> Globs(string text)
        => text.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// A Checks list (Plan Phase 3.5): the global one in the settings, a workspace's from its card - the merged checks with where
/// each comes from and what it shadows, the manifests the store skipped and its notes beside their files, why there are no
/// implicit build and test where there are none, and Add, Edit, Delete, Turn on/off and Raise to blocking.
///
/// <para><b>Files, saved at once</b>, as templates are: a manifest is a file in the global folder or the workspace's
/// <c>.enactive/checks</c>, written when its form is saved, which the settings' Cancel does not undo. A host that may not
/// change them gives no <see cref="ICheckChanges"/> (a Viewer's page): the list is read-only.</para>
/// </summary>
internal sealed class ChecksViewModel : ObservableObject
{
    private readonly CheckStore _store;
    private readonly ICheckChanges? _changes;
    private CheckDraft? _draft;
    private string _said = "";
    private string _notNow = "";
    private string _noImplicit = "";

    public ChecksViewModel(CheckStore store, CheckScope scope, ICheckChanges? changes)
    {
        _store = store;
        Scope = scope;
        _changes = changes;
        AddCommand = new RelayCommand(Add, () => CanChange);
        Refresh();
    }

    public CheckScope Scope { get; }

    /// <summary>Whether this list changes the files at all (the host's say), not whether it may now (<see cref="NotNow"/>).</summary>
    public bool CanChange => _changes is not null;

    public string Heading => Scope == CheckScope.Workspace ? "This workspace's checks" : "Global checks";

    /// <summary>Where this list's files are - the folder a person finds them in, and versions them in.</summary>
    public string Folder => Scope == CheckScope.Workspace ? _store.WorkspaceFolder ?? "" : _store.GlobalFolder;

    public ObservableCollection<CheckRow> Checks { get; } = new();

    /// <summary>The manifests skipped, and notes on files that are no row of the list.</summary>
    public ObservableCollection<CheckFileNote> Files { get; } = new();

    public bool HasFiles => Files.Count > 0;

    /// <summary>Why there is no implicit build and test here - said instead of empty rows (Plan Phase 3.1).</summary>
    public string NoImplicit { get => _noImplicit; private set { if (Set(ref _noImplicit, value)) OnPropertyChanged(nameof(HasNoImplicit)); } }
    public bool HasNoImplicit => _noImplicit.Length > 0;

    /// <summary>Why nothing may be changed in this list now - a run working in the workspace - or empty.</summary>
    public string NotNow { get => _notNow; private set { if (Set(ref _notNow, value)) OnPropertyChanged(nameof(HasNotNow)); } }
    public bool HasNotNow => _notNow.Length > 0;

    /// <summary>What the last change came to: saved, deleted, or why not.</summary>
    public string Said { get => _said; private set { if (Set(ref _said, value)) OnPropertyChanged(nameof(HasSaid)); } }
    public bool HasSaid => _said.Length > 0;

    /// <summary>The form open on a check, or null.</summary>
    public CheckDraft? Draft
    {
        get => _draft;
        private set
        {
            if (Set(ref _draft, value))
                OnPropertyChanged(nameof(IsEditing));
        }
    }

    public bool IsEditing => _draft is not null;

    public RelayCommand AddCommand { get; }

    /// <summary>
    /// Asks a yes/no question before a delete or a reset - (headline, detail, the confirm button's word: "Delete" or
    /// "Reset", which is what pressing it does). The host answers it; none asked, it is done.
    /// </summary>
    public event Func<string, string, string, Task<bool>>? ConfirmRequested;

    /// <summary>The list read again from the files: after every change, and when a host knows the files changed.</summary>
    public void Refresh()
    {
        var inventory = _store.Inventory();
        var notes = inventory.Notes;

        Checks.Clear();
        foreach (var check in inventory.Checks)
        {
            var onFile = notes.Where(n => check.File is { } file && string.Equals(n.File, file, StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Message).ToList();
            Checks.Add(new CheckRow(check, Scope, CanChange, onFile, Edit, row => _ = RemoveAsync(row), Toggle));
        }

        Files.Clear();
        foreach (var problem in inventory.Problems)
            Files.Add(Note(problem, isProblem: true));
        foreach (var note in notes.Where(n => !inventory.Checks.Any(c => string.Equals(c.File, n.File, StringComparison.OrdinalIgnoreCase))))
            Files.Add(Note(note, isProblem: false));
        OnPropertyChanged(nameof(HasFiles));

        NoImplicit = inventory.NoImplicitChecks ?? "";
        NotNow = _changes?.WhyNot(Scope) ?? "";
    }

    private static CheckFileNote Note(CheckProblem problem, bool isProblem)
        => new(Path.GetFileName(problem.File) is { Length: > 0 } name ? name : problem.File,
            problem.Scope == CheckScope.Workspace ? "this workspace" : "global", problem.Message, isProblem);

    private void Add()
    {
        if (!CanChange)
            return;
        Draft = new CheckDraft(null, null, isNew: true, hasCommand: true, SaveDraft, CloseDraft);
    }

    /// <summary>
    /// The form for a new check with its id and description filled in - a lesson made into a check (Plan Phase 5.4). The
    /// command is left empty for the person: the store refuses a new check without one, so nothing runs until they write
    /// it, and nothing here guesses it.
    /// </summary>
    public void AddFrom(string id, string description)
    {
        Add();
        if (Draft is { } draft)
        {
            draft.Id = id;
            draft.Description = description;
            // At the run's end: a check on a step's changes must name the paths it is about (matches), and a lesson says
            // what went wrong, not where - a guess at the paths would be the scaffold inventing what the person decides.
            draft.FireOn = nameof(CheckFire.Run);
        }
    }

    /// <summary>The id of the check the form last saved, or null - for what opened the form to hear it was kept.</summary>
    public string? LastSaved
    {
        get => _lastSaved;
        private set => Set(ref _lastSaved, value);
    }

    private string? _lastSaved;

    private void Edit(CheckRow row)
    {
        if (!(row.CanEdit || row.CanRaise))
            return;
        // Raised: the implicit check as it runs now, asked to block - nothing said yet. One of this list's own is edited as
        // its manifest says itself.
        Draft = row.CanRaise
            ? new CheckDraft(row.Definition, null, isNew: false, hasCommand: false, SaveDraft, CloseDraft, CheckSeverity.Blocking)
            : new CheckDraft(row.Definition, row.Check.Said, isNew: false, hasCommand: !row.IsRaise, SaveDraft, CloseDraft);
    }

    private void SaveDraft()
    {
        if (_draft is not { } draft || _changes is null)
            return;
        if (draft.ToSaid(out var problem) is not { } said)
        {
            draft.Problems = problem ?? "";
            return;
        }
        var id = draft.Id.Trim();
        if (_changes.Save(id, said, Scope) is { } refused)
        {
            draft.Problems = refused;
            return;
        }
        Said = $"Saved '{id}' to {Folder}.";
        LastSaved = id;
        CloseDraft();
        Refresh();
    }

    private void CloseDraft() => Draft = null;

    private void Toggle(CheckRow row)
    {
        if (!row.CanToggle || _changes is null)
            return;
        var on = !row.Enabled;
        // One of this list's own says what it said, turned on or off. One from below is turned off HERE by a manifest that
        // says that and nothing else: anything more would be a copy of that check, left behind when it changes.
        var said = row.IsOwn ? (row.Check.Said ?? new CheckManifestSaid()) with { Enabled = on } : new CheckManifestSaid(Enabled: on);
        Said = _changes.Save(row.Id, said, Scope) ?? $"'{row.Id}' is {(on ? "on" : "off")}.";
        Refresh();
    }

    private async Task RemoveAsync(CheckRow row)
    {
        if (!row.CanDelete || _changes is null)
            return;
        var reset = row.Check.Shadows is not null;
        if (ConfirmRequested is { } confirm
            && !await confirm(reset ? $"Reset '{row.Id}'?" : $"Delete '{row.Id}'?",
                (reset ? $"Its file is deleted and the {row.Check.Shadows.ToString()!.ToLowerInvariant()} '{row.Id}' applies again. "
                       : "Its file is deleted, and no run checks it any more. ")
                + "It is a file, deleted at once: no Cancel brings it back.",
                reset ? "Reset" : "Delete"))
            return;
        Said = _changes.Delete(row.Id, Scope) ?? (reset ? $"Reset '{row.Id}'." : $"Deleted '{row.Id}'.");
        Refresh();
    }
}
