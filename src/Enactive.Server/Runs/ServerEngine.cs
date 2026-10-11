namespace Enactive.Server.Runs;

using Enactive.Core.Permissions;
using Enactive.Settings;
using Enactive.Workspace;

/// <summary>
/// The engine the service runs on: built from the service account's own <c>settings.json</c> by the composition every
/// host shares (EngineComposition), with that account's remembered approvals and workspace list. Its absence is a
/// state, said where a run is asked for - a service whose account has no model configured yet still starts, signs
/// people in and shows the history.
/// </summary>
internal sealed class ServerEngine : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly LogHub _log;

    public ServerEngine(string dataFolder, FileLogSink logFile)
    {
        // The engine's log into the same daily file as the service's own, so a run and the request that started it
        // read in one place.
        _log = new LogHub(minLevel: Core.Diagnostics.LogLevel.Debug, downstream: [logFile]);
        Approvals = new ApprovalStore(Path.Combine(dataFolder, "permissions.json"));
        Workspaces = WorkspaceRegistry.Load(filePath: Path.Combine(dataFolder, "workspaces.json"));

        var settings = AppSettings.Load(Path.Combine(dataFolder, "settings.json"));
        if (EngineComposition.Missing(settings) is { Count: > 0 } missing)
        {
            Problem = "The service has no engine to run on: " + string.Join(" ", missing)
                      + " Configure it as the service's account (settings in the browser come later).";
            return;
        }
        Current = EngineComposition.Build(settings, _http, _log, new SessionApprovals()) with { Approvals = Approvals };
    }

    /// <summary>The engine, or null with <see cref="Problem"/> saying why.</summary>
    public ComposedEngine? Current { get; }

    public string? Problem { get; }

    /// <summary>"Allow (workspace)" as the service remembers it: per person (ApprovalStore, rule 5), in its own data folder.</summary>
    public ApprovalStore Approvals { get; }

    /// <summary>The folders the service has run in, with the level and worker saved for each.</summary>
    public WorkspaceRegistry Workspaces { get; }

    public void Dispose()
    {
        _http.Dispose();
        _log.Dispose();
    }
}
