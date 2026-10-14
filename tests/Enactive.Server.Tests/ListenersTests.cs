namespace Enactive.Server.Tests;

using System.Security.Claims;
using Enactive.Application;
using Enactive.Core.Permissions;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Enactive.Workspace;

/// <summary>
/// The pages listen on the service's objects - a run, its question, who watches it, the inbox, the proposals - and every
/// open tab is a listener. One that throws is passed over: the others are still told, and what was changing finishes.
/// Raised as one multicast call, the first throw stopped the rest and went up into the change itself.
/// </summary>
public sealed class ListenersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-listeners", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Enactive.Tests.SqlitePools.ClearUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private static Action Faulty => () => throw new InvalidOperationException("one tab's page has a fault");

    [Fact]
    public void Each_listener_is_told_whatever_the_ones_before_it_do()
    {
        var told = new List<string>();
        Action listeners = () => told.Add("first");
        listeners += Faulty;
        listeners += () => told.Add("third");

        Listeners.Tell(listeners, "a test's");

        Assert.Equal(["first", "third"], told);
    }

    /// <summary>
    /// A log that cannot take the line - the host it belongs to stopping - is passed over too: the warning threw out of
    /// the catch, and the listeners after the faulty one were not told after all.
    /// </summary>
    [Fact]
    public void A_log_that_throws_does_not_stop_the_listeners_after_a_faulty_one()
    {
        var told = new List<string>();
        Action listeners = Faulty;
        listeners += () => told.Add("after");
        var before = Listeners.Log;
        Listeners.Log = new GoneLog();
        try
        {
            Listeners.Tell(listeners, "a test's");
        }
        finally
        {
            Listeners.Log = before;
        }

        Assert.Equal(["after"], told);
    }

    /// <summary>A logger whose host has gone: every line throws, as a disposed logger factory's does.</summary>
    private sealed class GoneLog : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new ObjectDisposedException("the service's log");
    }

    [Fact]
    public void A_question_goes_up_on_every_page_though_one_throws()
    {
        var questions = new WebQuestions();
        var told = 0;
        questions.Changed += Faulty;
        questions.Changed += () => told++;
        var request = new DecisionRequest(Guid.NewGuid(), "Approve write_file", "notes.md", [new("allow", "Allow")], "allow");

        questions.Show(request, [new DecisionChoice("Allow", "allow")]);

        Assert.Equal(request.Id, questions.Pending?.Request.Id);
        Assert.Equal(1, told);
    }

    [Fact]
    public void A_watcher_who_comes_is_said_on_every_page_though_one_throws()
    {
        var session = new RunSession(_root, "alice", "write the digest");
        var told = 0;
        session.WatchersChanged += Faulty;
        session.WatchersChanged += () => told++;

        session.Subscribe(0, "ada");

        Assert.Equal(["ada"], session.Watchers);
        Assert.Equal(1, told);
    }

    [Fact]
    public async Task Proposals_and_the_inbox_change_for_every_page_though_one_throws()
    {
        var workspace = Path.Combine(_root, "project");
        Directory.CreateDirectory(workspace);
        var users = new UserStore(_root);
        users.Add("alice", "correct horse battery", Role.Operator);
        using var engine = new ServerEngine(Path.Combine(_root, "data"), new FileLogSink(Path.Combine(_root, "logs")));
        engine.Workspaces.Touch(workspace);
        var known = new KnownWorkspaces(engine, new WorkspaceRoots([_root]));

        var staging = new ServerStaging(users, known);
        var stagingTold = 0;
        staging.Changed += Faulty;
        staging.Changed += () => stagingTold++;
        Assert.True(Directory.Exists(staging.Begin(workspace, Guid.NewGuid(), "alice", "tidy the notes")));
        Assert.Equal(1, stagingTold);

        var inbox = new ServerInbox(users, known, new RunControl(users, new RunQueue(1), new NeverRuns(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RunControl>.Instance));
        var inboxTold = 0;
        inbox.Changed += Faulty;
        inbox.Changed += () => inboxTold++;
        Assert.Null(await inbox.MarkAllReadAsync(ServerAuth.PrincipalOf(users.Find("alice")!), CancellationToken.None));
        Assert.Equal(1, inboxTold);
    }

    private sealed class NeverRuns : IRunExecutor
    {
        public string? Problem => "no engine";
        public SessionApprovals Session { get; } = new();
        public ApprovalStore Approvals { get; } = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        public Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct) => Task.CompletedTask;
    }
}
