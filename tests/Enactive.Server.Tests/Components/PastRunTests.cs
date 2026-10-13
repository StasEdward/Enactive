namespace Enactive.Server.Tests.Components;

using System.Threading.Channels;
using Bunit;
using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Server.Accounts;
using Enactive.Server.Components.Runs;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// A past run on the Runs screen, as the window shows one over its live run (MainWindow.axaml 923-1243): its header with
/// Retry and Run again and which attempt it is, the tabs Execution / Artifacts / Timeline, and the status column of what
/// was recorded - read-only, at /history/{workspace}/{run}, while the live run goes on. Every action is the service's to
/// decide (ServerHistory); a question is asked in the window's words, in a dialog on the screen.
/// </summary>
public sealed class PastRunTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-past", Guid.NewGuid().ToString("N"));
    private readonly Holds _engine;

    public PastRunTests()
    {
        _engine = new Holds(new ApprovalStore(Path.Combine(_folders, "permissions.json")));
        Services.AddSingleton<IRunExecutor>(_engine);
        // Runs start only under the service's roots.
        Services.AddSingleton(new WorkspaceRoots([_folders]));
        Services.AddSingleton(new LiveRunClock(null));
    }

    protected override void Dispose(bool disposing)
    {
        _engine.Release();
        base.Dispose(disposing);
        if (disposing)
        {
            Enactive.Tests.SqlitePools.ClearUnder(_folders);
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
        }
    }

    private IRunControl Runs => Services.GetRequiredService<IRunControl>();

    /// <summary>A folder the service knows as a workspace.</summary>
    private string Known(string name)
    {
        var root = Path.Combine(_folders, name);
        Directory.CreateDirectory(root);
        Services.GetRequiredService<ServerEngine>().Workspaces.Touch(root);
        return root;
    }

    /// <summary>A finished run of "digest the inbox" in the folder, typed - its request recorded - that wrote digest.md.</summary>
    private static async Task<RunRecord> RecordedAsync(string root)
    {
        File.WriteAllText(Path.Combine(root, "digest.md"), "three mails");
        var at = DateTimeOffset.Now.AddHours(-1);
        var run = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "digest the inbox", "qwen", at, at.AddSeconds(5), "Completed",
            [
                new RunEventRecord(at, nameof(EventKind.IntentReceived), "Intent", Payload: WorkEventPayload.RequestPayload("digest the inbox")),
                new RunEventRecord(at, nameof(EventKind.ToolInvoked), "write_file digest.md")
            ],
            ["digest.md"], ["Approve write_file: allow"]);
        await RunStoreFactory.Create(WorkspaceInfo.For(root)).SaveAsync(run, CancellationToken.None);
        return run;
    }

    /// <summary>The Runs screen at /history/{workspace}/{run}, for this person, once the run is drawn.</summary>
    private IRenderedComponent<RunsScreen> Past(string who, string root, Guid run)
    {
        SignIn(who);
        var screen = Render<RunsScreen>(p => p.Add(s => s.WorkspaceId, WorkspaceInfo.For(root).Id).Add(s => s.PastRunId, run));
        screen.WaitForAssertion(() => Assert.Equal("digest the inbox", screen.Find(".past-run .task-title").TextContent.Trim()));
        return screen;
    }

    private static IEnumerable<string> Offered(IRenderedComponent<RunsScreen> screen)
        => screen.FindAll(".past-run button").Select(b => b.TextContent.Trim());

    private static void Tab(IRenderedComponent<RunsScreen> screen, string name)
        => screen.FindAll(".past-run [role=tab]").Single(t => t.TextContent.Trim().StartsWith(name, StringComparison.Ordinal)).Click();

    [Fact]
    public async Task Retry_starts_the_same_run_and_opens_it()
    {
        var root = Known("project");
        var past = await RecordedAsync(root);
        var screen = Past("alice", root, past.RunId);
        // Its header, as the window's: the figures, and what it was - a typed run, so no template to run again.
        Assert.Equal("Completed", screen.Find(".past-run .task-header .pill").TextContent.Trim());
        Assert.Equal(["Execution", "Artifacts (1)", "Timeline"], screen.FindAll(".past-run [role=tab]").Select(t => t.TextContent.Trim()));
        Assert.DoesNotContain("Run again", Offered(screen));

        screen.FindAll(".past-run button").Single(b => b.TextContent.Trim() == "Retry").Click();

        // Started as another attempt at the same task, and opened at its own address.
        var navigation = Services.GetRequiredService<NavigationManager>();
        screen.WaitForAssertion(() => Assert.Matches(@"/runs/[0-9a-f-]{36}$", navigation.Uri));
        var started = Runs.Find(ServerAuth.PrincipalOf(Users.Find("alice")!), Guid.Parse(navigation.Uri[^36..]))!;
        Assert.Equal("digest the inbox", started.Prompt);
        Assert.Equal(past.TaskId, started.TaskId);
    }

    /// <summary>
    /// Delete on a file the run wrote asks first, in the window's words (RunHistoryService.DeleteFile, with the file's own
    /// date: it deletes what is on disk now, not what the run wrote) - and only "Delete" deletes it, after which the card
    /// says what became of it, as the window's does.
    /// </summary>
    [Fact]
    public async Task Deleting_a_file_asks_first_in_the_window_s_words()
    {
        var root = Known("project");
        var past = await RecordedAsync(root);
        var screen = Past("alice", root, past.RunId);
        Tab(screen, "Artifacts");
        Assert.Equal("digest.md", screen.Find(".past-run .artifact .path").TextContent.Trim());

        screen.FindAll(".past-run .artifact button").Single(b => b.TextContent.Trim() == "Delete").Click();
        var dialog = screen.Find("[role=dialog]");
        Assert.Equal("Delete “digest.md”?", dialog.QuerySelector(".dialog-title")!.TextContent.Trim());
        Assert.StartsWith("This deletes the file as it is on disk now, last changed ", dialog.QuerySelector(".body")!.TextContent);
        Assert.Equal(["Delete", "Keep"], dialog.QuerySelectorAll("button").Select(b => b.TextContent.Trim()));

        // Keep keeps it.
        dialog.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Keep").Click();
        Assert.Empty(screen.FindAll("[role=dialog]"));
        Assert.True(File.Exists(Path.Combine(root, "digest.md")));

        screen.FindAll(".past-run .artifact button").Single(b => b.TextContent.Trim() == "Delete").Click();
        screen.Find("[role=dialog]").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Delete").Click();

        screen.WaitForAssertion(() => Assert.Equal("deleted", screen.Find(".past-run .artifact-status").TextContent.Trim()));
        Assert.False(File.Exists(Path.Combine(root, "digest.md")));
        // Done with: nothing left on the card to press, as the window's (ArtifactItemViewModel.CanAct).
        Assert.All(screen.FindAll(".past-run .artifact button"), b => Assert.True(b.HasAttribute("disabled")));
    }

    /// <summary>A file gone since the run is not asked about: its card says so, in the window's words, and is done with.</summary>
    [Fact]
    public async Task A_file_already_gone_is_said_to_be_on_its_card()
    {
        var root = Known("project");
        var past = await RecordedAsync(root);
        var screen = Past("alice", root, past.RunId);
        Tab(screen, "Artifacts");
        File.Delete(Path.Combine(root, "digest.md"));

        screen.FindAll(".past-run .artifact button").Single(b => b.TextContent.Trim() == "Delete").Click();

        screen.WaitForAssertion(() => Assert.Equal("already gone", screen.Find(".past-run .artifact-status").TextContent.Trim()));
        Assert.Empty(screen.FindAll("[role=dialog]"));
        Assert.All(screen.FindAll(".past-run .artifact button"), b => Assert.True(b.HasAttribute("disabled")));
    }

    [Fact]
    public async Task A_viewer_is_offered_neither_retry_nor_delete()
    {
        var root = Known("project");
        var past = await RecordedAsync(root);

        // An Operator's, on the same run - so the Viewer's absence of them is the role, not an empty page.
        var operating = Past("alice", root, past.RunId);
        Tab(operating, "Artifacts");
        Assert.Contains("Retry", Offered(operating));
        Assert.Contains("Delete", Offered(operating));

        var viewing = Past("vera", root, past.RunId);
        Tab(viewing, "Artifacts");
        var offered = Offered(viewing).ToList();
        Assert.Contains("Review", offered);
        Assert.DoesNotContain("Retry", offered);
        Assert.DoesNotContain("Run again", offered);
        Assert.DoesNotContain("Delete", offered);
    }

    /// <summary>
    /// A history row opens the run at its own address on the same screen, in place of the live run - which goes on: it is
    /// the service's, and reading history never interrupts work. Back returns to it, and the bar's Stop is still its.
    /// </summary>
    [Fact]
    public async Task Choosing_a_history_row_opens_it_without_stopping_the_live_run()
    {
        var root = Known("project");
        var past = await RecordedAsync(root);
        var live = Runs.Start(ServerAuth.PrincipalOf(Users.Find("alice")!), root, "check the disks").Run!;
        await _engine.Started.WaitAsync(TimeSpan.FromSeconds(10));
        SignIn("alice");
        var screen = Render<RunsScreen>(p => p.Add(s => s.RunId, live.Id));
        screen.WaitForAssertion(() => Assert.Equal("check the disks", screen.Find(".live-run .task-title").TextContent.Trim()));

        // The row names the workspace and the run by their ids.
        var workspace = WorkspaceInfo.For(root).Id;
        var row = screen.FindAll(".history a.card-text").Single(a => a.TextContent.Contains("digest the inbox"));
        Assert.Equal($"history/{workspace}/{past.RunId}", row.GetAttribute("href"));

        // The router moves the screen to the row's address: the route's values, and none of the last one's.
        screen.Render(p => p.Add(s => s.RunId, null).Add(s => s.WorkspaceId, workspace).Add(s => s.PastRunId, past.RunId));

        screen.WaitForAssertion(() => Assert.Equal("digest the inbox", screen.Find(".past-run .task-title").TextContent.Trim()));
        Assert.Empty(screen.FindAll(".live-run"));
        Assert.False(live.Ended.IsCompleted);
        Assert.False(screen.FindAll(".command-bar button").Single(b => b.TextContent.Trim() == "Stop").HasAttribute("disabled"));
        var back = screen.Find(".past-run a.back");
        Assert.Equal($"runs/{live.Id}", back.GetAttribute("href"));
        Assert.Equal("Back to the current run", back.GetAttribute("title"));
    }

    /// <summary>
    /// The run the screen showed last is remembered for a past run only - its Back, and the bar's Stop under it. On the
    /// Runs screen without a run (the mark, or another workspace in the switcher) the bar is free: kept on the last run,
    /// it was busy for a run not on screen, Run greyed and Stop cancelling a run in another workspace.
    /// </summary>
    [Fact]
    public async Task Leaving_a_run_for_the_runs_screen_leaves_the_bar_free()
    {
        var root = Known("project");
        var live = Runs.Start(ServerAuth.PrincipalOf(Users.Find("alice")!), root, "check the disks").Run!;
        await _engine.Started.WaitAsync(TimeSpan.FromSeconds(10));
        SignIn("alice");
        var screen = Render<RunsScreen>(p => p.Add(s => s.RunId, live.Id));
        screen.WaitForAssertion(() => Assert.False(Bar(screen, "Stop").HasAttribute("disabled")));

        // The router moves the screen to "/": none of the run's address is left.
        screen.Render(p => p.Add(s => s.RunId, null));

        screen.WaitForAssertion(() => Assert.True(Bar(screen, "Stop").HasAttribute("disabled")));
        Assert.False(Bar(screen, "Run").HasAttribute("disabled"));
        Assert.False(live.Ended.IsCompleted);
    }

    private static AngleSharp.Dom.IElement Bar(IRenderedComponent<RunsScreen> screen, string name)
        => screen.FindAll(".command-bar button").Single(b => b.TextContent.Trim() == name);

    /// <summary>An address naming a run the workspace does not have says so, rather than an empty frame.</summary>
    [Fact]
    public void A_run_the_workspace_does_not_have_is_said_to_be_missing()
    {
        var root = Known("project");
        SignIn("vera");

        var screen = Render<RunsScreen>(p => p.Add(s => s.WorkspaceId, WorkspaceInfo.For(root).Id).Add(s => s.PastRunId, Guid.NewGuid()));

        screen.WaitForAssertion(() => Assert.Contains("There is no such run in this workspace.", screen.Markup));
    }

    /// <summary>The engine, stood in for: a run starts and goes on until the test is over.</summary>
    private sealed class Holds(ApprovalStore approvals) : IRunExecutor
    {
        private readonly Channel<WorkEvent> _events = Channel.CreateUnbounded<WorkEvent>();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Problem => null;
        public SessionApprovals Session { get; } = new();
        public ApprovalStore Approvals { get; } = approvals;

        public Task Started => _started.Task;

        public void Release() => _events.Writer.TryComplete();

        public Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct)
        {
            var drained = run.Session.DrainAsync(_events.Reader.ReadAllAsync(ct), ct);
            _started.TrySetResult();
            return drained;
        }
    }
}
