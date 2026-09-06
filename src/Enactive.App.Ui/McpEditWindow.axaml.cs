namespace Enactive.App.Ui;

using Avalonia.Controls;
using Enactive.App.Ui.ViewModels;
using Enactive.Tools.Mcp;

internal sealed partial class McpEditWindow : Window
{
    public McpEditWindow(McpServerConfig config, Action<McpServerConfig> saved)
    {
        var vm = new McpEditViewModel(config, saved);
        vm.CloseRequested += Close;
        Closed += (_, _) => vm.Dispose();
        DataContext = vm;
        InitializeComponent();
    }
}
