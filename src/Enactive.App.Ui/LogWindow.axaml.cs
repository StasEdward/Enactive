using Avalonia.Controls;
using Enactive.Agents;
using Enactive.App.Ui.ViewModels;
using Enactive.Workspace;

namespace Enactive.App.Ui;

/// <summary>
/// The global log window. One per app, fed live by the <see cref="LogHub"/> and pre-filled from its
/// ring buffer, so it shows the whole message flow across every run. The view model holds the
/// entries and the filtering; what stays here is the one thing that needs the control itself -
/// following the tail.
/// </summary>
public sealed partial class LogWindow : Window
{
    /// <param name="analyse">
    /// How to have a model read the log, or null when none is configured. Supplied by the main
    /// window, which owns the providers and the routing; this window only knows what is on screen.
    /// </param>
    public LogWindow(LogHub hub, Func<string, CancellationToken, Task<LogAnalysisResult>>? analyse = null)
    {
        var viewModel = new LogWindowViewModel(hub) { Analyse = analyse };
        viewModel.AnalysisReady += result => new LogAnalysisWindow(result).Show(this);
        DataContext = viewModel;
        InitializeComponent();

        void FollowTail()
        {
            if (viewModel.AutoScroll && viewModel.Rows.Count > 0)
                RowList.ScrollIntoView(viewModel.Rows[^1]);
        }

        viewModel.RowsAppended += FollowTail;
        // The view model filled itself from the hub's backlog before this subscription existed, so
        // open on the newest line rather than at the top of twenty thousand of them.
        FollowTail();

        Closed += (_, _) => viewModel.Detach();
    }
}
