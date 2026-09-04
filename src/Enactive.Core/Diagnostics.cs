namespace Enactive.Core.Diagnostics;

/// <summary>Severity of a log entry. The global log window filters on this.</summary>
public enum LogLevel { Trace, Debug, Info, Warn, Error }

/// <summary>Where a log entry originates. Drives colour/grouping in the log window.</summary>
public enum LogSource
{
    System,        // host lifecycle, configuration
    Host,          // console/UI host actions
    Orchestrator,  // run routing, plan, steps (bridged from WorkEvent)
    Planner,       // plan generation
    Reviewer,      // multi-agent review verdicts
    Permission,    // permission decisions
    Prompt,        // what is sent DOWN to the model (rendered + raw wire request)
    Llm,           // what the model sends back (assembled + raw wire response)
    Tool,          // tool invocations and results
    Store          // run-store persistence
}

/// <summary>
/// One line in the global log. Immutable. Seq is a monotonic id assigned by the hub so the UI can
/// order and de-duplicate regardless of thread timing. RunId/TaskId carry run correlation, filled
/// from the ambient <see cref="LogScope"/> at write time. Detail holds the heavy payload (a full
/// prompt, a raw response body, a tool's arguments) that the UI shows only when a row is expanded.
/// </summary>
public sealed record LogEntry(
    long            Seq,
    DateTimeOffset  At,
    LogLevel        Level,
    LogSource       Source,
    Guid?           RunId,
    Guid?           TaskId,
    string          Message,
    string?         Detail,
    string?         Category);

/// <summary>
/// The write side of the log. Everything that wants to log holds one of these and nothing more —
/// implementations (the hub, a file sink, a fan-out) live outside Core. Implementations MUST be
/// thread-safe and MUST NOT throw: logging can never break the operation it is observing.
/// </summary>
public interface ILogSink
{
    void Log(LogEntry entry);
}

/// <summary>A sink that drops everything. The default when no logging is wired up.</summary>
public sealed class NullLogSink : ILogSink
{
    public static readonly NullLogSink Instance = new();
    private NullLogSink() { }
    public void Log(LogEntry entry) { }
}

/// <summary>
/// Ambient run correlation. The orchestrator opens a scope for the duration of a run; every nested
/// async call (provider, tool, planner, reviewer) then reads the current run id without it being
/// threaded through method signatures. Uses <see cref="AsyncLocal{T}"/> so the value flows with the
/// async call chain and is isolated per run even when runs overlap.
/// </summary>
public static class LogScope
{
    private static readonly AsyncLocal<Frame?> _current = new();

    public static (Guid Run, Guid Task)? Current
        => _current.Value is { } f ? (f.Run, f.Task) : null;

    public static IDisposable Begin(Guid run, Guid task)
    {
        var previous = _current.Value;
        _current.Value = new Frame(run, task);
        return new Pop(previous);
    }

    private sealed record Frame(Guid Run, Guid Task);

    private sealed class Pop : IDisposable
    {
        private readonly Frame? _previous;
        private bool _done;
        public Pop(Frame? previous) => _previous = previous;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _current.Value = _previous;
        }
    }
}

/// <summary>
/// Ergonomic, null-safe, never-throwing write helpers. Producers call <c>sink.Write(...)</c>; the
/// run/task ids are pulled from the ambient <see cref="LogScope"/> automatically. Seq is left 0 here
/// and assigned by the hub.
/// </summary>
public static class LogSinkExtensions
{
    public static void Write(
        this ILogSink? sink,
        LogLevel level,
        LogSource source,
        string message,
        string? detail = null,
        string? category = null)
    {
        if (sink is null) return;
        try
        {
            var run = LogScope.Current;
            sink.Log(new LogEntry(
                Seq: 0,
                At: DateTimeOffset.UtcNow,
                Level: level,
                Source: source,
                RunId: run?.Run,
                TaskId: run?.Task,
                Message: message,
                Detail: detail,
                Category: category));
        }
        catch
        {
            // Logging must never break the caller.
        }
    }

    public static void Trace(this ILogSink? sink, LogSource source, string message, string? detail = null, string? category = null)
        => sink.Write(LogLevel.Trace, source, message, detail, category);

    public static void Info(this ILogSink? sink, LogSource source, string message, string? detail = null, string? category = null)
        => sink.Write(LogLevel.Info, source, message, detail, category);

    public static void Warn(this ILogSink? sink, LogSource source, string message, string? detail = null, string? category = null)
        => sink.Write(LogLevel.Warn, source, message, detail, category);

    public static void Error(this ILogSink? sink, LogSource source, string message, string? detail = null, string? category = null)
        => sink.Write(LogLevel.Error, source, message, detail, category);
}
