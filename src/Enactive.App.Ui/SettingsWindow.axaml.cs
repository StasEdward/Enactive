using Avalonia.Controls;
using Enactive.App.Ui.ViewModels;

namespace Enactive.App.Ui;

/// <summary>
/// Settings as tabs over the universal team schema (Docs/MODELS.md). Layout is in the .axaml and the
/// state in SettingsViewModel; the only thing left here is opening the two child editors, which is a
/// view's job because it owns the window they parent to.
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings, Action<AppSettings> onSaved)
    {
        var viewModel = new SettingsViewModel(settings, onSaved);
        viewModel.CloseRequested += () => Close();
        viewModel.ProviderEditRequested += (config, saved) =>
            new ProviderEditWindow(config, saved).Show(this);
        viewModel.WorkerEditRequested += (config, catalog, saved) =>
            new WorkerEditWindow(config, catalog, saved).Show(this);

        DataContext = viewModel;
        InitializeComponent();
    }
}
