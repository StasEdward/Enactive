namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Media;
using Enactive.App.Ui.Mvvm;
using Enactive.Tools.Mcp;

/// <summary>One row of the MCP list, on the same terms as <see cref="ProviderRow"/>.</summary>
internal sealed class McpServerRow : ObservableObject
{
    public McpServerConfig Config { get; }
    public string Name => Config.Id;
    public string Meta => Config.CredentialsUnavailable ? "Disabled · credentials unavailable — edit to re-enter"
        : $"{(Config.Enabled ? "Enabled" : "Disabled")} · {Config.Transport} · {(Config.RequireApproval ? "asks before calls" : "workspace autonomy")}";

    /// <summary>
    /// Green when the server is a program on this machine, blue when it is reached over the network
    /// — the same one-glance distinction the Providers list draws, and here it also says which
    /// connections leave the machine at all.
    /// </summary>
    public IBrush EdgeBrush => IsLocal ? Brand.Success : Brand.Info;

    public string Reach => IsLocal ? "local" : "remote";

    private bool IsLocal => Config.Transport == McpTransportKind.Stdio;

    public RelayCommand EditCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public McpServerRow(McpServerConfig config, Action edit, Action remove)
    { Config = config; EditCommand = new(edit); RemoveCommand = new(remove); }
}

internal sealed partial class SettingsViewModel
{
    public bool IsMcp => Section == 7;
    public RelayCommand ShowMcpCommand { get; private set; } = null!;
    public RelayCommand AddMcpCommand { get; private set; } = null!;
    public ObservableCollection<McpServerRow> McpServers { get; } = new();
    private McpServerRow? _selectedMcp;
    public McpServerRow? SelectedMcp { get => _selectedMcp; set => Set(ref _selectedMcp, value); }
    public event Action<McpServerConfig, Action<McpServerConfig>>? McpEditRequested;

    private void InitializeMcp()
    {
        ShowMcpCommand = new(() => Section = 7);
        AddMcpCommand = new(() => EditMcp(null, new McpServerConfig()));
        RefreshMcp();
    }

    private void RefreshMcp()
    {
        // The rows are rebuilt, so the old selection points at an object that no longer exists;
        // clearing it stops the highlight sitting on a row nobody chose. Same as Providers/Team.
        SelectedMcp = null;
        McpServers.Clear();
        foreach (var config in _working.McpServers)
            McpServers.Add(new(config, () => EditMcp(config, config.Clone()), () => _ = RemoveMcpAsync(config)));
    }

    private void EditMcp(McpServerConfig? previous, McpServerConfig draft)
        => McpEditRequested?.Invoke(draft, saved =>
        {
            if (previous is null) _working.McpServers.Add(saved);
            else _working.McpServers[_working.McpServers.IndexOf(previous)] = saved;
            RefreshMcp();
        });

    private async Task RemoveMcpAsync(McpServerConfig config)
    {
        if (!await ConfirmAsync($"Remove MCP server '{config.Id}'?", "The server configuration is removed when you save Settings. Running tasks keep their existing connections.")) return;
        _working.McpServers.Remove(config);
        RefreshMcp();
    }
}
