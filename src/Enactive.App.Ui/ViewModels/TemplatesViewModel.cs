namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Media;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Workspace;

/// <summary>
/// One input in the form a template asks for.
///
/// <para>The form is GENERATED from <see cref="TemplateParameter"/> rather than written per
/// template, which is the whole reason a template can be added as a JSON file without touching the
/// app. A single row carries every control kind and shows the one its type calls for: Avalonia's
/// template selection would do the same job with three more files, and the flags read better next
/// to the value they belong to.</para>
/// </summary>
internal sealed class TemplateParameterInput : ObservableObject
{
    private string _value;
    private bool _flag;

    public TemplateParameterInput(TemplateParameter parameter)
    {
        Parameter = parameter;
        _value = parameter.Default ?? string.Empty;
        _flag = bool.TryParse(parameter.Default, out var b) && b;

        Choices = parameter.Choices is { Count: > 0 } choices
            ? new ObservableCollection<string>(choices)
            : new ObservableCollection<string>();

        if (parameter.Type == TemplateParameterType.Choice && _value.Length == 0 && Choices.Count > 0)
            _value = Choices[0];
    }

    public TemplateParameter Parameter { get; }

    public string Id => Parameter.Id;
    public string Label => Parameter.Required ? Parameter.Name : Parameter.Name + " (optional)";
    public string? Hint => Parameter.Description;
    public bool HasHint => !string.IsNullOrWhiteSpace(Parameter.Description);

    public ObservableCollection<string> Choices { get; }

    public bool IsMultiline => Parameter.Type == TemplateParameterType.MultilineText;
    public bool IsBoolean => Parameter.Type == TemplateParameterType.Boolean;
    public bool IsChoice => Parameter.Type == TemplateParameterType.Choice;

    /// <summary>Everything that is neither a tick nor a list: text, a path, a number.</summary>
    public bool IsLine => !IsMultiline && !IsBoolean && !IsChoice;

    public string Value { get => _value; set => Set(ref _value, value); }
    public bool Flag { get => _flag; set => Set(ref _flag, value); }

    /// <summary>What this input contributes to the resolver, in the string form it expects.</summary>
    public string Supplied => IsBoolean ? (Flag ? "true" : "false") : Value.Trim();
}

/// <summary>
/// The template library: what is available here, what the selected one asks for, and whether it can
/// be run as filled in.
///
/// <para>Nothing about running lives here. The window hands a resolved specification back and the
/// main window starts the run, because starting one needs the providers, the tools and the artifact
/// store - none of which a library has any business holding.</para>
/// </summary>
internal sealed class TemplatesViewModel : ObservableObject
{
    // Placeholders until SetWorkspace runs, which the constructor does before anything can read
    // them. The policy starts at the NARROWEST there is rather than at the permissive default: a
    // permission field whose placeholder allows everything is one refactor away from being the
    // value something actually used.
    private WorkspaceInfo _workspace = new(Guid.Empty, "workspace", string.Empty);
    private PermissionPolicy _workspacePolicy =
        new(PermissionLevel.Observe, Array.Empty<string>(), Array.Empty<string>());
    private string _workspaceRoot = string.Empty;

    private TemplateListItem? _selected;
    private string _problems = string.Empty;
    private bool _canRun;

    public TemplatesViewModel(string? workspaceRoot, PermissionPolicy workspacePolicy)
        => SetWorkspace(workspaceRoot, workspacePolicy);

    public string WorkspaceRoot { get => _workspaceRoot; private set => Set(ref _workspaceRoot, value); }

