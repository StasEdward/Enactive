namespace Enactive.Providers;

using System.Net;
using System.Runtime.CompilerServices;
using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>At most three attempts. A stream is never restarted after any event was delivered.</summary>
public sealed class ResilientChatProvider(IChatProvider inner, ILogSink? log = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IChatProvider
{
    public int? ContextWindow(ChatRequest request) => inner.ContextWindow(request);
    public int? AnswerReserve(ChatRequest request) => inner.AnswerReserve(request);
    public int? HandoverAtPercent(ChatRequest request) => inner.HandoverAtPercent(request);
    public int ReasoningAllowance(ChatRequest request) => inner.ReasoningAllowance(request);

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await inner.CompleteAsync(request, ct).ConfigureAwait(false); }
            catch (Exception ex) when (attempt < 2 && Retryable(ex) && !ct.IsCancellationRequested)
            { await Pause(request, ex, attempt, ct).ConfigureAwait(false); }
        }
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            Exception? retry = null;
            await using (var events = inner.StreamChatAsync(request, ct).GetAsyncEnumerator(ct))
            {
                var delivered = false;
                while (true)
                {
                    bool moved;
                    try { moved = await events.MoveNextAsync().ConfigureAwait(false); }
                    catch (Exception ex) when (!delivered && attempt < 2 && Retryable(ex) && !ct.IsCancellationRequested)
                    { retry = ex; break; }
                    if (!moved) yield break;
                    delivered = true;
                    yield return events.Current;
                }
            }
            await Pause(request, retry!, attempt, ct).ConfigureAwait(false);
        }
    }

    private static bool Retryable(Exception error) => error switch
    {
        HttpRequestException http => http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or (HttpStatusCode)529
            || http.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                or HttpRequestError.ResponseEnded,
        HttpIOException http => http.HttpRequestError is HttpRequestError.ResponseEnded or HttpRequestError.ConnectionError,
        // Malformed protocol/data and local file errors are not transient network reads.
        IOException io => io.GetType() == typeof(IOException) || io is EndOfStreamException,
        _ => false
    };

    private async Task Pause(ChatRequest request, Exception error, int attempt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CheckBudget(request, error);
        var wait = error.Data[ProviderHttpError.RetryAfterKey] is TimeSpan specified
            ? specified : TimeSpan.FromMilliseconds(500 * (1 << attempt) + Random.Shared.Next(100, 300));
        // A very long Retry-After is not permission to retry earlier than the server requested.
        if (wait > TimeSpan.FromSeconds(30)) throw error;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        log.Warn(LogSource.Llm, $"Transient provider failure; retry {attempt + 1}/2 in {wait.TotalSeconds:F1}s (HTTP {(int?)(error as HttpRequestException)?.StatusCode}).");
        await (delay ?? Task.Delay)(wait, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        CheckBudget(request, error);
    }

    private static void CheckBudget(ChatRequest request, Exception error)
    {
        if (request.RetryBudget?.TurnExhausted is { } reason)
            throw new RetryBudgetExceededException(reason, error);
    }
}
