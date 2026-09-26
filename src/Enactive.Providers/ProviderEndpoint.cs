namespace Enactive.Providers;

using Enactive.Core.Providers;

internal static class ProviderEndpoint
{
    public static Uri Chat(ProviderDescriptor provider, ProviderKind kind)
    {
        if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var root)
            || root.Scheme is not ("http" or "https") || string.IsNullOrEmpty(root.Host))
            throw new ArgumentException($"Provider '{provider.Id}' has an invalid endpoint URL. Configure an absolute HTTP(S) BaseUrl.");
        var path = root.AbsolutePath.TrimEnd('/');
        switch (kind)
        {
            case ProviderKind.Anthropic:
                if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path += "/v1";
                path += "/messages";
                break;
            case ProviderKind.OllamaNative:
                if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path = path[..^3];
                path += "/api/chat";
                break;
            default: path += "/chat/completions"; break;
        }
        return new UriBuilder(root) { Path = path }.Uri;
    }
}
