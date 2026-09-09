namespace Enactive.Providers;

using System.Net;
using System.Net.Http;
using System.Net.Sockets;

/// <summary>
/// What to say when the model could not be reached.
///
/// <para><b>Why this exists.</b> A run whose provider is unreachable failed with the sentence the
/// operating system produced and nothing else:</para>
///
/// <code>FAILED: No connection could be made because the target machine actively refused it. (127.0.0.1:9)</code>
///
/// <para>That names no provider, no model and no setting. It is a true statement about a socket
/// offered to somebody who asked a question about their work, and the one thing an AI workspace
/// cannot afford to be vague about is the AI not answering: everything else in the product is
/// downstream of that call.</para>
///
/// <para><b>Read from the exception's TYPE, never its text.</b> <see cref="HttpRequestException"/>
/// carries <see cref="HttpRequestException.HttpRequestError"/> and a <see cref="SocketException"/>
/// with a <see cref="SocketError"/>; those are stable. The message is written by the operating
/// system and is TRANSLATED - a Russian or German Windows says "Подключение не установлено" or
/// "Es konnte keine Verbindung hergestellt werden", and a check that looked for "actively refused"
/// would quietly stop recognising the commonest failure there is on most of the machines this runs
/// on.</para>
///
/// <para>Returns null when there is nothing to add. A guess dressed as an explanation is worse
/// than the original error, because the person then debugs the guess.</para>
/// </summary>
public static class ProviderTrouble
{
    /// <summary>
    /// A sentence naming what could not be reached and what to look at, or null to leave the
    /// original error alone.
    /// </summary>
    /// <param name="providerId">The provider as configured - "ollama", "anthropic".</param>
    /// <param name="model">The model that was asked for.</param>
    /// <param name="baseUrl">Where it was asked, when the provider has an address.</param>
    public static string? Explain(string providerId, string model, string? baseUrl, Exception error)
    {
        var at = string.IsNullOrWhiteSpace(baseUrl) ? "" : $" at {baseUrl}";
        var who = $"{providerId}/{model}";

        return error switch
        {
            // The endpoint answered the connection with a refusal: nothing is listening there.
            // Overwhelmingly this is a local server that is not running.
            HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } e
                when Socket(e) is SocketError.ConnectionRefused =>
                $"Nothing is listening{at}, so {who} could not be asked. "
                + "If that is a local model server, it is not running.",

            // Reached nobody at all - the address exists as text and not as a machine.
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
                $"The address{at} could not be resolved, so {who} could not be asked. "
                + "Check the host name in this provider's settings.",

            HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } =>
                $"Could not connect{at}, so {who} could not be asked. "
                + "Check that the service is running and that the address and port are right.",

            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
                $"The secure connection{at} failed, so {who} could not be asked. "
                + "Check whether the address should be http rather than https, and whether a "
                + "proxy or antivirus is intercepting it.",

            // 401 and 403 are about the credential and nothing else, and saying so saves the
            // person reading a body that says "invalid x-api-key" in a field they cannot see.
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
                $"{providerId} refused the API key, so {model} could not be asked. "
                + "Check the key in Settings → Providers.",

            HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                $"{providerId} answered 404{at}. Either the model '{model}' is not installed there, "
                + "or the address is not the API root this provider expects.",

            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
                $"{providerId} is rate-limiting this key, so {model} could not be asked right now.",

            // The address was never usable. Found once already, from a port typed wrong: it threw
            // out of the provider during planning and past every catch that named a provider.
            UriFormatException =>
                $"'{baseUrl}' is not a usable address, so {who} could not be asked. "
                + "Check the endpoint in Settings → Providers.",

            // A timeout from HttpClient arrives as a cancellation that nobody requested. A run the
            // person stopped is a different thing entirely and must not be reported as this.
            TaskCanceledException { InnerException: TimeoutException } =>
                $"{who} did not answer in time{at}. A local model that is loading for the first "
                + "time can exceed the request timeout; a second attempt often succeeds.",

            _ => null
        };
    }

    /// <summary>The socket error underneath, when there is one.</summary>
    private static SocketError? Socket(Exception error)
    {
        for (var e = error.InnerException; e is not null; e = e.InnerException)
            if (e is SocketException socket)
                return socket.SocketErrorCode;

        return null;
    }
}
