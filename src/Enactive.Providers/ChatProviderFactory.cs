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

    public IChatProvider Create(string providerId)
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

        // Wrap in the readable-plane decorator when logging is on (raw byte-level dump is inside each provider).
        return _log is null ? provider : new LoggingChatProvider(provider, _log, descriptor.Id);
    }
}