    /// <summary>
    /// Points the library at a workspace and rereads it.
    ///
    /// <para>The policy comes with it: a template may only NARROW what the workspace allows, so the
    /// summary line under each template ("runs at Execute · may not use…") is a statement about a
    /// particular workspace's autonomy. Moving the library to another folder without its policy
    /// would leave that line describing the one you left.</para>
    /// </summary>
    public void SetWorkspace(string? workspaceRoot, PermissionPolicy workspacePolicy)
    {
        WorkspaceRoot = workspaceRoot ?? string.Empty;
        _workspace = string.IsNullOrWhiteSpace(workspaceRoot)
            ? new WorkspaceInfo(Guid.Empty, "workspace", string.Empty)
            : WorkspaceInfo.For(workspaceRoot);
        _workspacePolicy = workspacePolicy;

        Reload();
    }

    /// <summary>
    /// Rereads the library for THIS workspace.
    ///
    /// <para>Called when the window opens and again whenever the main window changes workspace. A
    /// library left over from the folder you were in five minutes ago offers a project's own Release
    /// Check for a project it knows nothing about, and the run would be resolved against the
    /// workspace you are actually in — the list and the truth would disagree, silently.</para>
    /// </summary>
    public void Reload()
    {
        var keep = Selected?.Template.Id;

        Templates.Clear();
        foreach (var entry in new TemplateStore(
                     string.IsNullOrWhiteSpace(WorkspaceRoot) ? null : WorkspaceRoot).Inventory())
            Templates.Add(new TemplateListItem(entry));

        // The same template by id, if this workspace also has one - not the same row by position,
        // which after a workspace change is a different template wearing the old one's place.
        Selected = Templates.FirstOrDefault(
                       t => string.Equals(t.Template.Id, keep, StringComparison.OrdinalIgnoreCase))
                   ?? Templates.FirstOrDefault();
    }

    public ObservableCollection<TemplateListItem> Templates { get; } = new();

    public ObservableCollection<TemplateParameterInput> Parameters { get; } = new();

