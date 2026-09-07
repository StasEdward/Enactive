namespace Enactive.App.Ui;

using Avalonia.Controls;
using Enactive.App.Ui.ViewModels;
using Enactive.Core.Templates;
using Enactive.Workspace;

/// <summary>
/// The template editor. Layout is in the .axaml and the state in
/// <see cref="TemplateEditViewModel"/>; what stays here is closing the window, which is a view's job.
/// </summary>
internal sealed partial class TemplateEditWindow : Window
{
    public TemplateEditWindow(
        TaskTemplate draft, bool idEditable, IReadOnlyList<TemplateScope> scopes,
        IReadOnlyList<string> toolNames, Action<TaskTemplate, TemplateScope> saved)
    {
        var vm = new TemplateEditViewModel(draft, idEditable, scopes, toolNames, saved);
        vm.CloseRequested += Close;
        DataContext = vm;
        InitializeComponent();
    }
}
