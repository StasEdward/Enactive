using Avalonia.Controls;
using Enactive.Application;

namespace Enactive.App.Ui;

/// <summary>A workspace's checks, opened from its card in the main window (Plan Phase 3.5).</summary>
public sealed partial class ChecksWindow : Window
{
    public ChecksWindow() => InitializeComponent();

    internal ChecksWindow(ChecksViewModel checks, string workspaceName) : this()
    {
        Title = $"Checks - {workspaceName}";
        List.DataContext = checks;
        checks.ConfirmRequested += (headline, detail) => ConfirmWindow.AskAsync(this, headline, detail, "Delete", "Keep");
        // Read again when it comes forward: a run that ended while it was behind the main window lets its checks be changed.
        Activated += (_, _) => checks.Refresh();
    }
}
