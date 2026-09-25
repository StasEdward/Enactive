namespace Enactive.Providers;

using Enactive.Core.Diagnostics;

/// <summary>
/// Raw HTTP body logging for the providers, bounded with an explicit truncation marker. Streaming
/// captures a prefix of decoded SSE/JSONL lines. It lives at Trace level (off unless the log window
/// raises the threshold) because it is verbose. Only the request/response BODIES are logged — never
/// headers — so the Authorization / x-api-key secrets never reach the log.
/// </summary>
internal static class WireTap
{
    public static void Request(ILogSink? log, string providerId, string model, string bodyJson)
    {
        if (log.IsLoggingEnabled(LogLevel.Trace))
            log.Trace(LogSource.Prompt, $"→ HTTP {providerId}/{model} request",
                LogPayload.Limit(bodyJson, LogPayload.MaxEntryBytes), "wire:request");
    }

    public static void Response(ILogSink? log, string providerId, int status, string body, bool streamed = false)
    {
        if (!log.IsLoggingEnabled(LogLevel.Trace)) return;
        log.Trace(
            LogSource.Llm,
            $"← HTTP {providerId} {status}{(streamed ? " (stream)" : "")}",
            LogPayload.Limit(body, LogPayload.MaxEntryBytes),
            streamed ? "wire:response-stream" : "wire:response");
    }

    public static void Error(ILogSink? log, string providerId, int status, string body)
        => log.Error(LogSource.Llm, $"← HTTP {providerId} {status} error",
            LogPayload.Limit(body, LogPayload.MaxEntryBytes), "wire:error");
}
