namespace Enactive.Engine.Tests;

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Enactive.Core.Chat;
using Enactive.Core.Providers;
using Enactive.Providers;
using Xunit;

/// <summary>
/// What a run says when the model could not be reached.
///
/// <para>It used to say what the operating system said and nothing more: "No connection could be
/// made because the target machine actively refused it. (127.0.0.1:9)". No provider, no model, no
/// setting to go and look at - handed to somebody who had asked a question about their work. An AI
/// workspace can be vague about many things; the AI not answering is not one of them.</para>
/// </summary>
public sealed class ProviderTroubleTests
{
    private const string Url = "http://localhost:11434/v1";

    private static string? Explain(Exception error, string model = "qwen2.5-coder")
        => ProviderTrouble.Explain("ollama", model, Url, error);

    /// <summary>The commonest one by a distance: the local server is not running.</summary>
    [Fact]
    public void A_refused_connection_says_nothing_is_listening()
    {
        var refused = new HttpRequestException(
            HttpRequestError.ConnectionError, "whatever the OS said",
            new SocketException((int)SocketError.ConnectionRefused));

        var said = Explain(refused);

        Assert.NotNull(said);
        Assert.Contains("not running", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Url, said);
        Assert.Contains("qwen2.5-coder", said);
    }

    /// <summary>
    /// Read from the exception's TYPE, never its text.
    ///
    /// <para>The operating system's message is translated. A check looking for "actively refused"
    /// works on an English Windows and silently stops recognising the commonest failure there is on
    /// every other one - and the failure it stops recognising is the one where the product does
    /// nothing at all.</para>
    /// </summary>
    [Fact]
    public void The_operating_systems_language_does_not_change_the_answer()
    {
        var english = new HttpRequestException(
            HttpRequestError.ConnectionError,
            "No connection could be made because the target machine actively refused it. (127.0.0.1:9)",
            new SocketException((int)SocketError.ConnectionRefused));

        var russian = new HttpRequestException(
            HttpRequestError.ConnectionError,
            "Подключение не установлено, т.к. конечный компьютер отверг запрос на подключение. (127.0.0.1:9)",
            new SocketException((int)SocketError.ConnectionRefused));

        Assert.Equal(Explain(english), Explain(russian));
    }

