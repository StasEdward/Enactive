using Avalonia.Controls;
using Enactive.App.Ui.ViewModels;
using Enactive.Core.History;
using Enactive.Core.Inbox;

namespace Enactive.App.Ui;

/// <summary>
/// The AI Inbox: what background runs reported back. Selecting an item opens the run behind it and
/// marks the item read, which is what the old text dump could not do.
/// </summary>
internal sealed partial class InboxWindow : Window
{
    private readonly InboxViewModel _viewModel;

    /// <param name="carryOn">What answering a question a background run stopped at does next: carries that run on.</param>
    public InboxWindow(IInboxStore inbox, IRunStore runs, string workspaceRoot,
        Func<Enactive.Agents.ParkedDecision, InboxItem, Task>? carryOn = null)
    {
        _viewModel = new InboxViewModel(inbox, runs, workspaceRoot, carryOn);
        DataContext = _viewModel;
        InitializeComponent();

        // The first load cannot happen in a constructor, so it starts as the window opens.
        Opened += (_, _) => _ = _viewModel.RefreshAsync();
    }

    /// <summary>Raised whenever the unread count changes, so the main window's badge can follow.</summary>
    public event Action<int>? UnreadChanged
    {
        add => _viewModel.UnreadChanged += value;
        remove => _viewModel.UnreadChanged -= value;
    }
}
