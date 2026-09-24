namespace Enactive.Providers;

using Enactive.Core.Diagnostics;
using Enactive.Core.Providers;

/// <summary>Builds a chat provider from a registered <see cref="ProviderDescriptor"/>.</summary>
public sealed class ChatProviderFactory : IChatProviderFactory
{
    private readonly IReadOnlyDictionary<string, ProviderDescriptor> _descriptors;
    private readonly HttpClient _http;
    private readonly ILogSink? _log;

    public ChatProviderFactory(IEnumerable<ProviderDescriptor> descriptors, HttpClient http, ILogSink? log = null)
    {
        _descriptors = descriptors.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Whether prompts logged through this factory carry their bodies. A setting, because the
    /// bodies are what make a log diagnosable and also most of what makes it large; a caller that
    /// asks for a specific answer overrides it.
    /// </summary>
    public bool PromptBodies { get; set; } = true;

    public Action<Enactive.Core.Chat.ModelCallMetrics>? MetricsReported { get; set; }

    public IChatProvider Create(string providerId) => Create(providerId, PromptBodies);

    /// <param name="promptBodies">
    /// False for a caller whose prompts carry the log itself — the log window's "AI Analyze". The
    /// call is still logged; only its messages are left out. See
    /// <see cref="LoggingChatProvider(IChatProvider, ILogSink, string, bool)"/> for what happened
    /// without this.
    /// </param>
    public IChatProvider Create(string providerId, bool promptBodies)
    {
        if (!_descriptors.TryGetValue(providerId, out var descriptor))
            throw new InvalidOperationException($"Unknown provider '{providerId}'.");

        IChatProvider provider = descriptor.Kind switch
        {
            ProviderKind.OpenAiCompatible => new OpenAiCompatibleProvider(_http, descriptor, _log),
            ProviderKind.OllamaNative => new OllamaNativeProvider(_http, descriptor, _log),
            ProviderKind.Anthropic => new AnthropicProvider(_http, descriptor, _log),
            _ => throw new NotSupportedException($"Provider kind '{descriptor.Kind}' is not supported yet.")
        };

        // Explained ALWAYS, and innermost, so the sentence is on the exception before anything else
        // sees it - including the logging decorator, which writes the message it is given. Turning
        // the log off must not make the errors worse.
        provider = new ExplainedChatProvider(provider, descriptor.Id, descriptor.BaseUrl);

        // Wrap in the readable-plane decorator when logging is on (raw byte-level dump is inside each provider).
        provider = _log is null ? provider : new LoggingChatProvider(provider, _log, descriptor.Id, promptBodies);
        return MetricsReported is { } report ? new MeteredChatProvider(provider, descriptor.Id, report) : provider;
    }
}
