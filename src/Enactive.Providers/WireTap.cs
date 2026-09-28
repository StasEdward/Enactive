namespace Enactive.Providers;

using Enactive.Core.Diagnostics;

/// <summary>
/// Raw HTTP body logging for the providers, bounded with an explicit truncation marker. Streaming
/// captures a prefix of decoded SSE/JSONL lines. It lives at Trace level (off unless the log window
/// raises the threshold) because it is verbose. Only the request/response BODIES are logged — never
/// headers — so the Authorization / x-api-key secrets never reach the log.
///
/// <para><b>Whole bodies, when asked.</b> The log keeps a bounded prefix, and a request to a local model
/// with a nearly full window is larger than that - cut exactly where the latest messages and the tool
/// schemas are. With <c>ENACTIVE_WIRE_DUMP</c> set to a folder, every body is also written there whole,
/// one file each, in the order they were sent. Bodies only, as above.</para>
/// </summary>
internal static class WireTap
{
    private static int _sequence;

    public static void Request(ILogSink? log, string providerId, string model, string bodyJson)
    {
        Dump($"{providerId}-{model}-request", bodyJson);
        if (log.IsLoggingEnabled(LogLevel.Trace))
            log.Trace(LogSource.Prompt, $"→ HTTP {providerId}/{model} request",
                LogPayload.Limit(bodyJson, LogPayload.MaxEntryBytes), "wire:request");
    }

    public static void Response(ILogSink? log, string providerId, int status, string body, bool streamed = false)
    {
        Dump($"{providerId}-{status}-response{(streamed ? "-stream" : "")}", body);
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

    /// <summary>The whole body, to the folder ENACTIVE_WIRE_DUMP names - or nothing. Never fails a call.</summary>
    private static void Dump(string what, string body)
    {
        if (Environment.GetEnvironmentVariable("ENACTIVE_WIRE_DUMP") is not { Length: > 0 } dir) return;
        try
        {
            Directory.CreateDirectory(dir);
            var safe = string.Concat(what.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or ':' ? '_' : c));
            File.WriteAllText(Path.Combine(dir, $"{Interlocked.Increment(ref _sequence):D5}-{DateTime.Now:HHmmss.fff}-{safe}.json"), body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
