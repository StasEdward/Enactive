namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.Permissions;

/// <summary>One tool the worker may or may not use. A row in the tools list, not a checkbox built in code.</summary>
internal sealed class ToolToggle : ObservableObject
{
    private bool _isSelected;

    public ToolToggle(string name, bool isSelected)
    {
        Name = name;
        _isSelected = isSelected;
    }

    public string Name { get; }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>
/// Editing one team member: role, instructions, the tools it may call, its permission level and its
/// own model. Like the provider editor, it edits a working copy and writes back only on Save.
/// </summary>
internal sealed class WorkerEditViewModel : ObservableObject
{
    /// <summary>The tools the app ships. A worker carrying an unknown tool keeps it - see the ctor.</summary>
    private static readonly string[] KnownTools =
        { "write_file", "read_file", "list_dir", "run_command", "run_powershell", "git", "docker" };

    private const string NoneItem = "(none)";

    private readonly WorkerConfig _config;
    private readonly Action _onSaved;

    private string _id;
    private string _role;
    private string _instructions;
    private PermissionLevel _level;
    private string? _model;
    private string? _fallback;

    public WorkerEditViewModel(WorkerConfig config, IReadOnlyList<string> modelCatalog, Action onSaved)
    {
        _config = config;
        _onSaved = onSaved;

        _id = config.Id;
        _role = config.Role;
        _instructions = config.Instructions;
        _level = config.Level;

        // The known tools first, then anything this worker already has that we do not know about, so
        // an unrecognised tool survives a round trip through this dialog instead of being dropped.
        foreach (var tool in KnownTools.Concat(config.Tools.Where(t => !KnownTools.Contains(t))).Distinct())
            Tools.Add(new ToolToggle(tool, config.Tools.Contains(tool)));

        // A saved model that is no longer in the catalog is kept as an item, so opening the dialog
        // and pressing Save does not quietly retarget the worker.
        foreach (var m in modelCatalog)
            Models.Add(m);
        if (!string.IsNullOrWhiteSpace(config.Model) && !Models.Contains(config.Model))
            Models.Add(config.Model);
        _model = !string.IsNullOrWhiteSpace(config.Model) ? config.Model
               : Models.Count > 0 ? Models[0]
               : null;

        Fallbacks.Add(NoneItem);
        foreach (var m in modelCatalog)
            Fallbacks.Add(m);
        if (!string.IsNullOrWhiteSpace(config.Fallback) && !Fallbacks.Contains(config.Fallback))
            Fallbacks.Add(config.Fallback);
        _fallback = string.IsNullOrWhiteSpace(config.Fallback) ? NoneItem : config.Fallback;

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public event Action? CloseRequested;

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Role { get => _role; set => Set(ref _role, value); }
    public string Instructions { get => _instructions; set => Set(ref _instructions, value); }
    public PermissionLevel Level { get => _level; set => Set(ref _level, value); }
    public string? Model { get => _model; set => Set(ref _model, value); }
    public string? Fallback { get => _fallback; set => Set(ref _fallback, value); }

    public ObservableCollection<ToolToggle> Tools { get; } = new();
    public ObservableCollection<string> Models { get; } = new();
    public ObservableCollection<string> Fallbacks { get; } = new();

    public IReadOnlyList<PermissionLevel> Levels { get; } = Enum.GetValues<PermissionLevel>();

    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    private void Save()
    {
        _config.Id = Id.Trim();
        _config.Role = Role.Trim();
        _config.Instructions = Instructions;
        _config.Tools = Tools.Where(t => t.IsSelected).Select(t => t.Name).ToList();
        _config.Level = Level;
        _config.Model = Model ?? string.Empty;
        _config.Fallback = string.IsNullOrEmpty(Fallback) || Fallback == NoneItem ? null : Fallback;
        _onSaved();
        CloseRequested?.Invoke();
    }
}
