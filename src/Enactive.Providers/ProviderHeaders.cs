namespace Enactive.Providers;

using Enactive.Core.Providers;

/// <summary>
/// Applies the custom headers configured on a provider to an outgoing request.
///
/// It exists because two of the three adapters were not doing it. A user could add
/// <c>anthropic-workspace-id</c>, an <c>OpenAI-Organization</c>, a gateway's auth header or a proxy
/// token in the provider editor, see it saved, and have it never reach the wire — the request simply
/// went out without it and the failure surfaced somewhere else entirely, as a 401 or a wrong-account
/// bill. A setting that is offered has to be applied by everything that offers it, which is exactly
/// the class of defect the 2026-09-06 review was about.
/// </summary>
internal static class ProviderHeaders
{
    public static void Apply(HttpRequestMessage request, ProviderDescriptor descriptor)
        => Apply(request, descriptor.Headers);

    public static void Apply(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is not { Count: > 0 })
            return;

        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header.Key))
                continue;

            // Without validation: these are the user's own headers for their own endpoint, and
            // HttpClient's idea of which ones are "restricted" is not a judgement to impose here.
            try
            {
                request.Headers.Remove(header.Key);
                if (request.Headers.TryAddWithoutValidation(header.Key, header.Value)) continue;
            }
            catch (InvalidOperationException) { /* A content header belongs on HttpContent. */ }
            if (request.Content is { } content)
            {
                content.Headers.Remove(header.Key);
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }
}
