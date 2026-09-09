namespace Enactive.Providers;

using System.Runtime.CompilerServices;
using Enactive.Core.Chat;
using Enactive.Core.Providers;

/// <summary>
/// Puts a readable sentence on a provider failure, on the way out.
///
/// <para>Applied to every provider ALWAYS, unlike <see cref="LoggingChatProvider"/>, which is only
/// wrapped when logging is on. Turning the log off must not make the errors worse - and the person
/// most likely to have turned it off is the one least likely to go looking in it.</para>
///
/// <para><b>A cancellation is never rewritten.</b> A run the person stopped and a model that never
/// answered are different facts, and the second used to be reported as the first: HttpClient's
/// timeout arrives as a TaskCanceledException, the orchestrator's fallback path steps aside for
/// anything that is an OperationCanceledException, and the run ended looking as though somebody had
/// pressed Stop. The token says which it was, so the token is what decides.</para>
/// </summary>
public sealed class ExplainedChatProvider(IChatProvider inner, string providerId, string? baseUrl)
    : IChatProvider
{
    public int? ContextWindow(ChatRequest request) => inner.ContextWindow(request);

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // A manual walk of the enumerator: a yield cannot sit inside a try that has a catch, and
        // the failure being explained happens DURING the stream as often as at its start - a local
        // server that dies halfway through a long answer is exactly this case.
        await using var steps = inner.StreamChatAsync(request, ct).GetAsyncEnumerator(ct);

        while (true)
        {
            bool moved;

            try
            {
                moved = await steps.MoveNextAsync();
            }
            catch (Exception ex)
            {
                throw Explained(ex, request, ct);
            }

            if (!moved) yield break;

            yield return steps.Current;
        }
    }

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        try
        {
            return await inner.CompleteAsync(request, ct);
        }
        catch (Exception ex)
        {
            throw Explained(ex, request, ct);
        }
    }

    /// <summary>
    /// The same failure with a sentence somebody can act on, or the original untouched.
    ///
    /// <para>The original is always the inner exception: the operating system's text and the stack
    /// are what a log is read for, and an explanation that replaced them would trade one kind of
    /// blindness for another.</para>
    /// </summary>
    private Exception Explained(Exception error, ChatRequest request, CancellationToken ct)
    {
        // Asked to stop, and it stopped. Nothing to explain.
        if (ct.IsCancellationRequested)
            return error;

        var said = ProviderTrouble.Explain(providerId, request.Model, baseUrl, error);

        return said is null ? error : new ProviderUnreachableException(said, error);
    }
}

/// <summary>
/// A provider failure that has been explained.
///
/// <para>An HttpRequestException so that every existing catch keeps matching - including the
/// console host's, which prints a provider-connection message and exits with a code a scheduler
/// reads. A new exception type there would have fallen through to the backstop and become a stack
/// trace.</para>
///
/// <para>It deliberately does NOT derive from OperationCanceledException even when the failure was
/// a timeout, because that is the whole point: a timeout is trouble to be retried and reported, not
/// a person pressing Stop.</para>
/// </summary>
public sealed class ProviderUnreachableException(string message, Exception inner)
    : HttpRequestException(message, inner);
