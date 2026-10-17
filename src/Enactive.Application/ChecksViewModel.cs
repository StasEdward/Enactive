namespace Enactive.Application;

using System.Collections.ObjectModel;
using Enactive.Application.Mvvm;
using Enactive.Core.Checks;
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

    string? Save(CheckDefinition check, CheckScope scope);

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

    public static string NotSaved(IEnumerable<string> problems) => "Not saved - " + string.Join("; ", problems);
}

/// <summary>
/// The window's: straight to the check files, as its templates are - the store's own validation first, so nothing the store
/// would skip is written - and nothing written to a workspace while a run is working in it (<paramref name="runIn"/>).
/// </summary>
/// <param name="workspaceRoot">The workspace the store's workspace checks are of; null for a store of global checks only.</param>
/// <param name="runIn">The workspace a run is working in now, or null: the window's foreground run.</param>
internal sealed class StoreCheckChanges(CheckStore store, string? workspaceRoot, Func<string?>? runIn = null) : ICheckChanges
{
    /// <summary>
    /// Only a workspace's own checks are refused while a run works in it: the run puts back THAT folder (CheckManifestGuard),
    /// and it took its global checks into its snapshot when it started, so a global change is the next run's.
    /// </summary>
    public string? WhyNot(CheckScope scope)
        => scope == CheckScope.Workspace && workspaceRoot is { Length: > 0 } root && runIn?.Invoke() is { Length: > 0 } running
           && string.Equals(WorkspaceRegistry.Normalise(running), WorkspaceRegistry.Normalise(root), StringComparison.OrdinalIgnoreCase)
            ? CheckChangeWords.RunActive
            : null;

