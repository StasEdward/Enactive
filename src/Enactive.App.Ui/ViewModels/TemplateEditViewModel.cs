namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Workspace;

/// <summary>One parameter of the template being edited.</summary>
internal sealed class ParameterRow : ObservableObject
{
    private string _id, _name, _description, _default, _choices;
    private TemplateParameterType _type;
    private bool _required;

    public ParameterRow(TemplateParameter p)
    {
        _id = p.Id;
        _name = p.Name;
        _type = p.Type;
        _required = p.Required;
        _default = p.Default ?? "";
        _description = p.Description ?? "";
        _choices = p.Choices is null ? "" : string.Join(", ", p.Choices);
    }

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public TemplateParameterType Type
    {
        get => _type;
        set { Set(ref _type, value); OnPropertyChanged(nameof(IsChoice)); }
    }
    public bool Required { get => _required; set => Set(ref _required, value); }
    public string Default { get => _default; set => Set(ref _default, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Choices { get => _choices; set => Set(ref _choices, value); }

    public bool IsChoice => Type == TemplateParameterType.Choice;
    public IReadOnlyList<TemplateParameterType> Types { get; } = Enum.GetValues<TemplateParameterType>();

    public RelayCommand? RemoveCommand { get; set; }

    public TemplateParameter ToParameter() => new(
        Id.Trim(), Name.Trim(), Type, Required,
        string.IsNullOrWhiteSpace(Default) ? null : Default,
        Type == TemplateParameterType.Choice && !string.IsNullOrWhiteSpace(Choices)
            ? Choices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null,
        string.IsNullOrWhiteSpace(Description) ? null : Description);
}

/// <summary>One success criterion of the template being edited.</summary>
internal sealed class CriterionRow : ObservableObject
{
    private string _name, _command, _exit;
    private bool _required;

    public CriterionRow(SuccessCriterionDefinition c)
    {
        _name = c.Name;
        _command = c.Command;
        // All of them, so that saving a template keeps a check that passes on "0, 1" - showing only the first
        // would quietly turn it back into one that demands 0.
        _exit = c.PassingExitCodesText;
        _required = c.Required;
    }

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Command { get => _command; set => Set(ref _command, value); }
    public string ExitCode { get => _exit; set => Set(ref _exit, value); }
    public bool Required { get => _required; set => Set(ref _required, value); }

    public RelayCommand? RemoveCommand { get; set; }

    public SuccessCriterionDefinition ToCriterion()
    {
        var codes = ExitCode.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => int.TryParse(text, out var code) ? (int?)code : null)
            .OfType<int>().Distinct().ToArray();
        return new(Name.Trim(), Command.Trim(), codes.Length > 0 ? codes[0] : 0, Required)
        {
            ExpectedExitCodes = codes.Length > 1 ? codes : null
        };
    }
}

/// <summary>
/// One tool, and what this template says about it.
///
/// <para>The list comes from the tools this build actually registers, never from names typed here.
/// A hand-kept list is how <c>create_dir</c> ended up in three templates' deny lists while the tool
/// is called <c>create_directory</c> — a restriction written down, displayed, and connected to
/// nothing.</para>
/// </summary>
internal sealed class ToolPermissionRow : ObservableObject
{
    private bool _deny, _ask;

    public ToolPermissionRow(string name, bool deny, bool ask)
    {
        Name = name;
        _deny = deny;
        _ask = ask;
    }

    public string Name { get; }

    /// <summary>Denied and ask-before at once is denied, so setting one clears the other.</summary>
    public bool Deny
    {
        get => _deny;
        set { Set(ref _deny, value); if (value && _ask) { _ask = false; OnPropertyChanged(nameof(Ask)); } }
    }

    public bool Ask
    {
        get => _ask;
        set { Set(ref _ask, value); if (value && _deny) { _deny = false; OnPropertyChanged(nameof(Deny)); } }
    }
}

/// <summary>
/// The template editor: a form for what people change, and the whole thing as JSON for what a form
/// cannot reach.
///
/// <para>The two views are the same template, so they are synchronised when the tab changes rather
/// than left to drift: the form writes the JSON on the way in, and the JSON is parsed back on the
/// way out. JSON that will not parse refuses to switch and says why — silently discarding what
/// somebody typed is the worse of the two options by a distance.</para>
///
/// <para><b>Save writes a file, immediately.</b> Templates are not part of the settings document, so
/// the Cancel in the Settings window does not undo them. The pane says so.</para>
/// </summary>
internal sealed class TemplateEditViewModel : ObservableObject
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Action<TaskTemplate, TemplateScope> _saved;
    private readonly TaskTemplate _original;

    public event Action? CloseRequested;

