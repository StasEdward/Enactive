namespace Enactive.Server.Tests;

using System.Security.Claims;
using Enactive.Core.Diagnostics;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Enactive.Workspace;

/// <summary>The service's log page (server plan phase 3): read by anybody signed in, filtered as the window filters, analysed by an Operator.</summary>
public sealed class ServerLogsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-logs", Guid.NewGuid().ToString("N"));
    private readonly UserStore _users;
    private readonly ServerEngine _engine;
    private readonly ServerLogs _logs;

    public ServerLogsTests()
    {
        _users = new UserStore(_root);
        _users.Add("alice", "correct horse battery", Role.Operator);
        _users.Add("vera", "correct horse battery", Role.Viewer);
        _engine = new ServerEngine(_root, new FileLogSink(Path.Combine(_root, "logs")));
        _logs = new ServerLogs(_users, _engine);
        _engine.Log.Info(LogSource.Host, "service started");
        _engine.Log.Warn(LogSource.Tool, "dotnet build exited 1");
        _engine.Log.Trace(LogSource.Prompt, "the whole prompt");
    }

    public void Dispose()
    {
        _engine.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private ClaimsPrincipal As(string name) => ServerAuth.PrincipalOf(_users.Find(name)!);

    [Fact]
    public void A_viewer_reads_the_log_narrowed_by_level_source_and_text()
    {
        Assert.Equal(["service started", "dotnet build exited 1"],
            _logs.Entries(As("vera"), LogLevel.Info, [], "").Select(e => e.Message));
        Assert.Equal(["dotnet build exited 1"], _logs.Entries(As("vera"), LogLevel.Info, [LogSource.Tool], "").Select(e => e.Message));
        Assert.Equal(["dotnet build exited 1"], _logs.Entries(As("vera"), LogLevel.Trace, [], "BUILD").Select(e => e.Message));
    }

    [Fact]
    public void An_account_disabled_reads_nothing()
    {
        var vera = As("vera");
        _users.SetDisabled("vera", true);

        Assert.Empty(_logs.Entries(vera, LogLevel.Trace, [], ""));
    }

    /// <summary>Asking a model spends its time, and its answer is about runs: an Operator's.</summary>
    [Fact]
    public async Task A_viewer_cannot_ask_a_model_and_without_an_engine_nobody_can()
    {
        var shown = _logs.Entries(As("alice"), LogLevel.Info, [], "");

        Assert.Contains("role does not include", (await _logs.AnalyseAsync(As("vera"), shown, CancellationToken.None)).Problem);
        Assert.Contains("no engine", (await _logs.AnalyseAsync(As("alice"), shown, CancellationToken.None)).Problem);
    }
}