    [Fact]
    public void An_address_that_resolves_to_nothing_says_so()
    {
        var said = Explain(new HttpRequestException(
            HttpRequestError.NameResolutionError, "no such host"));

        Assert.NotNull(said);
        Assert.Contains("resolved", said, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A key problem is a key problem, and the body that says so is not on screen.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void A_refused_key_points_at_the_key(HttpStatusCode status)
    {
        var said = Explain(new HttpRequestException("...", null, status));

        Assert.NotNull(said);
        Assert.Contains("key", said, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 404 is genuinely ambiguous - a model that is not pulled, or an address that is not the API
    /// root - so it says both rather than picking one. An explanation that names the wrong cause
    /// sends somebody to debug the wrong thing.
    /// </summary>
    [Fact]
    public void A_404_names_both_things_it_can_mean()
    {
        var said = Explain(new HttpRequestException("...", null, HttpStatusCode.NotFound), "gemma4:31b");

        Assert.NotNull(said);
        Assert.Contains("gemma4:31b", said);
        Assert.Contains("address", said, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A timeout arrives as a cancellation nobody asked for. A run the PERSON stopped is a
    /// different fact and must never be reported as the model being slow.
    /// </summary>
    [Fact]
    public void A_timeout_is_not_reported_as_a_cancellation()
    {
        var timedOut = new TaskCanceledException("...", new TimeoutException());

        var said = Explain(timedOut);

        Assert.NotNull(said);
        Assert.Contains("did not answer in time", said);

        // And a real cancellation gets no explanation at all - it is not trouble.
        Assert.Null(Explain(new TaskCanceledException("stopped")));
        Assert.Null(Explain(new OperationCanceledException()));
    }

    /// <summary>
    /// Nothing to add means null, not a sentence. A guess dressed as an explanation is worse than
    /// the original error, because the person then debugs the guess.
    /// </summary>
    [Fact]
    public void An_error_it_does_not_recognise_is_left_alone()
    {
        Assert.Null(Explain(new InvalidOperationException("something else entirely")));
        Assert.Null(Explain(new HttpRequestException("...", null, HttpStatusCode.InternalServerError)));
    }

    /// <summary>A provider with no address of its own is still named, without a dangling "at".</summary>
    [Fact]
    public void A_provider_without_an_address_reads_properly()
    {
        var said = ProviderTrouble.Explain(
            "anthropic", "claude", null,
            new HttpRequestException("...", null, HttpStatusCode.Unauthorized));

        Assert.NotNull(said);
        Assert.DoesNotContain(" at ,", said);
        Assert.DoesNotContain("at .", said);
        Assert.Contains("anthropic", said);
    }
}

/// <summary>
/// The seam: the explanation reaching a real call, and a cancellation surviving it untouched.
/// </summary>
public sealed class ExplainedProviderTests
{
    private sealed class Throws(Exception error) : IChatProvider
    {
        public int? ContextWindow(ChatRequest request) => null;

        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
            => throw error;

        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            // One event first: the failure being explained happens DURING a stream at least as
            // often as at its start, and a decorator that only guarded the first step would miss
            // a local server dying halfway through an answer.
            yield return new TextDelta("thinking");
            await Task.Yield();
            throw error;
        }
    }

    private static readonly ChatRequest Ask =
        new("qwen2.5-coder", new[] { ChatMessage.User("hello") });

    private static ExplainedChatProvider Wrapping(Exception error)
        => new(new Throws(error), "ollama", "http://localhost:11434/v1");

    private static HttpRequestException Refused() => new(
        HttpRequestError.ConnectionError, "the OS text",
        new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));

    [Fact]
    public async Task A_completion_failure_arrives_explained()
    {
        var thrown = await Assert.ThrowsAsync<ProviderUnreachableException>(
            () => Wrapping(Refused()).CompleteAsync(Ask, default));

        Assert.Contains("not running", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<HttpRequestException>(thrown.InnerException);
    }

    /// <summary>A stream that dies partway through is explained too, not only one that never starts.</summary>
    [Fact]
    public async Task A_stream_that_fails_partway_arrives_explained()
    {
        var provider = Wrapping(Refused());

        var thrown = await Assert.ThrowsAsync<ProviderUnreachableException>(async () =>
        {
            await foreach (var _ in provider.StreamChatAsync(Ask, default)) { }
        });

        Assert.Contains("not running", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The decisive one. A timeout is not a cancellation, and it used to be reported as one: the
    /// orchestrator's fallback and error path both step aside for an OperationCanceledException, so
    /// a model that never answered ended the run looking exactly like somebody pressing Stop.
    /// </summary>
    [Fact]
    public async Task A_timeout_stops_being_a_cancellation()
    {
        var timedOut = new TaskCanceledException("...", new TimeoutException());

        var thrown = await Assert.ThrowsAsync<ProviderUnreachableException>(
            () => Wrapping(timedOut).CompleteAsync(Ask, default));

        Assert.IsNotAssignableFrom<OperationCanceledException>(thrown);
        Assert.Contains("did not answer in time", thrown.Message);
    }

    /// <summary>
    /// And a cancellation the person asked for stays exactly what it is. The token decides, not the
    /// shape of the exception - which is the only thing that can tell these two apart.
    /// </summary>
    [Fact]
    public async Task A_run_that_was_stopped_is_still_a_cancellation()
    {
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => Wrapping(new TaskCanceledException("stopped")).CompleteAsync(Ask, stopped.Token));
    }

    /// <summary>An error it cannot explain passes through as itself, with its own type.</summary>
    [Fact]
    public async Task An_unrecognised_failure_is_not_rewritten()
        => await Assert.ThrowsAsync<InvalidOperationException>(
            () => Wrapping(new InvalidOperationException("something else")).CompleteAsync(Ask, default));
}
