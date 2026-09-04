namespace Enactive.Providers;

using Enactive.Core.Diagnostics;

/// <summary>
/// Raw HTTP wire logging for the providers. This is the byte-level plane: the exact JSON body sent
/// to the model and the exact bytes it sends back. It lives at Trace level (off unless the log window
/// raises the threshold) because it is verbose. Only the request/response BODIES are logged — never
/// headers — so the Authorization / x-api-key secrets never reach the log.
/// </summary>
internal static class WireTap
{
    public static void Request(ILogSink? log, string providerId, string model, string bodyJson)
        => log.Trace(LogSource.Prompt, $"→ HTTP {providerId}/{model} request", bodyJson, "wire:request");

    public static void Response(ILogSink? log, string providerId, int status, string body, bool streamed = false)
        => log.Trace(
            LogSource.Llm,
            $"← HTTP {providerId} {status}{(streamed ? " (stream)" : "")}",
            body,
            streamed ? "wire:response-stream" : "wire:response");

    public static void Error(ILogSink? log, string providerId, int status, string body)
        => log.Error(LogSource.Llm, $"← HTTP {providerId} {status} error", body, "wire:error");
}
