namespace Enactive.App.Ui;

using Avalonia.Controls;
using Enactive.App.Ui.ViewModels;
using Enactive.Settings;

/// <summary>
/// Managing schedules where the person is: the list, what each one is allowed to do in words, the
/// last few outcomes, and the form for adding, editing or turning one off.
///
/// <para>It holds nothing and decides nothing. The schedules are in the store, the words are
/// <c>ScheduleWords</c> and the validation is <c>ScheduleDrafts</c> — both in Core, where a test can
/// reach them. This window shows what they say.</para>
/// </summary>
internal sealed partial class SchedulesWindow : Window
{
    private readonly SchedulesViewModel _viewModel;

    public SchedulesWindow(string workspaceRoot, Func<AppSettings> settings)
    {
        _viewModel = new SchedulesViewModel(workspaceRoot, settings);
        DataContext = _viewModel;
        InitializeComponent();

        // The first load cannot happen in a constructor, so it starts as the window opens.
        Opened += (_, _) => _ = _viewModel.RefreshAsync();
    }

    /// <summary>
    /// Points an already-open window at another workspace. Schedules are per-workspace; a list left
    /// showing the previous project's while the buttons act on this one is the same disagreement
    /// the template library was fixed for.
    /// </summary>
    public void FollowWorkspace(string workspaceRoot)
        => _ = _viewModel.SetWorkspaceAsync(workspaceRoot);
}
