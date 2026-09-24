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

        return said is null
            ? null
            : new HttpRequestException(
                $"Provider '{providerId}' reported an error in the middle of its answer: {said}. "
                + "Nothing it sent before the error is acted on.");
    }

    /// <summary>The exception for a stream that stopped without saying it had finished.</summary>
    public static HttpRequestException Unfinished(string providerId, string expected)
        => new($"Provider '{providerId}' stopped sending before its answer was finished (no {expected}). "
               + "The answer is incomplete, and nothing in it is acted on.");
}
