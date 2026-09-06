namespace Enactive.App.Ui.ViewModels;

using System.Text.Json;
using Enactive.App.Ui.Mvvm;
using Enactive.Tools.Mcp;

internal sealed class McpEditViewModel : ObservableObject, IDisposable
{
    private readonly Action<McpServerConfig> _saved;
    private readonly CancellationTokenSource _lifetime = new();
    private string _status = "";
    private McpTransportKind _transport;

    // Every field notifies. They used to be plain auto-properties, which is enough while the only
    // writer is the TextBox itself, but means nothing on screen changes when the view model sets
    // one — the template button below does exactly that.
    private string _id = "", _command = "", _argumentsJson = "", _workingDirectory = "";
    private string _url = "", _environmentJson = "", _headersJson = "", _timeoutText = "";
    private bool _enabled, _requireApproval;

    public string Id { get => _id; set => Set(ref _id, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool RequireApproval { get => _requireApproval; set => Set(ref _requireApproval, value); }
    public string Command { get => _command; set => Set(ref _command, value); }
    public string ArgumentsJson { get => _argumentsJson; set => Set(ref _argumentsJson, value); }
    public string WorkingDirectory { get => _workingDirectory; set => Set(ref _workingDirectory, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public string EnvironmentJson { get => _environmentJson; set => Set(ref _environmentJson, value); }
    public string HeadersJson { get => _headersJson; set => Set(ref _headersJson, value); }
    public string TimeoutText { get => _timeoutText; set => Set(ref _timeoutText, value); }
    public McpTransportKind Transport
    {
        get => _transport;
        set { if (Set(ref _transport, value)) { OnPropertyChanged(nameof(IsStdio)); OnPropertyChanged(nameof(IsHttp)); } }
    }
    public bool IsStdio => Transport == McpTransportKind.Stdio;
    public bool IsHttp => !IsStdio;
    public IReadOnlyList<McpTransportKind> Transports { get; } = Enum.GetValues<McpTransportKind>();
    public string Status { get => _status; private set => Set(ref _status, value); }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand TestCommand { get; }
    public event Action? CloseRequested;

    public McpEditViewModel(McpServerConfig config, Action<McpServerConfig> saved)
    {
        _saved = saved;
        if (config.CredentialsUnavailable)
            Status = "Stored credentials could not be decrypted. Re-enter environment variables and headers before using these settings.";
        Id = config.Id; Enabled = config.Enabled; RequireApproval = config.RequireApproval;
        Transport = config.Transport; Command = config.Command; WorkingDirectory = config.WorkingDirectory;
        Url = config.Url; TimeoutText = config.TimeoutSeconds.ToString();
        var json = new JsonSerializerOptions { WriteIndented = true };
        ArgumentsJson = JsonSerializer.Serialize(config.Arguments, json);
        EnvironmentJson = JsonSerializer.Serialize(config.Environment, json);
        HeadersJson = JsonSerializer.Serialize(config.Headers, json);
        SaveCommand = new(Save);
        CancelCommand = new(() => CloseRequested?.Invoke());
        TestCommand = new(TestAsync);
    }

    private McpServerConfig Read()
    {
        var config = new McpServerConfig
        {
            Id = Id.Trim(), Enabled = Enabled, RequireApproval = RequireApproval, Transport = Transport,
            Command = Command.Trim(), WorkingDirectory = WorkingDirectory.Trim(), Url = Url.Trim(),
            Arguments = JsonSerializer.Deserialize<List<string>>(ArgumentsJson) ?? throw new FormatException("Arguments must be a JSON array."),
            Environment = JsonSerializer.Deserialize<Dictionary<string, string>>(EnvironmentJson) ?? throw new FormatException("Environment must be a JSON object."),
            Headers = JsonSerializer.Deserialize<Dictionary<string, string>>(HeadersJson) ?? throw new FormatException("Headers must be a JSON object."),
            TimeoutSeconds = int.TryParse(TimeoutText, out var seconds) ? seconds : 0
        };
        if (config.Environment.Any(p => p.Value is null) || config.Headers.Any(p => p.Value is null))
            throw new FormatException("Environment and header values must be strings.");
        if (config.Validate() is { } error) throw new FormatException(error);
        return config;
    }

    private void Save()
    {
        try { _saved(Read()); CloseRequested?.Invoke(); }
        catch (JsonException) { Status = "Invalid JSON. Arguments need an array of strings; environment and headers need objects of strings."; }
        catch (FormatException ex) { Status = ex.Message; }
    }

    private async Task TestAsync()
    {
        McpServerConfig config;
        try { config = Read(); }
        catch (JsonException) { Status = "Invalid JSON in arguments, environment or headers."; return; }
        catch (FormatException ex) { Status = ex.Message; return; }
        Status = "Connecting and listing tools…";
        try
        {
            await using var connection = await McpConnection.ConnectAsync(config, Directory.GetCurrentDirectory(), _lifetime.Token);
            Status = $"Connected · {connection.Tools.Count} tools\n" + string.Join("\n", connection.Tools.Select(t => t.Definition.Name));
        }
        catch (OperationCanceledException) { Status = "Connection cancelled or timed out."; }
        catch (Exception ex) { Status = $"Connection failed ({ex.GetType().Name}). Check the command, endpoint, credentials and server availability."; }
    }

    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
