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
/// order and de-duplicate regardless of thread timing. RunId/TaskId carry run correlation and Step
/// the plan step that produced the line, all filled from the ambient <see cref="LogScope"/> at write
/// time; Step is what makes a parallel run readable, since several steps interleave their prompts and
/// responses in one file. Detail holds the heavy payload (a full prompt, a raw response body, a
/// tool's arguments) that the UI shows only when a row is expanded.
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
    string?         Category,
    int?            Step = null);

/// <summary>
/// The write side of the log. Everything that wants to log holds one of these and nothing more —
/// implementations (the hub, a file sink, a fan-out) live outside Core. Implementations MUST be
/// thread-safe and MUST NOT throw: logging can never break the operation it is observing.
/// </summary>
public interface ILogSink
{
    void Log(LogEntry entry);
}

/// <summary>
/// A log whose history is read back later - the log window, the export, AI Analyze - and does not
/// keep everything: it is cleared, and its oldest entries give way to new ones. It can say whether
/// entries it accepted are still there, so a writer whose entry only makes sense next to an earlier
/// one (a prompt logged as "the previous prompt, plus these messages") can tell when that earlier one
/// is gone and write the whole thing again. A log that keeps everything does not implement this.
/// </summary>
public interface ILogHistory
{
    /// <summary>True when every one of these details - the very strings logged, compared by
    /// reference - is still in the history.</summary>
    bool HoldsAll(IReadOnlyCollection<string> details);
}

/// <summary>A sink that drops everything. The default when no logging is wired up.</summary>
public sealed class NullLogSink : ILogSink
{
    public static readonly NullLogSink Instance = new();
    private NullLogSink() { }
    public void Log(LogEntry entry) { }
}

/// <summary>
/// Ambient run correlation. A scope is opened around the work of a run (and, inside it, of a single
/// plan step); every nested async call — provider, tool, planner, reviewer — then reads the current
/// ids without them being threaded through method signatures. Uses <see cref="AsyncLocal{T}"/> so the
/// value flows with the async call chain and is isolated per run, and per step, even when several
/// overlap.
///
/// IMPORTANT: an async ITERATOR resumes on its consumer's execution context after every
/// <c>yield return</c>, so a scope opened once at the top of one is gone from the second segment
/// onwards — that is why the orchestrator opens the scope around the work (the planner call, the step
/// pump) rather than once around its event stream. Do not "simplify" it back.
/// </summary>
public static class LogScope
{
    private static readonly AsyncLocal<Frame?> _current = new();

    public static (Guid Run, Guid Task, int? Step)? Current
        => _current.Value is { } f ? (f.Run, f.Task, f.Step) : null;

    /// <summary>Opens a scope for a run, optionally narrowed to one plan step.</summary>
    public static IDisposable Begin(Guid run, Guid task, int? step = null)
    {
        var previous = _current.Value;
        _current.Value = new Frame(run, task, step);
        return new Pop(previous);
    }

    private sealed record Frame(Guid Run, Guid Task, int? Step);

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
                Category: category,
                Step: run?.Step));
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