    public string? Save(CheckDefinition check, CheckScope scope)
    {
        if (WhyNot(scope) is { } why)
            return why;
        if (store.Validate(check, scope) is { Count: > 0 } problems)
            return CheckChangeWords.NotSaved(problems);
        try
        {
            store.Save(check, scope);
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
    public CheckRow(ResolvedCheck check, CheckScope scope, bool canChange, bool raise, IReadOnlyList<string> notes,
        Action<CheckRow> edit, Action<CheckRow> remove, Action<CheckRow> toggle)
    {
        Check = check;
        IsRaise = raise;
        Notes = string.Join(" ", notes);
        var own = OriginOf(scope) == check.Origin;
        // Above this list's scope - a workspace check in the global list - is another list's to change.
        var below = check.Origin < OriginOf(scope);
        CanEdit = canChange && own;
        CanDelete = canChange && own;
        CanToggle = canChange && (own || below);
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

    /// <summary>It has no command of its own: it changes how the check it shadows runs (or, implicit, runs through an ecosystem).</summary>
    public bool IsRaise { get; }

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
            if (Definition.Matches.Count > 0)
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
/// </summary>
internal sealed class CheckDraft : ObservableObject
{
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

    public CheckDraft(CheckDefinition? from, bool isNew, bool hasCommand, Action save, Action cancel)
    {
        IsNew = isNew;
        HasCommand = hasCommand;
        _id = from?.Id ?? "";
        _description = from?.Description ?? "";
        _command = from?.Command ?? "";
        _matches = string.Join(Environment.NewLine, from?.Matches ?? []);
        _fireOn = (from?.FireOn ?? CheckFire.Step).ToString();
        _severity = (from?.Severity ?? CheckSeverity.Advisory).ToString();
        _timeout = from is null ? "" : from.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _protects = string.Join(Environment.NewLine, from?.Protects ?? []);
        _enabled = from?.Enabled ?? true;
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

    /// <summary>Seconds; empty, the default for when it fires.</summary>
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

    /// <summary>The check the form says, or null and why not (a timeout that is no number, a choice that is none).</summary>
    public CheckDefinition? ToDefinition(out string? problem)
    {
        problem = null;
        if (!Enum.TryParse<CheckFire>(_fireOn, ignoreCase: true, out var fire) || !Enum.IsDefined(fire))
            problem = $"'{_fireOn}' is not when a check can fire (write, step or run).";
        else if (!Enum.TryParse<CheckSeverity>(_severity, ignoreCase: true, out var severity) || !Enum.IsDefined(severity))
            problem = $"'{_severity}' is not a severity (advisory or blocking).";
        else if (_timeout.Trim() is { Length: > 0 } said
                 && !int.TryParse(said, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            problem = "The timeout is a whole number of seconds.";
        else
        {
            var seconds = _timeout.Trim() is { Length: > 0 } t
                ? int.Parse(t, System.Globalization.CultureInfo.InvariantCulture)
                : CheckLimits.DefaultTimeoutFor(fire);
            var protects = Globs(_protects);
            return new CheckDefinition(_id.Trim(), string.IsNullOrWhiteSpace(_description) ? null : _description.Trim(),
                HasCommand && !string.IsNullOrWhiteSpace(_command) ? _command.Trim() : null,
                Globs(_matches), fire, severity, seconds, protects.Count == 0 ? null : protects)
            { Enabled = _enabled };
        }
        return null;
    }

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
    private CheckRow? _editing;
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

    /// <summary>Asks a yes/no question before a delete; the host answers it. None asked, it is done.</summary>
    public event Func<string, string, Task<bool>>? ConfirmRequested;

    /// <summary>The list read again from the files: after every change, and when a host knows the files changed.</summary>
    public void Refresh()
    {
        var inventory = _store.Inventory();
        var notes = inventory.Notes;
        // The check each workspace manifest without a command raises keeps that check's command: to tell a raise of a
        // global check from a workspace check of its own, its command is compared with the global one's.
        var global = Scope == CheckScope.Workspace && _store.WorkspaceFolder is not null
            ? new CheckStore(null, _store.GlobalFolder).Inventory().Checks
            : [];

        Checks.Clear();
        foreach (var check in inventory.Checks)
        {
            var onFile = notes.Where(n => check.File is { } file && string.Equals(n.File, file, StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Message).ToList();
            Checks.Add(new CheckRow(check, Scope, CanChange, IsRaise(check, global), onFile, Edit, row => _ = RemoveAsync(row), Toggle));
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

    private static bool IsRaise(ResolvedCheck check, IReadOnlyList<ResolvedCheck> global)
        => check.RunsThroughEcosystem
           || (check.Shadows == CheckOrigin.Global
               && global.FirstOrDefault(g => string.Equals(g.Id, check.Id, StringComparison.OrdinalIgnoreCase)) is { } below
               && string.Equals(below.Definition.Command, check.Definition.Command, StringComparison.Ordinal));

    private static CheckFileNote Note(CheckProblem problem, bool isProblem)
        => new(Path.GetFileName(problem.File) is { Length: > 0 } name ? name : problem.File,
            problem.Scope == CheckScope.Workspace ? "this workspace" : "global", problem.Message, isProblem);

    private void Add()
    {
        if (!CanChange)
            return;
        _editing = null;
        Draft = new CheckDraft(null, isNew: true, hasCommand: true, SaveDraft, CloseDraft);
    }

    private void Edit(CheckRow row)
    {
        if (!(row.CanEdit || row.CanRaise))
            return;
        _editing = row;
        // Raised: the implicit check as it runs now, asked to block. A raise of it already made is edited as itself.
        var from = row.CanRaise ? row.Definition with { Severity = CheckSeverity.Blocking } : row.Definition;
        Draft = new CheckDraft(from, isNew: false, hasCommand: !row.IsRaise, SaveDraft, CloseDraft);
    }

    private void SaveDraft()
    {
        if (_draft is not { } draft || _changes is null)
            return;
        if (draft.ToDefinition(out var problem) is not { } check)
        {
            draft.Problems = problem ?? "";
            return;
        }
        if (_changes.Save(check, Scope) is { } refused)
        {
            draft.Problems = refused;
            return;
        }
        Said = $"Saved '{check.Id}' to {Folder}.";
        CloseDraft();
        Refresh();
    }

    private void CloseDraft()
    {
        Draft = null;
        _editing = null;
    }

    private void Toggle(CheckRow row)
    {
        if (!row.CanToggle || _changes is null)
            return;
        var own = CheckRow.OriginOf(Scope) == row.Check.Origin;
        // One of this list's own keeps what it is - its command, or none for a raise. One from below is turned off HERE by a
        // manifest without a command, which changes that check for this scope and leaves its own file as it is.
        var check = row.Definition with
        {
            Command = own && !row.IsRaise ? row.Definition.Command : null,
            Enabled = !row.Enabled
        };
        Said = _changes.Save(check, Scope) ?? $"'{row.Id}' is {(check.Enabled ? "on" : "off")}.";
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
                + "It is a file, deleted at once: no Cancel brings it back."))
            return;
        Said = _changes.Delete(row.Id, Scope) ?? (reset ? $"Reset '{row.Id}'." : $"Deleted '{row.Id}'.");
        Refresh();
    }
}