    public TemplateEditViewModel(
        TaskTemplate draft, bool idEditable, IReadOnlyList<TemplateScope> scopes,
        IReadOnlyList<string> toolNames, Action<TaskTemplate, TemplateScope> saved)
    {
        _original = draft;
        _saved = saved;
        IdEditable = idEditable;
        Scopes = scopes;
        _scope = scopes[0];

        _id = draft.Id;
        _name = draft.Name;
        _description = draft.Description ?? "";
        _category = draft.Category ?? "";
        _goal = draft.Goal;
        _workerId = draft.WorkerId ?? "";
        _reviewRequired = draft.ReviewRequired;

        _maxSteps = draft.LimitsOrNone.MaxSteps?.ToString() ?? "";
        _maxTokens = draft.LimitsOrNone.MaxTokens?.ToString() ?? "";
        _maxSeconds = draft.LimitsOrNone.MaxDurationSeconds?.ToString() ?? "";
        _maxLevel = draft.Ceiling.MaxLevel;

        foreach (var p in draft.ParameterList)
            Parameters.Add(Track(new ParameterRow(p)));
        foreach (var c in draft.CriteriaList)
            Criteria.Add(Track(new CriterionRow(c)));

        // Every tool this build registers, plus any name the template already carries that no longer
        // exists - so a restriction on a tool that has been renamed is visible rather than dropped
        // silently the first time somebody opens the editor.
        var known = new List<string>(toolNames);
        foreach (var named in draft.Ceiling.DenyList.Concat(draft.Ceiling.AskBeforeList))
            if (!known.Contains(named, StringComparer.OrdinalIgnoreCase))
                known.Add(named);

        foreach (var tool in known.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            Tools.Add(new ToolPermissionRow(
                tool,
                draft.Ceiling.DenyList.Contains(tool, StringComparer.OrdinalIgnoreCase),
                draft.Ceiling.AskBeforeList.Contains(tool, StringComparer.OrdinalIgnoreCase)));

        UnknownTools = known.Where(n => !toolNames.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();

        AddParameterCommand = new(() => Parameters.Add(Track(new ParameterRow(
            new TemplateParameter("", "", TemplateParameterType.Text)))));
        AddCriterionCommand = new(() => Criteria.Add(Track(new CriterionRow(
            new SuccessCriterionDefinition("", "")))));
        ShowFormCommand = new(() => Tab = 0);
        ShowJsonCommand = new(() => Tab = 1);
        SaveCommand = new(Save);
        CancelCommand = new(() => CloseRequested?.Invoke());
    }

    // ── the form ────────────────────────────────────────────────────────────

    private string _id, _name, _description, _category, _goal, _workerId;
    private string _maxSteps, _maxTokens, _maxSeconds;
    private PermissionLevel? _maxLevel;
    private bool _reviewRequired;
    private string _status = "";
    private string _json = "";
    private int _tab;
    private TemplateScope _scope;

    public bool IdEditable { get; }

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Category { get => _category; set => Set(ref _category, value); }
    public string Goal { get => _goal; set => Set(ref _goal, value); }
    public string WorkerId { get => _workerId; set => Set(ref _workerId, value); }
    public bool ReviewRequired { get => _reviewRequired; set => Set(ref _reviewRequired, value); }

    public string MaxSteps { get => _maxSteps; set => Set(ref _maxSteps, value); }
    public string MaxTokens { get => _maxTokens; set => Set(ref _maxTokens, value); }
    public string MaxSeconds { get => _maxSeconds; set => Set(ref _maxSeconds, value); }

    /// <summary>Null is "no cap of its own", which is not the same as Autonomous.</summary>
    public PermissionLevel? MaxLevel { get => _maxLevel; set => Set(ref _maxLevel, value); }
    public IReadOnlyList<PermissionLevel?> Levels { get; } =
        new PermissionLevel?[] { null, PermissionLevel.Observe, PermissionLevel.Suggest,
                                 PermissionLevel.Execute, PermissionLevel.Autonomous };

    public ObservableCollection<ParameterRow> Parameters { get; } = new();
    public ObservableCollection<CriterionRow> Criteria { get; } = new();
    public ObservableCollection<ToolPermissionRow> Tools { get; } = new();

    /// <summary>Names this template restricts that no registered tool answers to.</summary>
    public IReadOnlyList<string> UnknownTools { get; }
    public bool HasUnknownTools => UnknownTools.Count > 0;
    public string UnknownToolsText =>
        $"This template names {string.Join(", ", UnknownTools)}, which no tool in this build is called. "
        + "The permission engine matches names exactly, so those entries restrict nothing.";

    public IReadOnlyList<TemplateScope> Scopes { get; }
    public TemplateScope Scope { get => _scope; set => Set(ref _scope, value); }

    public string Status
    {
        get => _status;
        private set { Set(ref _status, value); OnPropertyChanged(nameof(HasStatus)); }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(_status);
    public string JsonText { get => _json; set => Set(ref _json, value); }

    /// <summary>0 = the form, 1 = the JSON. Switching synchronises the two.</summary>
    public int Tab
    {
        get => _tab;
        set
        {
            if (_tab == value)
                return;

            if (_tab == 1 && !ApplyJson())
                return;   // stays on the JSON tab, with the parse error in Status

            if (value == 1)
                JsonText = JsonSerializer.Serialize(Compose(), Json);

            Set(ref _tab, value);
            OnPropertyChanged(nameof(IsForm));
            OnPropertyChanged(nameof(IsJson));
        }
    }

    public bool IsForm => Tab == 0;
    public bool IsJson => Tab == 1;

    public RelayCommand AddParameterCommand { get; }
    public RelayCommand AddCriterionCommand { get; }
    public RelayCommand ShowFormCommand { get; }
    public RelayCommand ShowJsonCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    // ── form <-> template ───────────────────────────────────────────────────

    /// <summary>The template as the form currently describes it.</summary>
    private TaskTemplate Compose()
    {
        var deny = Tools.Where(t => t.Deny).Select(t => t.Name).ToArray();
        var ask = Tools.Where(t => t.Ask).Select(t => t.Name).ToArray();

        var ceiling = MaxLevel is null && deny.Length == 0 && ask.Length == 0
            ? null
            : new PermissionCeiling(MaxLevel, ask.Length == 0 ? null : ask, deny.Length == 0 ? null : deny);

        var limits = Number(MaxSteps) is null && Number(MaxTokens) is null && Number(MaxSeconds) is null
            ? null
            : new ExecutionLimits(Number(MaxSteps), Number(MaxTokens), Number(MaxSeconds));

        return _original with
        {
            Id = Id.Trim(),
            Name = Name.Trim(),
            Goal = Goal,
            Description = Blank(Description),
            Category = Blank(Category),
            Parameters = Parameters.Count == 0 ? null : Parameters.Select(p => p.ToParameter()).ToArray(),
            SuccessCriteria = Criteria.Count == 0 ? null : Criteria.Select(c => c.ToCriterion()).ToArray(),
            Permissions = ceiling,
            Limits = limits,
            WorkerId = Blank(WorkerId),
            ReviewRequired = ReviewRequired,
            // A file is never a built-in, whatever it was copied from. Saving one under a built-in's
            // id shadows it, and deleting the file brings the original back.
            Builtin = false
        };
    }

    /// <summary>Reads the JSON tab back into the form. False when it will not parse.</summary>
    private bool ApplyJson()
    {
        TaskTemplate? parsed;
        try { parsed = JsonSerializer.Deserialize<TaskTemplate>(JsonText, Json); }
        catch (JsonException ex)
        {
            Status = "That JSON could not be read, so nothing was changed: " + ex.Message;
            return false;
        }

        if (parsed is null)
        {
            Status = "That JSON is empty.";
            return false;
        }

        Id = parsed.Id;
        Name = parsed.Name;
        Goal = parsed.Goal;
        Description = parsed.Description ?? "";
        Category = parsed.Category ?? "";
        WorkerId = parsed.WorkerId ?? "";
        ReviewRequired = parsed.ReviewRequired;
        MaxSteps = parsed.LimitsOrNone.MaxSteps?.ToString() ?? "";
        MaxTokens = parsed.LimitsOrNone.MaxTokens?.ToString() ?? "";
        MaxSeconds = parsed.LimitsOrNone.MaxDurationSeconds?.ToString() ?? "";
        MaxLevel = parsed.Ceiling.MaxLevel;

        Parameters.Clear();
        foreach (var p in parsed.ParameterList)
            Parameters.Add(Track(new ParameterRow(p)));

        Criteria.Clear();
        foreach (var c in parsed.CriteriaList)
            Criteria.Add(Track(new CriterionRow(c)));

        foreach (var tool in Tools)
        {
            tool.Deny = parsed.Ceiling.DenyList.Contains(tool.Name, StringComparer.OrdinalIgnoreCase);
            tool.Ask = parsed.Ceiling.AskBeforeList.Contains(tool.Name, StringComparer.OrdinalIgnoreCase);
        }

        Status = "";
        return true;
    }

    private void Save()
    {
        if (Tab == 1 && !ApplyJson())
            return;

        var template = Compose();

        // The same validator the store uses. Checking here as well is not duplication: it is the
        // difference between a message naming the field and an exception from a file write.
        var problems = TemplateValidator.Validate(template);
        if (problems.Count > 0)
        {
            Status = string.Join("\n", problems.Select(p => "· " + p));
            return;
        }

        try
        {
            _saved(template, Scope);
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Status = "This template was not saved: " + ex.Message;
        }
    }

    private ParameterRow Track(ParameterRow row)
    {
        row.RemoveCommand = new(() => Parameters.Remove(row));
        return row;
    }

    private CriterionRow Track(CriterionRow row)
    {
        row.RemoveCommand = new(() => Criteria.Remove(row));
        return row;
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static int? Number(string value)
        => int.TryParse(value, out var n) && n > 0 ? n : null;
}
