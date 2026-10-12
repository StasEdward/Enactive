namespace Enactive.Server.Tests;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Enactive.Workspace;

/// <summary>
/// The Timeline and the Environment (ServerReports): read by anybody signed in, for a workspace the service knows and no
/// other folder - a Viewer naming any folder would read whatever memory sat under it, and have git probed in it.
/// </summary>
public sealed class ServerReportsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-reports", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly UserStore _users;
    private readonly ServerEngine _engine;
    private readonly List<string> _probed = [];
    private readonly ServerReports _reports;

    public ServerReportsTests()
    {
        _workspace = Path.Combine(_root, "project");
        Directory.CreateDirectory(_workspace);
        _users = new UserStore(_root);
        _users.Add("vera", "correct horse battery", Role.Viewer);
        _engine = new ServerEngine(_root, new FileLogSink(Path.Combine(_root, "logs")));
        _engine.Workspaces.Touch(_workspace);
        _reports = new ServerReports(_users, new KnownWorkspaces(_engine, new WorkspaceRoots([_root])), Probe);
    }

    public void Dispose()
    {
        _engine.Dispose();
        Enactive.Tests.SqlitePools.ClearUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private Task<(EnvironmentInfo Info, EnvironmentSnapshot Snapshot)> Probe(WorkspaceInfo workspace, CancellationToken ct)
    {
        _probed.Add(workspace.RootPath);
        var info = new EnvironmentInfo(Guid.NewGuid(), workspace.Id, new HostInfo("Windows 11", "X64", "build-01", "svc", 8, null, null), [], [], [], []);
        return Task.FromResult((info, new EnvironmentSnapshot(Guid.NewGuid(), info.Id, DateTimeOffset.UtcNow, 0, 40, [], new Dictionary<string, object?>())));
    }

    private ClaimsPrincipal Vera => ServerAuth.PrincipalOf(_users.Find("vera")!);

    [Fact]
    public async Task A_viewer_reads_a_known_workspace_s_reports()
    {
        var (timeline, problem) = await _reports.TimelineAsync(Vera, _workspace, CancellationToken.None);
        Assert.Null(problem);
        Assert.StartsWith("PROJECT MEMORY — " + _workspace, timeline);

        var (environment, _) = await _reports.EnvironmentAsync(Vera, _workspace, CancellationToken.None);
        Assert.StartsWith("Host: Windows 11 (X64), 8 CPUs", environment);
        Assert.Equal([_workspace], _probed);
    }

    [Fact]
    public async Task A_folder_the_service_does_not_know_is_neither_read_nor_probed()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        Assert.Equal((null, "The service does not know that folder as a workspace."),
            await _reports.TimelineAsync(Vera, elsewhere, CancellationToken.None));
        Assert.Equal((null, "The service does not know that folder as a workspace."),
            await _reports.EnvironmentAsync(Vera, elsewhere, CancellationToken.None));
        Assert.Empty(_probed);
    }

    [Fact]
    public async Task An_account_disabled_reads_nothing()
    {
        var vera = Vera;
        _users.SetDisabled("vera", true);

        Assert.Equal((null, "Your session has ended. Sign in again."), await _reports.TimelineAsync(vera, _workspace, CancellationToken.None));
        Assert.Equal((null, "Your session has ended. Sign in again."), await _reports.EnvironmentAsync(vera, _workspace, CancellationToken.None));
        Assert.Empty(_probed);
    }
}
