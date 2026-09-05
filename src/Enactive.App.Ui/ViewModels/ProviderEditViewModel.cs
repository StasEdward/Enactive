namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Enactive.App.Ui.Mvvm;
using Enactive.Core.Providers;

/// <summary>
/// Editing one provider endpoint. The view model holds a working copy as text - the way the fields
/// are typed - and only parses it back into the <see cref="ProviderConfig"/> on Save, so Cancel
/// leaves the settings untouched without needing a clone.
/// </summary>
internal sealed class ProviderEditViewModel : ObservableObject
{
    // One client for every editor: fetching a model list is a short, occasional call, and a client
    // per window is how socket exhaustion starts.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ProviderConfig _config;
    private readonly Action _onSaved;

    private string _id;
    private string _displayName;
    private ProviderKind _kind;
    private string _baseUrl;
    private string _apiKey;
    private string _maxTokensText;
    private string _headersText;
    private string _modelsText;
    private string _status = string.Empty;
    private string? _selectedFetchedModel;

    public ProviderEditViewModel(ProviderConfig config, Action onSaved)
    {
        _config = config;
        _onSaved = onSaved;

        _id = config.Id;
        _displayName = config.DisplayName;
        _kind = config.Kind;
        _baseUrl = config.BaseUrl;
        _apiKey = config.ApiKey;
        _maxTokensText = config.MaxTokens?.ToString() ?? string.Empty;
        _headersText = ModelFetch.FormatHeaders(config.Headers);
        _modelsText = string.Join("\n", config.Models);

        FetchCommand = new AsyncRelayCommand(FetchModelsAsync);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    /// <summary>Raised when the dialog is done; the window subscribes and closes itself.</summary>
    public event Action? CloseRequested;

    public string Id { get => _id; set => Set(ref _id, value); }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public ProviderKind Kind { get => _kind; set => Set(ref _kind, value); }
    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value); }
    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, value); }
    public string MaxTokensText { get => _maxTokensText; set => Set(ref _maxTokensText, value); }
    public string HeadersText { get => _headersText; set => Set(ref _headersText, value); }
    public string ModelsText { get => _modelsText; set => Set(ref _modelsText, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    public IReadOnlyList<ProviderKind> Kinds { get; } = Enum.GetValues<ProviderKind>();

    /// <summary>What the last Fetch returned. Picking one appends it to the models list.</summary>
    public ObservableCollection<string> FetchedModels { get; } = new();

    public string? SelectedFetchedModel
    {
        get => _selectedFetchedModel;
        set
        {
            if (!Set(ref _selectedFetchedModel, value) || string.IsNullOrEmpty(value))
                return;
            var lines = ModelLines();
            if (!lines.Contains(value))
            {
                lines.Add(value);
                ModelsText = string.Join("\n", lines);
            }
        }
    }

    public AsyncRelayCommand FetchCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    private async Task FetchModelsAsync()
    {
        Status = "loading…";
        try
        {
            var models = Kind == ProviderKind.Anthropic
                ? await ModelFetch.AnthropicAsync(
                    Http,
                    ApiKey,
                    ModelFetch.ParseHeaders(HeadersText).TryGetValue("anthropic-workspace-id", out var wid) ? wid : null)
                : await ModelFetch.OllamaAsync(Http, BaseUrl);

            FetchedModels.Clear();
            foreach (var m in models)
                FetchedModels.Add(m);
            Status = models.Count == 0 ? "no models" : $"{models.Count} model(s) — pick to add";
        }
        catch (Exception ex)
        {
            Status = "error: " + ex.Message;
        }
    }

    private void Save()
    {
        _config.Id = Id.Trim();
        _config.DisplayName = DisplayName.Trim();
        _config.Kind = Kind;
        _config.BaseUrl = BaseUrl.Trim();
        _config.ApiKey = ApiKey.Trim();
        _config.MaxTokens = int.TryParse(MaxTokensText.Trim(), out var mt) && mt > 0 ? mt : null;
        _config.Headers = ModelFetch.ParseHeaders(HeadersText);
        _config.Models = ModelLines();
        _onSaved();
        CloseRequested?.Invoke();
    }

    private List<string> ModelLines()
        => ModelsText.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
}
