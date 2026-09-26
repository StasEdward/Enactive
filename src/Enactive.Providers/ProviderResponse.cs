namespace Enactive.Providers;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Providers;

internal static class ProviderResponse
{
    public static T Parse<T>(Func<T> parse, ProviderDescriptor provider, ChatRequest request)
    {
        try { return parse(); }
        catch (Exception ex) when (Malformed(ex)) { throw Error(provider, request, ex); }
    }

    public static async IAsyncEnumerable<ChatStreamEvent> Guard(
        IAsyncEnumerable<ChatStreamEvent> source, ProviderDescriptor provider, ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var iterator = source.GetAsyncEnumerator(ct);
        while (true)
        {
            bool moved;
            try { moved = await iterator.MoveNextAsync(); }
            catch (Exception ex) when (Malformed(ex)) { throw Error(provider, request, ex); }
            if (!moved) yield break;
            yield return iterator.Current;
        }
    }

    public static void RejectHtml(string line, ProviderDescriptor provider, ChatRequest request)
    {
        if (line.AsSpan().TrimStart().StartsWith("<")) throw Error(provider, request);
    }

    private static bool Malformed(Exception ex) => ex is JsonException or KeyNotFoundException
        or InvalidOperationException or FormatException;

    private static InvalidDataException Error(ProviderDescriptor provider, ChatRequest request, Exception? inner = null)
    {
        // Do not echo response bodies, query tokens, passwords or request headers into diagnostics.
        var endpoint = Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
            ? new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri
            : "(invalid endpoint URL)";
        return new InvalidDataException($"Provider '{provider.Id}', model '{request.Model}', endpoint '{endpoint}': " +
            "invalid JSON response or unexpected response structure (possibly HTML from a proxy).", inner);
    }
}
