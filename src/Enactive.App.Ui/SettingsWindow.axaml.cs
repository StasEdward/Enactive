using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Enactive.App.Ui.ViewModels;

namespace Enactive.App.Ui;

/// <summary>
/// Settings as sections over the universal team schema (Docs/MODELS.md) - a list on the left, one
/// pane on the right. Layout is in the .axaml and the state in SettingsViewModel; what stays here
/// is what needs the controls themselves: opening the two child editors, which is a view's job
/// because it owns the window they parent to, and double-click to edit.
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
        viewModel.ConfirmRequested += (headline, detail) =>
            ConfirmWindow.AskAsync(this, headline, detail, "Remove", "Keep");

        viewModel.McpEditRequested += (config, saved) => new McpEditWindow(config, saved).ShowDialog(this);
        DataContext = viewModel;
        InitializeComponent();

        // Double-click opens the row, the way a list of things has opened them since Windows 3.
        ProviderList.DoubleTapped += (_, e) => OpenTapped<ProviderRow>(e, row => row.EditCommand);
        WorkerList.DoubleTapped += (_, e) => OpenTapped<WorkerRow>(e, row => row.EditCommand);
        McpList.DoubleTapped += (_, e) => OpenTapped<McpServerRow>(e, row => row.EditCommand);
    }

    /// <summary>
    /// Edits the row that was double-clicked. Taps that landed on one of the row's own buttons are
    /// ignored: a quick second click on the pencil is one person pressing it twice, not a request
    /// to open the same editor again.
    /// </summary>
    private static void OpenTapped<T>(TappedEventArgs e, Func<T, Mvvm.RelayCommand> command)
        where T : class
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<Button>(true) is not null)
            return;

        // The card carries the row as its DataContext, wherever inside it the tap landed.
        if (source.FindAncestorOfType<ListBoxItem>(true)?.DataContext is T row)
            command(row).Execute(null);
    }
}
