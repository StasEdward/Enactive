namespace Enactive.Application;

using System.Collections.ObjectModel;
using Enactive.Application;
using Enactive.Application.Mvvm;
using Enactive.Core.Templates;
using Enactive.Workspace;

/// <summary>One row of the Templates list, on the same terms as <see cref="McpServerRow"/>.</summary>
internal sealed class TemplateRow : ObservableObject
{
    public StoredTemplate Entry { get; }
    public TaskTemplate Template => Entry.Template;

    public string Name => Template.Name;

    public string Meta
    {
        get
        {
            var parts = new List<string> { Origin };
            if (Entry.Shadows is { } shadowed)
                parts.Add($"replaces the {shadowed.ToString().ToLowerInvariant()} one");
            if (Template.Category is { Length: > 0 } category)
                parts.Add(category);
            parts.Add($"{Template.ParameterList.Count} parameter(s)");
            if (Template.CriteriaList.Count > 0)
                parts.Add($"{Template.CriteriaList.Count} check(s)");
            return string.Join(" · ", parts);
        }
    }

    public string Origin => Entry.Origin switch
    {
        TemplateOrigin.Builtin => "Built-in",
        TemplateOrigin.Global => "Global",
        _ => "This workspace"
    };

    public string? Description => Template.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Template.Description);

    /// <summary>
    /// Where the template comes from, which decides who else sees it - so it is worth a colour rather than only
    /// a word (Palette.Template).
    /// </summary>
    public TemplateEdge Edge => TemplateEdges.Of(Entry.Origin);

    /// <summary>A built-in is customised rather than edited: the original has to stay reachable.</summary>
    public bool IsEditable => !Entry.IsBuiltin;

    /// <summary>A file that replaced a built-in can be deleted to get the built-in back.</summary>
    public bool CanReset => Entry.Shadows == TemplateOrigin.Builtin;

    public string RemoveTip => CanReset ? "Reset to the built-in" : "Delete this template";

    public RelayCommand EditCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public TemplateRow(StoredTemplate entry, Action edit, Action duplicate, Action remove)
    {
        Entry = entry;
        EditCommand = new(edit);
        DuplicateCommand = new(duplicate);
        RemoveCommand = new(remove);
    }
}

/// <summary>
/// The Templates section: create, edit, duplicate and delete the saved tasks the library offers.
///
/// <para><b>These are files, and they are saved immediately.</b> Everything else in this window is
/// edited on a copy of the settings and written when you press Save, so Cancel puts it back. A
/// template is a file in <c>%APPDATA%\Enactive\templates</c> or in the project's
/// <c>.enactive\templates</c>, and the editor writes it when you press Save there. That is a real
/// difference in how the window behaves and it is stated in the pane rather than left to be
/// discovered — a Cancel that silently does not undo something is worse than no Cancel.</para>
///
/// <para>A built-in is never edited in place. Saving a file under the same id SHADOWS it, which is
/// reversible: delete the file and the original comes back. That is what the Reset action does, and
/// it is why the customisation path is a copy rather than an edit — a person who edits the original
/// has nowhere to get it back from.</para>
/// </summary>
internal sealed partial class SettingsViewModel
{
    private const int SectionTemplates = 8;

    public bool IsTemplates => Section == SectionTemplates;
    public RelayCommand ShowTemplatesCommand { get; private set; } = null!;
    public RelayCommand AddTemplateCommand { get; private set; } = null!;
    public ObservableCollection<TemplateRow> Templates { get; } = new();

    private TemplateRow? _selectedTemplate;
    public TemplateRow? SelectedTemplate { get => _selectedTemplate; set => Set(ref _selectedTemplate, value); }

    public string TemplateFolders => _templates is null
        ? ""
        : _templates.WorkspaceFolder is { } local
            ? $"Global: {_templates.GlobalFolder}    ·    This workspace: {local}"
            : $"Global: {_templates.GlobalFolder}    ·    no workspace open, so only global templates can be saved";

    /// <summary>
    /// Asks the view to open the template editor: (draft, whether the id may still be changed, the
    /// scopes it may be saved to, what to do once it saved).
    /// </summary>
    public event Action<TaskTemplate, bool, IReadOnlyList<TemplateScope>, Action<TaskTemplate, TemplateScope>>? TemplateEditRequested;

    /// <summary>Set by the window: the tools this build actually registers, for the deny/ask lists.</summary>
    public IReadOnlyList<string> ToolNames { get; set; } = Array.Empty<string>();

    private TemplateStore? _templates;

    private void InitializeTemplates(string? workspaceRoot)
    {
        _templates = new TemplateStore(workspaceRoot);
        ShowTemplatesCommand = new(() => Section = SectionTemplates);
        AddTemplateCommand = new(() => Edit(TemplateDrafts.New(), isNew: true));
        RefreshTemplates();
    }

    private void RefreshTemplates()
    {
        if (_templates is null)
            return;

        // Same as Providers/MCP: the rows are rebuilt, so the old selection points at an object that
        // no longer exists and the highlight would sit on a row nobody chose.
        SelectedTemplate = null;
        Templates.Clear();

        foreach (var entry in _templates.Inventory())
        {
            var row = entry;
            Templates.Add(new TemplateRow(
                row,
                edit: () =>
                {
                    var (draft, idEditable) = TemplateDrafts.Edit(row);
                    Edit(draft, isNew: idEditable);
                },
                duplicate: () => Edit(TemplateDrafts.Duplicate(row, Templates.Select(r => r.Template.Id)), isNew: true),
                remove: () => _ = RemoveTemplateAsync(row)));
        }

        OnPropertyChanged(nameof(TemplateFolders));
    }

    private void Edit(TaskTemplate draft, bool isNew)
    {
        // Not for a host whose templates are changed elsewhere, as the person (editsFilesHere).
        if (_templates is null || !_editsFilesHere)
            return;

        var scopes = _templates.WorkspaceFolder is null
            ? new[] { TemplateScope.Global }
            : new[] { TemplateScope.Global, TemplateScope.Workspace };

        TemplateEditRequested?.Invoke(draft, isNew, scopes, (saved, scope) =>
        {
            _templates.Save(saved, scope);
            RefreshTemplates();
        });
    }

    private async Task RemoveTemplateAsync(StoredTemplate entry)
    {
        if (_templates is null || !_editsFilesHere || entry.Scope is not { } scope)
            return;

        // The window's Settings has a Cancel, and it does not undo this: said, in the question's words (TemplateDrafts).
        var question = TemplateDrafts.Remove(entry, "closing Settings with Cancel");
        if (!await ConfirmAsync(question.Headline, question.Body))
            return;

        _templates.Delete(entry.Template.Id, scope);
        RefreshTemplates();
    }
}
