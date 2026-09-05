namespace Enactive.Workspace;

using System.Threading.Channels;
using Enactive.Core.Diagnostics;

/// <summary>
/// The in-memory heart of the global log. Every producer writes here through <see cref="ILogSink"/>.
/// The hub (1) stamps a monotonic <c>Seq</c>, (2) keeps a bounded ring buffer so a window opened later
/// still sees recent history, and (3) fans entries out to live subscribers and any downstream sinks
/// (e.g. a file) on a single background pump thread so the producer's hot path is never blocked.
/// One instance per application; it outlives individual runs, which is what makes the log "global".
/// </summary>
public sealed class LogHub : ILogSink, IDisposable
{
    private readonly int _capacity;
    private readonly Queue<LogEntry> _ring;
    private readonly object _gate = new();
    private readonly Channel<LogEntry> _channel;
    private readonly ILogSink[] _downstream;
    private readonly Task _pump;
    private long _seq;
    private volatile LogLevel _minLevel;

    /// <summary>Raised on the pump thread for every accepted entry. UI subscribers must marshal to
    /// their own thread. Backlog for a late subscriber comes from <see cref="Snapshot"/>.</summary>
    public event Action<LogEntry>? Entry;

    public LogHub(int capacity = 5000, LogLevel minLevel = LogLevel.Trace, params ILogSink[] downstream)
    {
        _capacity = Math.Max(64, capacity);
        _ring = new Queue<LogEntry>(_capacity);
        _minLevel = minLevel;
        _downstream = downstream ?? Array.Empty<ILogSink>();
        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(Math.Max(1024, _capacity * 2))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Entries below this level are dropped at the door (cheap). The window raises/lowers it
    /// to turn the noisy Trace-level raw-wire dumps on and off.</summary>
    public LogLevel MinLevel
    {
        get => _minLevel;
        set => _minLevel = value;
    }

    public void Log(LogEntry raw)
    {
        if (raw.Level < _minLevel)
            return;

        var entry = raw with { Seq = Interlocked.Increment(ref _seq) };

        // Ring is updated synchronously so Snapshot() is always consistent with what was accepted.
        lock (_gate)
        {
            _ring.Enqueue(entry);
            while (_ring.Count > _capacity)
                _ring.Dequeue();
        }

        // Fan-out is async and best-effort; a slow subscriber must never stall a producer.
        _channel.Writer.TryWrite(entry);
    }

    /// <summary>A copy of the current ring buffer, oldest first — the backlog a new window replays.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
            return _ring.ToArray();
    }

    /// <summary>Clears the in-memory ring (does not touch any file sink).</summary>
    public void Clear()
    {
        lock (_gate)
            _ring.Clear();
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync())
            {
                var handler = Entry;
                if (handler is not null)
                {
                    try { handler(entry); }
                    catch { /* a subscriber's fault is not the hub's problem */ }
                }

                foreach (var sink in _downstream)
                {
                    try { sink.Log(entry); }
                    catch { /* keep pumping even if a downstream sink fails */ }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Hub disposed.
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try { _pump.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* ignore */ }
        foreach (var sink in _downstream)
            (sink as IDisposable)?.Dispose();
    }
}

/// <summary>Fans one entry out to several sinks, swallowing any individual sink's failure.</summary>
public sealed class CompositeLogSink : ILogSink, IDisposable
{
    private readonly ILogSink[] _sinks;
    public CompositeLogSink(params ILogSink[] sinks) => _sinks = sinks ?? Array.Empty<ILogSink>();

    public void Log(LogEntry entry)
    {
        foreach (var sink in _sinks)
        {
            try { sink.Log(entry); }
            catch { /* isolate sink failures */ }
        }
    }

    public void Dispose()
    {
        foreach (var sink in _sinks)
            (sink as IDisposable)?.Dispose();
    }
}

/// <summary>
/// Appends the log to a daily-rotated text file under a directory (default
/// <c>%APPDATA%/Enactive/logs</c>), so a crash still leaves a full trace on disk. Writes are
/// serialized and never throw. Detail payloads are written indented beneath their entry line.
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly bool _includeDetail;
    private string? _currentPath;
    private StreamWriter? _writer;
    private DateOnly _currentDay;

    public FileLogSink(string? directory = null, bool includeDetail = true)
    {
        _directory = directory ?? DefaultDirectory();
        _includeDetail = includeDetail;
        try { Directory.CreateDirectory(_directory); } catch { /* ignore */ }
    }

    public static string DefaultDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(root, "Enactive", "logs");
    }

    public void Log(LogEntry entry)
    {
        try
        {
            lock (_gate)
            {
                RollIfNeeded(entry.At);
                if (_writer is null) return;

                // "run=1a2b3c4d#2" - the step suffix is what makes parallel steps separable in one file.
                var run = entry.RunId is { } r ? r.ToString("N")[..8] : "--------";
                if (entry.Step is { } stepNo)
                    run += "#" + stepNo;
                run = run.PadRight(11);   // keep the columns aligned whether or not a line has a step
                var cat = string.IsNullOrEmpty(entry.Category) ? "" : $" [{entry.Category}]";
                _writer.WriteLine(
                    $"{entry.At.ToLocalTime():HH:mm:ss.fff} {entry.Level,-5} {entry.Source,-12} run={run}{cat} {entry.Message}");

                if (_includeDetail && !string.IsNullOrEmpty(entry.Detail))
                {
                    foreach (var line in entry.Detail.Replace("\r\n", "\n").Split('\n'))
                        _writer.WriteLine("    | " + line);
                }
                _writer.Flush();
            }
        }
        catch
        {
            // A logging file error must never surface to the caller.
        }
    }

    private void RollIfNeeded(DateTimeOffset at)
    {
        var day = DateOnly.FromDateTime(at.ToLocalTime().DateTime);
        if (_writer is not null && day == _currentDay)
            return;

        _writer?.Flush();
        _writer?.Dispose();
        _currentDay = day;
        _currentPath = Path.Combine(_directory, $"enactive-{day:yyyyMMdd}.log");
        _writer = new StreamWriter(new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
    }

    public string? CurrentPath { get { lock (_gate) return _currentPath; } }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}