    public TemplateListItem? Selected
    {
        get => _selected;
        set
        {
            var previous = _selected;
            if (!Set(ref _selected, value))
                return;

            if (previous is not null) previous.IsSelected = false;
            if (value is not null) value.IsSelected = true;

            Parameters.Clear();
            if (value is not null)
                foreach (var parameter in value.Template.ParameterList)
                {
                    var input = new TemplateParameterInput(parameter);
                    // Revalidate as it is typed, so Run is not a button that turns out to be a
                    // refusal - the reason appears next to the field that caused it.
                    input.PropertyChanged += (_, _) => Revalidate();
                    Parameters.Add(input);
                }

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedGoal));
            OnPropertyChanged(nameof(SelectedSummary));
            OnPropertyChanged(nameof(HasParameters));
            Revalidate();
        }
    }

    public bool HasSelection => Selected is not null;
    public bool HasParameters => Parameters.Count > 0;

    public string SelectedGoal => Selected?.Template.Goal ?? string.Empty;

    /// <summary>
    /// What running this would actually mean, in one line: how much it may do, whether it is
    /// checked, and how far it may go. These are the parts of a template a person needs before
    /// pressing Run, and they are exactly the parts that are easy to leave invisible.
    /// </summary>
    public string SelectedSummary
    {
        get
        {
            if (Selected is not { } item)
                return string.Empty;

            var t = item.Template;
            var parts = new List<string>();

            var effective = TemplateResolution.Narrow(_workspacePolicy, t.Ceiling);
            parts.Add("runs at " + effective.Level);

            if (effective.Deny.Count > 0)
                parts.Add("may not use " + string.Join(", ", effective.Deny));

            parts.Add(t.CriteriaList.Count == 0
                ? "no success checks"
                : t.CriteriaList.Count + " success check(s)");

            if (t.LimitsOrNone.MaxSteps is { } steps)
                parts.Add($"at most {steps} step(s)");
            if (t.LimitsOrNone.MaxTokens is { } tokens)
                parts.Add($"at most {tokens} token(s)");
            if (t.LimitsOrNone.MaxDurationSeconds is { } seconds)
                parts.Add($"at most {seconds} second(s)");

            parts.Add(t.ReviewRequired ? "reviewed" : "not reviewed");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Why it cannot be run as filled in, or empty when it can.</summary>
    public string Problems { get => _problems; private set => Set(ref _problems, value); }
    public bool HasProblems => Problems.Length > 0;

    public bool CanRun { get => _canRun; private set => Set(ref _canRun, value); }

    /// <summary>
    /// The specification as filled in, or null with <see cref="Problems"/> saying why not. The same
    /// resolver the engine uses - the form does not get its own idea of what is valid.
    /// </summary>
    public ResolvedTaskSpec? Resolve()
    {
        if (Selected is not { } item)
            return null;

        var values = Parameters.ToDictionary(p => p.Id, p => p.Supplied, StringComparer.OrdinalIgnoreCase);
        var result = TemplateResolution.Resolve(item.Template, _workspace, _workspacePolicy, values);
        return result.Spec;
    }

    private void Revalidate()
    {
        if (Selected is null)
        {
            Problems = string.Empty;
            CanRun = false;
            OnPropertyChanged(nameof(HasProblems));
            return;
        }

        var values = Parameters.ToDictionary(p => p.Id, p => p.Supplied, StringComparer.OrdinalIgnoreCase);
        var result = TemplateResolution.Resolve(Selected.Template, _workspace, _workspacePolicy, values);

        Problems = result.Ok
            ? string.Empty
            : string.Join("\n", result.Problems.Select(p => "• " + p.Message));

        // A workspace is not optional: a run happens in a folder, and resolving one against an empty
        // path would produce a specification pointing at nothing.
        CanRun = result.Ok && WorkspaceRoot.Length > 0;

        if (!CanRun && result.Ok && WorkspaceRoot.Length == 0)
            Problems = "• Open a workspace first — a template runs in a folder.";

        OnPropertyChanged(nameof(HasProblems));
    }
}

/// <summary>A row in the library. Carries the template so the window never re-reads the store.</summary>
internal sealed class TemplateListItem : ObservableObject
{
    private bool _isSelected;

    public TemplateListItem(StoredTemplate entry)
    {
        Entry = entry;
        Template = entry.Template;

        Meta = string.Join(" · ", new[]
        {
            Template.Category,
            Where,
            entry.Shadows is { } shadowed ? $"replaces the {shadowed.ToString().ToLowerInvariant()} one" : null,
            "v" + Template.Version
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public StoredTemplate Entry { get; }
    public TaskTemplate Template { get; }

    public string Name => Template.Name;
    public string Meta { get; }
    public string Description => Template.Description ?? string.Empty;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Template.Description);

    /// <summary>Which of the three places this copy came from, in a word.</summary>
    public string Where => Entry.Origin switch
    {
        TemplateOrigin.Builtin => "built-in",
        TemplateOrigin.Global => "global",
        _ => "this workspace"
    };

    /// <summary>
    /// The one you are about to run. Set by the list, because only the list knows.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) OnPropertyChanged(nameof(EdgeBrush)); }
    }

    /// <summary>
    /// The card's left edge: amber for the one that is selected, and otherwise by where the
    /// template lives — grey for a built-in, blue for one of yours, green for one that belongs to
    /// this project. The three place colours are the ones the Settings list uses, because it is the
    /// same distinction and a person should only have to learn it once.
    ///
    /// <para>Selection wins over place on the selected row, and that is the right way round: where
    /// a template comes from is still a word in the line underneath, while "this is the one whose
    /// form I am filling in" is only ever shown by the highlight. Losing the second to preserve the
    /// first would be trading the urgent fact for the durable one.</para>
    ///
    /// <para>It is a binding rather than a style because a style setter cannot win against one: a
    /// bound BorderBrush is a local value, and <c>ListBoxItem:selected</c> would never take effect.</para>
    /// </summary>
    public IBrush EdgeBrush => IsSelected
        ? Brand.Amber
        : Entry.Origin switch
        {
            TemplateOrigin.Builtin => Brand.Line,
            TemplateOrigin.Global => Brand.Info,
            _ => Brand.Success
        };
}
