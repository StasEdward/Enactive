namespace Enactive.Server.Tests;

using Enactive.Server.Runs;
using Enactive.Settings;
using Enactive.Workspace;

/// <summary>
/// Plan Phase 3.1: the service reads the machine's global check manifests next to ITS settings.json - its own data
/// folder - not from whatever %APPDATA% the process resolves to. A test's service therefore reads its own temporary
/// folder, and no test depends on the checks of whoever runs the suite.
/// </summary>
public sealed class ServerEngineChecksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-server-checks", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void The_service_reads_global_checks_next_to_its_own_settings()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        new AppSettings
        {
            Providers = [new ProviderConfig { Id = "fixture", BaseUrl = "http://localhost:1", Models = ["fixture"] }],
            Workers = [new WorkerConfig { Id = "fixture", Role = "fixture", Model = "fixture/fixture" }]
        }.Save(Path.Combine(data, "settings.json"), replaceUnreadable: true);

        using var engine = new ServerEngine(data, new FileLogSink(Path.Combine(_root, "logs")));

        Assert.NotNull(engine.Current);
        Assert.Equal(Path.Combine(data, "checks"), engine.Current!.GlobalChecks);
    }
}
