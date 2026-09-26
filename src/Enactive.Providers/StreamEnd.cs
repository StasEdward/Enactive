namespace Enactive.Providers;

using System.Text.Json;

/// <summary>
/// How a streamed answer is allowed to END - for every adapter that reads one.
///
/// <para><b>Found 2026-09-24</b> (Docs/PROVIDERS_AGENTS_TOOLS_TESTS_REVIEW_2026-09-24.md #1): an
/// error the server reports INSIDE a stream that began with HTTP 200 - <c>{"error": ...}</c> as a
/// chunk - was not an event either parser knew, so it produced none, and the stream then ran out.
/// Nothing required it to have finished, so an error became an ordinary end: through the real
/// orchestrator, a <c>write_file</c> streamed before the error was EXECUTED, the task closed as
/// Completed, and no ErrorObserved was recorded. A stream that stops without its protocol's own
/// "finished" (Ollama's <c>done: true</c>, OpenAI's <c>finish_reason</c> or <c>[DONE]</c>) is the
/// same failure without the courtesy of a message.</para>
///
/// <para>Both are raised as <see cref="HttpRequestException"/>, the same type as a failed HTTP
/// status, so everything that already handles a provider failure - the fallback, the step's
/// outcome, the log - handles these too. And nothing streamed before them is acted on: the
/// exception leaves the stream before the loop can run the tool calls it had gathered.</para>
/// </summary>
internal static class StreamEnd
{
    /// <summary>The error a chunk carries, as the exception to throw - or null when it carries none.</summary>
    public static HttpRequestException? ErrorIn(string providerId, JsonElement chunk)
    {
        if (chunk.ValueKind != JsonValueKind.Object || !chunk.TryGetProperty("error", out var error))
            return null;

        var said = error.ValueKind switch
        {
            JsonValueKind.String => error.GetString(),
            JsonValueKind.Object when error.TryGetProperty("message", out var message)
                                      && message.ValueKind == JsonValueKind.String => message.GetString(),
            JsonValueKind.Null => null,
            _ => error.GetRawText()
        };

        if (said is null) return null;
        if (said.Length > 500) said = said[..500] + "…";
        var status = Status(error) ?? Status(chunk);
        return new HttpRequestException(
            $"Provider '{providerId}' reported an error in the middle of its answer: {said}. "
            + "Nothing it sent before the error is acted on.", null,
            status is { } code ? (System.Net.HttpStatusCode)code : null);
    }

    private static int? Status(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "status", "status_code", "code" })
            if (error.TryGetProperty(name, out var value))
            {
                int code;
                if ((value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out code)
                    || value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out code))
                    && code is >= 400 and <= 599) return code;
            }
        // Match protocol identifiers only, never guesses based on a human error message.
        // Anthropic: https://platform.claude.com/docs/en/api/errors
        foreach (var name in new[] { "type", "code" })
            if (error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                int? status = value.GetString() switch
                {
                    "invalid_request_error" => 400,
                    "authentication_error" => 401,
                    "billing_error" => 402,
                    "permission_error" => 403,
                    "not_found_error" => 404,
                    "request_too_large" => 413,
                    "rate_limit_error" or "rate_limit_exceeded" => 429,
                    "api_error" or "server_error" => 500,
                    "service_unavailable" => 503,
                    "timeout_error" => 504,
                    "overloaded_error" => 529,
                    _ => null
                };
                if (status is not null) return status;
            }
        return null;
    }

    /// <summary>The exception for a stream that stopped without saying it had finished.</summary>
    public static HttpRequestException Unfinished(string providerId, string expected)
        => new(HttpRequestError.ResponseEnded, $"Provider '{providerId}' stopped sending before its answer was finished (no {expected}). "
               + "The answer is incomplete, and nothing in it is acted on.");
}
