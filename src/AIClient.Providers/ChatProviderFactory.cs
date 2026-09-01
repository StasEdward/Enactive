namespace AIClient.Providers;

using AIClient.Core.Providers;

/// <summary>Builds a chat provider from a registered <see cref="ProviderDescriptor"/>.</summary>
public sealed class ChatProviderFactory : IChatProviderFactory
{
    private readonly IReadOnlyDictionary<string, ProviderDescriptor> _descriptors;
    private readonly HttpClient _http;

    public ChatProviderFactory(IEnumerable<ProviderDescriptor> descriptors, HttpClient http)
    {
        _descriptors = descriptors.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        _http = http;
    }

    public IChatProvider Create(string providerId)
    {
        if (!_descriptors.TryGetValue(providerId, out var descriptor))
            throw new InvalidOperationException($"Unknown provider '{providerId}'.");

        return descriptor.Kind switch
        {
            ProviderKind.OpenAiCompatible or ProviderKind.OllamaNative => new OpenAiCompatibleProvider(_http, descriptor),
            ProviderKind.Anthropic => new AnthropicProvider(_http, descriptor),
            _ => throw new NotSupportedException($"Provider kind '{descriptor.Kind}' is not supported yet.")
        };
    }
}
