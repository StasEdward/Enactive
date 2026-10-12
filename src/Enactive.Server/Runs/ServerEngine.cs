namespace Enactive.Server.Runs;

using Enactive.Core.Permissions;
using Enactive.Settings;
using Enactive.Workspace;

/// <summary>
/// The engine the service runs on: built from the service account's own <c>settings.json</c> by the composition every
/// host shares (EngineComposition), with that account's remembered approvals and workspace list. Its absence is a
/// state, said where a run is asked for - a service whose account has no model configured yet still starts, signs
/// people in and shows the history, and an Admin configures it from the settings page.
/// </summary>
internal sealed class ServerEngine : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly LogHub _log;
    private readonly object _gate = new();
    private (ComposedEngine? Engine, string? Problem, AppSettings Settings) _now;

    /// <summary>A test's: an engine with a log of its own, into this file.</summary>
    public ServerEngine(string dataFolder, FileLogSink logFile)
        : this(dataFolder, new LogHub(minLevel: Core.Diagnostics.LogLevel.Debug, downstream: [logFile]))
    {
    }

    /// <param name="log">The service's one log - its own lines and the engine's, so a run and the request that started it read in one place.</param>
    public ServerEngine(string dataFolder, LogHub log)
    {
        _log = log;
        SettingsFile = Path.Combine(dataFolder, "settings.json");
        Approvals = new ApprovalStore(Path.Combine(dataFolder, "permissions.json"));
        Workspaces = WorkspaceRegistry.Load(filePath: Path.Combine(dataFolder, "workspaces.json"));
        Schedules = new Core.Schedules.ScheduleStore(Path.Combine(dataFolder, "schedules.json"));
        Markers = new Core.Schedules.RunMarkers(Path.Combine(dataFolder, "running"));
        Reload();
    }

    /// <summary>The service account's settings file - the same one its desktop would use, under the same account.</summary>
    public string SettingsFile { get; }

    /// <summary>
    /// Reads the settings again and builds the engine from them: after the settings page saved them. A run already
    /// going keeps the engine it was composed on; the next one is composed on this.
    /// </summary>
    public void Reload()
    {
        var settings = AppSettings.Load(SettingsFile);
        ComposedEngine? engine = null;
        string? problem = null;
        if (EngineComposition.Missing(settings) is { Count: > 0 } missing)
            problem = "The service has no engine to run on: " + string.Join(" ", missing)
                      + " An Admin sets the providers and models on the settings page.";
        else
            // "Allow for this session" outlives a rebuilt engine, as the window's does a saved settings. Each model call's
            // figures go to the run whose flow made it (ServerRunScope): the engine is one, the runs on it several.
            engine = EngineComposition.Build(settings, _http, _log, Current?.Session ?? new SessionApprovals(),
                metrics: ServerRunScope.Report) with { Approvals = Approvals };
        lock (_gate)
            _now = (engine, problem, settings);
    }

    /// <summary>The engine, or null with <see cref="Problem"/> saying why.</summary>
    public ComposedEngine? Current
    {
        get { lock (_gate) return _now.Engine; }
    }

    public string? Problem
    {
        get { lock (_gate) return _now.Problem; }
    }

    /// <summary>The service account's settings, as last loaded - the providers' addresses decide where a model ran (ModelReaches).</summary>
    public AppSettings? Settings
    {
        get { lock (_gate) return _now.Settings; }
    }

    /// <summary>"Allow (workspace)" as the service remembers it: per person (ApprovalStore, rule 5), in its own data folder.</summary>
    public ApprovalStore Approvals { get; }

    /// <summary>The folders the service has run in, with the level and worker saved for each.</summary>
    public WorkspaceRegistry Workspaces { get; }

    /// <summary>The service account's schedules - the same file its own console tick would read.</summary>
    public Core.Schedules.ScheduleStore Schedules { get; }

    /// <summary>Which schedules are running now, shared with a console tick of the same account so neither runs one twice.</summary>
    public Core.Schedules.RunMarkers Markers { get; }

    /// <summary>The log the engine writes to - the service's one log.</summary>
    public LogHub Log => _log;

    public void Dispose() => _http.Dispose();
}
