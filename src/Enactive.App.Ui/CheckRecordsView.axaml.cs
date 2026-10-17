using Avalonia.Controls;

namespace Enactive.App.Ui;

/// <summary>
/// "Run checks now" and a run's check records (Plan Phase 3.2). The layout is in the .axaml; the state is
/// CheckRecordsViewModel, and running the checks is the window's (MainWindow.RunChecksNowAsync). One view, so the past run's
/// page and the live run's proposals cannot show it two ways.
/// </summary>
internal sealed partial class CheckRecordsView : UserControl
{
    public CheckRecordsView() => InitializeComponent();
}
