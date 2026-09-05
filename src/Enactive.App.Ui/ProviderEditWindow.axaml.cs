using Avalonia.Controls;
using Enactive.App.Ui.ViewModels;

namespace Enactive.App.Ui;

/// <summary>
/// Add/edit one provider endpoint. The window is the shell only: layout lives in the .axaml and the
/// behaviour in ProviderEditViewModel, which writes back into the config it was given only on Save.
/// </summary>
internal sealed partial class ProviderEditWindow : Window
{
    public ProviderEditWindow(ProviderConfig config, Action onSaved)
    {
        var viewModel = new ProviderEditViewModel(config, onSaved);
        viewModel.CloseRequested += () => Close();
        DataContext = viewModel;
        InitializeComponent();
    }
}
