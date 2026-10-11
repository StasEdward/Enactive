namespace Enactive.Server;

using Enactive.Core.Diagnostics;
using Enactive.Workspace;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using LogLevel = Enactive.Core.Diagnostics.LogLevel;

/// <summary>
/// The service's own log in the same daily file the engine writes (FileLogSink: one file a day, old days pruned),
/// so a run and the request that started it read in one place. The Event Log gets what the Windows service host
/// sends it; a file is what somebody reads afterwards.
/// </summary>
internal sealed class FileLoggerProvider(ILogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Writer(sink, categoryName);

    public void Dispose() { }

    private sealed class Writer(ILogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Information and up: the framework's Debug lines are per request and would bury the runs.
        public bool IsEnabled(MsLogLevel level) => level >= MsLogLevel.Information && level != MsLogLevel.None;

        public void Log<TState>(MsLogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level))
                return;
            var mapped = level switch
            {
                MsLogLevel.Information => LogLevel.Info,
                MsLogLevel.Warning => LogLevel.Warn,
                _ => LogLevel.Error
            };
            sink.Log(new LogEntry(0, DateTimeOffset.Now, mapped, LogSource.Host, null, null,
                formatter(state, exception), exception?.ToString(), category));
        }
    }
}
