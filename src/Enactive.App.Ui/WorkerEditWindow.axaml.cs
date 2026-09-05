using Avalonia.Controls;
using Enactive.App.Ui.ViewModels;

namespace Enactive.App.Ui;

/// <summary>
/// Add/edit one team member (role + tools + level + its own model). Layout lives in the .axaml,
/// behaviour in WorkerEditViewModel, which writes back into the config only on Save.
/// </summary>
internal sealed partial class WorkerEditWindow : Window
{
    public WorkerEditWindow(WorkerConfig config, IReadOnlyList<string> modelCatalog, Action onSaved)
    {
        var viewModel = new WorkerEditViewModel(config, modelCatalog, onSaved);
        viewModel.CloseRequested += () => Close();
        DataContext = viewModel;
        InitializeComponent();
    }
}
