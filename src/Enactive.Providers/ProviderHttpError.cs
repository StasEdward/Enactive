namespace Enactive.Providers;

internal static class ProviderHttpError
{
    public static async Task<string> ReadBodyAsync(HttpResponseMessage response, int idleSeconds, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(new IdleTimeoutStream(stream, TimeSpan.FromSeconds(Math.Clamp(idleSeconds, 1, 86400))));
            var text = new System.Text.StringBuilder();
            var buffer = new char[4096];
            while (text.Length < 65536)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 65536 - text.Length)), ct);
                if (read == 0) return text.ToString();
                text.Append(buffer, 0, read);
            }
            return text.Append("\n[error response truncated at 65536 characters]").ToString();
        }
        catch (TimeoutException ex) when (!ct.IsCancellationRequested)
        {
            throw Create($"Provider returned HTTP {(int)response.StatusCode}; its error body timed out while being read.",
                (int)response.StatusCode, RetryAfter(response), ex);
        }
    }

    internal const string RetryAfterKey = "Enactive.RetryAfter";

    public static HttpRequestException Create(string message, int status, TimeSpan? retryAfter, Exception? inner = null)
    {
        var error = new HttpRequestException(message, inner, (System.Net.HttpStatusCode)status);
        if (retryAfter is { } delay) error.Data[RetryAfterKey] = delay;
        return error;
    }

    public static TimeSpan? RetryAfter(HttpResponseMessage response)
        => response.Headers.RetryAfter?.Delta
           ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
}
