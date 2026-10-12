namespace Enactive.Server.Tests;

using System.Security.Claims;
using Enactive.Core.Diagnostics;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Enactive.Workspace;

/// <summary>
/// The service's log page (server plan phase 3): read by anybody signed in through the window's own view of the log
/// (LogWindowViewModel), analysed by an Operator, and the raw wire captured by an Admin only - it changes what the service
/// records for everybody.
/// </summary>
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
        _users.Add("ada", "correct horse battery", Role.Admin);
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

    /// <summary>The page's clock, held by the test: lines that arrive come in when it drains.</summary>
    private static IDisposable NoClock(Action drain) => new Nothing();

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The view did not come to the state the test waited for.");
            await Task.Delay(10);
        }
    }

    /// <summary>The hub's pump has handed on the lines the constructor logged.</summary>
    private Task Logged() => Until(() => _engine.Log.Snapshot().Count == 2);

    [Fact]
    public async Task A_viewer_reads_the_log_narrowed_by_level_source_and_text()
    {
        await Logged();
        var (view, problem) = _logs.View(As("vera"), NoClock);
        Assert.Null(problem);
        var log = view!;
        string[] Shown() => log.Rows.Select(r => r.Entry.Message).ToArray();

        log.SelectedLevel = "Info";
        await Until(() => Shown().SequenceEqual(["service started", "dotnet build exited 1"]));
        foreach (var source in log.Sources.Where(s => s.Source != LogSource.Tool))
            source.IsEnabled = false;
        await Until(() => Shown().SequenceEqual(["dotnet build exited 1"]));
        foreach (var source in log.Sources)
            source.IsEnabled = true;
        log.SearchText = "BUILD";
        await Until(() => Shown().SequenceEqual(["dotnet build exited 1"]));
        log.Detach();
    }

    [Fact]
    public void An_account_disabled_reads_nothing()
    {
        var vera = As("vera");
        _users.SetDisabled("vera", true);

        Assert.Null(_logs.View(vera, NoClock).View);
    }

    /// <summary>Asking a model spends its time, and its answer is about runs: an Operator's - and only an Operator's view is given it.</summary>
    [Fact]
    public async Task A_viewer_cannot_ask_a_model_and_without_an_engine_nobody_can()
    {
        const string shown = "09:30:15.250  WRN  Tool         ------    dotnet build exited 1\n";

        Assert.Contains("role does not include", (await _logs.AnalyseAsync(As("vera"), shown, CancellationToken.None)).Problem);
        Assert.Contains("no engine", (await _logs.AnalyseAsync(As("alice"), shown, CancellationToken.None)).Problem);
        Assert.Null(_logs.View(As("vera"), NoClock).View!.Analyse);
        Assert.NotNull(_logs.View(As("alice"), NoClock).View!.Analyse);
    }

    /// <summary>
    /// Capturing the raw wire is an Admin's: it changes what the service records for everybody, and the dumps carry whole
    /// prompts and replies. Turned on, the hub records Trace; off, it goes back to the level the service was opened at -
    /// each in the audit trail with who did it. Anybody else is refused, and nothing changes.
    /// </summary>
    [Fact]
    public void Capture_raw_wire_is_an_admin_s_and_audited()
    {
        Assert.Equal(LogLevel.Debug, _engine.NormalLogLevel);

        Assert.Contains("role does not include", _logs.SetCapture(As("alice"), trace: true));
        Assert.Contains("role does not include", _logs.SetCapture(As("vera"), trace: true));
        Assert.Equal(LogLevel.Debug, _engine.Log.MinLevel);

        Assert.Null(_logs.SetCapture(As("ada"), trace: true));
        Assert.Equal(LogLevel.Trace, _engine.Log.MinLevel);
        Assert.Contains(_users.AuditTrail(10), a => a.Account == "ada" && a.Action == "raw wire capture on");
        // A view opened now says capture is on: it is.
        Assert.True(_logs.View(As("vera"), NoClock).View!.CaptureRaw);

        Assert.Null(_logs.SetCapture(As("ada"), trace: false));
        Assert.Equal(LogLevel.Debug, _engine.Log.MinLevel);
        Assert.Contains(_users.AuditTrail(10), a => a.Account == "ada" && a.Action == "raw wire capture off");
        Assert.DoesNotContain(_users.AuditTrail(10), a => a.Account != "ada" && a.Action.StartsWith("raw wire"));
    }

    /// <summary>The view's switch goes through the same check: an Operator's switch is refused and stays off.</summary>
    [Fact]
    public void An_operator_s_view_cannot_turn_capture_on()
    {
        var log = _logs.View(As("alice"), NoClock).View!;

        log.CaptureRaw = true;

        Assert.False(log.CaptureRaw);
        Assert.Equal(LogLevel.Debug, _engine.Log.MinLevel);
        Assert.Contains("role does not include", log.Status);
    }

    /// <summary>Clear in a page's view leaves the service's log as it is: it is everybody's.</summary>
    [Fact]
    public async Task A_view_s_clear_leaves_the_service_s_log()
    {
        await Logged();
        var log = _logs.View(As("vera"), NoClock).View!;
        await Until(() => log.Rows.Count == 2);

        log.ClearCommand.Execute(null);

        await Until(() => log.Rows.Count == 0);
        Assert.Equal(2, _engine.Log.Snapshot().Count);
    }

    /// <summary>
    /// A live run's Log tab: that run's lines and no other's, oldest first, at every level kept - the window's tab beside the steps
    /// (RunLogViewModel), which answers "what went to the model in this run" without the whole service's log around it.
    /// </summary>
    [Fact]
    public void A_run_s_log_is_its_own_lines()
    {
        var run = Guid.NewGuid();
        Entry(run, LogLevel.Info, "the prompt of this run");
        Entry(Guid.NewGuid(), LogLevel.Info, "another run's line");
        Entry(run, LogLevel.Warn, "a tool of this run failed");

        Assert.Equal(["the prompt of this run", "a tool of this run failed"], _logs.OfRun(As("vera"), run).Select(e => e.Message));

        var vera = As("vera");
        _users.SetDisabled("vera", true);
        Assert.Empty(_logs.OfRun(vera, run));
    }

    private void Entry(Guid run, LogLevel level, string message)
        => _engine.Log.Log(new LogEntry(0, DateTimeOffset.UtcNow, level, LogSource.Host, run, null, message, null, null));
}
