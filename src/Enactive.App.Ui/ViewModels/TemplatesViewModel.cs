namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
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
    private readonly WorkspaceInfo _workspace;
    private readonly PermissionPolicy _workspacePolicy;

    private TemplateListItem? _selected;
    private string _problems = string.Empty;
    private bool _canRun;

    public TemplatesViewModel(string? workspaceRoot, PermissionPolicy workspacePolicy)
    {
        WorkspaceRoot = workspaceRoot ?? string.Empty;
        _workspace = string.IsNullOrWhiteSpace(workspaceRoot)
            ? new WorkspaceInfo(Guid.Empty, "workspace", string.Empty)
            : WorkspaceInfo.For(workspaceRoot);
        _workspacePolicy = workspacePolicy;

        var store = new TemplateStore(workspaceRoot);
        foreach (var template in store.Load())
            Templates.Add(new TemplateListItem(template));

        Selected = Templates.FirstOrDefault();
    }

    public string WorkspaceRoot { get; }

    public ObservableCollection<TemplateListItem> Templates { get; } = new();

    public ObservableCollection<TemplateParameterInput> Parameters { get; } = new();

    public TemplateListItem? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;

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
internal sealed class TemplateListItem
{
    public TemplateListItem(TaskTemplate template)
    {
        Template = template;
        Meta = string.Join(" · ", new[]
        {
            template.Category,
            template.Builtin ? "built-in" : "yours",
            "v" + template.Version
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public TaskTemplate Template { get; }

    public string Name => Template.Name;
    public string Meta { get; }
    public string Description => Template.Description ?? string.Empty;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Template.Description);
}
