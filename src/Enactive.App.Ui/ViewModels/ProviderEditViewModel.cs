namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Media;
using Enactive.App.Ui.Mvvm;
using Enactive.Providers;
using Enactive.Core.Providers;
using Enactive.Settings;

/// <summary>
/// Editing one provider endpoint. The view model holds a working copy as text - the way the fields
/// are typed - and only parses it back into the <see cref="ProviderConfig"/> on Save, so Cancel
/// leaves the settings untouched without needing a clone.
/// </summary>
internal sealed class ProviderEditViewModel : ObservableObject
{
    // One client for every editor: fetching a model list is a short, occasional call, and a client
    // per window is how socket exhaustion starts.
    // Shared with the Providers list, which checks the same endpoints with the same timeout. Two
    // clients would be two socket pools and two different ideas of how long is too long.
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ProviderConfig _config;
    private readonly Action _onSaved;

    private string _id;
    private string _displayName;
    private ProviderKind _kind;
    private string _baseUrl;
    private string _apiKey;
    private string _maxTokensText;
    private string _streamIdleTimeoutText;
    private string _completionTimeoutText;
    private string _reasoningAllowanceText;
    private bool _openAiReasoningProfile;
    private string _ollamaKeepAliveText;
    private string _contextWindowText;
    private string _answerReserveText;
    private string _handoverAtText;
    private string _workingContextText;
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
        _streamIdleTimeoutText = config.StreamIdleTimeoutSeconds.ToString();
        _completionTimeoutText = config.CompletionTimeoutSeconds.ToString();
        _reasoningAllowanceText = config.ReasoningTokenAllowance?.ToString() ?? string.Empty;
        _openAiReasoningProfile = config.OpenAiReasoningProfile;
        _ollamaKeepAliveText = config.OllamaKeepAliveSeconds?.ToString() ?? string.Empty;
        _contextWindowText = config.ContextWindowTokens?.ToString() ?? string.Empty;
        _answerReserveText = config.AnswerReserveTokens?.ToString() ?? string.Empty;
        _handoverAtText = config.HandoverAtPercent?.ToString() ?? string.Empty;
        _workingContextText = config.WorkingContextTokens?.ToString() ?? string.Empty;
        _headersText = ModelFetch.FormatHeaders(config.Headers);
        _modelsText = string.Join("\n", config.Models);

        FetchCommand = new AsyncRelayCommand(FetchModelsAsync);
        TestCommand = new AsyncRelayCommand(TestAsync);
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
    public string OllamaKeepAliveText { get => _ollamaKeepAliveText; set => Set(ref _ollamaKeepAliveText, value); }
    public string StreamIdleTimeoutText { get => _streamIdleTimeoutText; set => Set(ref _streamIdleTimeoutText, value); }
    public string CompletionTimeoutText { get => _completionTimeoutText; set => Set(ref _completionTimeoutText, value); }
    public string ReasoningAllowanceText { get => _reasoningAllowanceText; set => Set(ref _reasoningAllowanceText, value); }
    public bool OpenAiReasoningProfile { get => _openAiReasoningProfile; set => Set(ref _openAiReasoningProfile, value); }

    /// <summary>The CONTEXT window, which nothing can discover — see ProviderConfig.ContextWindowTokens.</summary>
    public string ContextWindowText { get => _contextWindowText; set => Set(ref _contextWindowText, value); }

    /// <summary>Tokens kept free for the answer — see ProviderConfig.AnswerReserveTokens.</summary>
    public string AnswerReserveText { get => _answerReserveText; set => Set(ref _answerReserveText, value); }

    /// <summary>Hand over at this % of the window — see ProviderConfig.HandoverAtPercent.</summary>
    public string HandoverAtText { get => _handoverAtText; set => Set(ref _handoverAtText, value); }

    /// <summary>The prompt size to work at, in tokens — see ProviderConfig.WorkingContextTokens.</summary>
    public string WorkingContextText { get => _workingContextText; set => Set(ref _workingContextText, value); }
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
    public AsyncRelayCommand TestCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>The colour of the last check. Grey until one has been made - not green.</summary>
    public IBrush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    private IBrush _statusBrush = Brand.TextFaint;

    /// <summary>
    /// Asks the provider whether it is there, using the FIRST model this provider lists.
    ///
    /// <para>The first one because a provider's model list here is what the workers may be bound
    /// to, and checking the endpoint without checking a model answers the easier question: a
    /// server that is up and a model that was never pulled look identical to a status check, and
    /// the second is how a working setup usually stops working.</para>
    ///
    /// <para>It does not send a completion. That is the only thing that would prove generation
    /// works, and it costs money on a metered provider and can take half a minute on a local model
    /// loading for the first time - for a button pressed while filling in a form. So the answer
    /// says what was actually established, and does not say "OK".</para>
    /// </summary>
    private async Task TestAsync()
    {
        Status = "checking…";
        StatusBrush = Brand.TextFaint;

        var status = await ProviderProbe.CheckAsync(
            Http, Kind, Id.Trim(), BaseUrl.Trim(), ApiKey,
            ModelFetch.ParseHeaders(HeadersText), ModelLines().FirstOrDefault());

        Status = status.Summary;
        StatusBrush = BrushFor(status.Health);
    }

    /// <summary>
    /// Three states, three colours, and grey for "nobody has asked".
    ///
    /// <para>Amber rather than red for a missing model: the provider answered and the credential
    /// was accepted, so nothing is broken - something is not installed or is misspelled, and that
    /// is a different repair.</para>
    /// </summary>
    internal static IBrush BrushFor(ProviderHealth health) => health switch
    {
        ProviderHealth.Ready => Brand.Success,
        ProviderHealth.ModelMissing => Brand.Warning,
        ProviderHealth.Unreachable => Brand.Danger,
        _ => Brand.TextFaint
    };

    private async Task FetchModelsAsync()
    {
        Status = "loading…";
        try
        {
            // Which catalogue a provider has is a fact about the provider, and lives with it.
            var models = await ModelFetch.ForAsync(
                Http, Kind, BaseUrl, ApiKey, ModelFetch.ParseHeaders(HeadersText));

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
        _config.StreamIdleTimeoutSeconds = int.TryParse(StreamIdleTimeoutText, out var idle) && idle > 0 ? idle : 300;
        _config.OpenAiReasoningProfile = OpenAiReasoningProfile;
        _config.CompletionTimeoutSeconds = int.TryParse(CompletionTimeoutText, out var deadline) && deadline > 0 ? Math.Min(deadline, 86400) : 900;
        _config.ReasoningTokenAllowance = int.TryParse(ReasoningAllowanceText, out var reasoning) && reasoning >= 0 ? Math.Min(reasoning, 65536) : null;
        _config.OllamaKeepAliveSeconds = int.TryParse(OllamaKeepAliveText, out var keepAlive) ? keepAlive : null;
        _config.ContextWindowTokens =
            int.TryParse(ContextWindowText.Trim(), out var cw) && cw > 0 ? cw : null;
        _config.AnswerReserveTokens =
            int.TryParse(AnswerReserveText.Trim(), out var ar) && ar > 0 ? ar : null;
        _config.HandoverAtPercent =
            int.TryParse(HandoverAtText.Trim().TrimEnd('%'), out var hp) && hp is > 0 and < 100 ? hp : null;
        _config.WorkingContextTokens =
            int.TryParse(WorkingContextText.Trim(), out var wc) && wc > 0 ? wc : null;
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
