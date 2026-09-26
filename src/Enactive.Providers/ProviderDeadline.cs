namespace Enactive.Providers;

/// <summary>Separate total completion and response-header deadlines; caller cancellation remains cancellation.</summary>
internal static class ProviderDeadline
{
    public static async Task<T> RunAsync<T>(int seconds, string phase, CancellationToken caller,
        Func<CancellationToken, Task<T>> action)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        var bounded = Math.Clamp(seconds, 1, 86400);
        deadline.CancelAfter(TimeSpan.FromSeconds(bounded));
        try { return await action(deadline.Token); }
        catch (OperationCanceledException ex) when (!caller.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException($"Provider {phase} exceeded its {bounded}-second deadline.", ex); }
    }

    public static Task<HttpResponseMessage> HeadersAsync(HttpClient http, HttpRequestMessage message,
        int seconds, CancellationToken ct)
        => RunAsync(seconds, "response headers", ct,
            token => http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token));
}
