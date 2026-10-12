namespace Enactive.Server.Tests.Components;

using System.Text;
using System.Threading.Channels;
using Bunit;
using Enactive.Application;
using Enactive.Core.Artifacts;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Server.Accounts;
using Enactive.Server.Components.Runs;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The live run on the Runs screen, as the window shows it (MainWindow.axaml 546-920, 1244-1300): the task header with who
/// started it and who else watches, the tabs Execution / Artifacts / Log, the step cards with their tone, Current action,
/// and the status column - the AI STATUS tiles, ROUTING, WHERE IT RAN, the question card and MODEL PERFORMANCE. The run is
/// the service's; the page follows it (RunFollower) and every answer is the service's to decide (IRunControl).
/// </summary>
public sealed class LiveRunTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-live", Guid.NewGuid().ToString("N"));
    private readonly Script _script;

    public LiveRunTests()
    {
        _script = new Script(new ApprovalStore(Path.Combine(_folders, "permissions.json")));
        Services.AddSingleton<IRunExecutor>(_script);
        Services.AddSingleton(new WorkspaceRoots([_folders]));
        // Copy all is the browser's clipboard.
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true);
    }

    protected override void Dispose(bool disposing)
    {
        _script.End();
        base.Dispose(disposing);
        if (disposing)
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
    }

    private IRunControl Runs => Services.GetRequiredService<IRunControl>();
    private System.Security.Claims.ClaimsPrincipal As(string name) => ServerAuth.PrincipalOf(Users.Find(name)!);

    /// <summary>A folder the service knows as a workspace.</summary>
    private string Known(string name)
    {
        var root = Path.Combine(_folders, name);
        Directory.CreateDirectory(root);
        Services.GetRequiredService<ServerEngine>().Workspaces.Touch(root);
        return root;
    }

    /// <summary>Alice's run of "check the disks" in a known folder, going once the script has it.</summary>
    private async Task<ServerRun> StartedAsync(string task = "check the disks")
    {
        var run = Runs.Start(As("alice"), Known("project"), task).Run!;
        await _script.Started.WaitAsync(TimeSpan.FromSeconds(10));
        return run;
    }

    private IRenderedComponent<LiveRun> Open(string who, ServerRun run)
    {
        SignIn(who);
        return Render<LiveRun>(p => p.Add(l => l.RunId, run.Id));
    }

    private static WorkEvent Step(EventKind kind, int step, string summary, string? payload = null)
        => Script.Ev(kind, summary, payload ?? $"{{\"step\":{step}}}");

    private static DecisionRequest WriteFile(string root)
        => new(Guid.NewGuid(), "Approve write_file", "digest.md", [new("allow", "Allow"), new("deny", "Deny")], "allow",
            Subject: "write_file", FullDetail: "write_file {\"path\":\"digest.md\",\n \"content\":\"the whole digest\"}",
            Action: new(Script.RunId, Guid.NewGuid().ToString(), "write_file", "{}", root));

    private static IEnumerable<string> Answers(IRenderedComponent<LiveRun> page)
        => page.FindAll(".decision button").Select(b => b.TextContent.Trim());

    [Fact]
    public async Task The_feed_shows_each_step_with_its_tone()
    {
        var run = await StartedAsync();
        _script.Emit(Script.Ev(EventKind.IntentReceived, "Intent: check the disks"));
        _script.Emit(Script.Ev(EventKind.PlanCreated, "Disks — 2 steps", WorkEventPayload.PlanPayload("Disks", ["check the disks", "write the report"])));
        _script.Emit(Step(EventKind.StepStarted, 1, "[1/2] check the disks"));
        _script.Emit(Step(EventKind.ToolInvoked, 1, """run_command {"command":"chkdsk C:"}"""));
        _script.Emit(Step(EventKind.StepCompleted, 1, "[1/2] check the disks — done", WorkEventPayload.StepPayload(1, StepOutcomeKind.Succeeded)));
        _script.Emit(Step(EventKind.StepStarted, 2, "[2/2] write the report"));
        await WaitUntil(() => run.Session.Seq == 6);

        var page = Open("alice", run);

        page.WaitForAssertion(() =>
        {
            var steps = page.FindAll(".step");
            Assert.Equal(["check the disks", "write the report"], steps.Select(s => s.QuerySelector(".step-title")!.TextContent.Trim()));
            Assert.Contains("tone-done", steps[0].ClassList);
            // A running step is blue (Looks.Card), not the history's amber.
            Assert.Contains("tone-running", steps[1].ClassList);
        });
        Assert.Equal("Disks", page.Find(".task-title").TextContent.Trim());
        Assert.Equal("check the disks", page.Find(".task-intent").TextContent.Trim());
        Assert.Matches(@"^started by alice at \d\d:\d\d$", page.Find(".task-meta").TextContent.Trim());
        Assert.Equal(["Execution", "Artifacts", "Log"], page.FindAll("[role=tab]").Select(t => t.TextContent.Trim()));

        // What the step did is folded behind its tally, one click away, as the window's card.
        page.FindAll(".step")[0].QuerySelector("button.disclosure")!.Click();
        Assert.Contains("chkdsk C:", page.FindAll(".step")[0].QuerySelector(".step-lines")!.TextContent);
    }

    [Fact]
    public async Task A_viewer_sees_the_question_and_no_answer_buttons()
    {
        var run = await StartedAsync();
        _ = _script.AskAsync(WriteFile(run.Workspace));
        await WaitUntil(() => run.Questions.Pending is not null);

        var page = Open("vera", run);

        page.WaitForAssertion(() => Assert.Contains("USER DECISION REQUIRED", page.Find(".decision").TextContent));
        Assert.Equal("Approve write_file", page.Find(".decision-topic").TextContent.Trim());
        // The complete action, never the summary: it is what an answer authorises.
        Assert.Equal("write_file {\"path\":\"digest.md\",\n \"content\":\"the whole digest\"}", page.Find(".decision pre").TextContent);
        Assert.Empty(Answers(page));
        // The service's wait (30 minutes by default), counted from when it was asked.
        Assert.Equal("waits 30 min more, then goes to the Inbox", page.Find(".decision-wait").TextContent.Trim());
    }

    /// <summary>
    /// The answers that remember an approval are the owner's alone (RunControl.Answer): what is remembered binds the person
    /// it is remembered for. Another Operator is offered the plain answers, and nothing the service would refuse.
    /// </summary>
    [Fact]
    public async Task Only_the_owner_is_offered_to_allow_for_longer()
    {
        var run = await StartedAsync();
        _ = _script.AskAsync(WriteFile(run.Workspace));
        await WaitUntil(() => run.Questions.Pending is not null);

        var owner = Open("alice", run);
        owner.WaitForAssertion(() => Assert.Equal(
            ["Allow", "Deny", "Allow (session)", "Allow (workspace)", "Allow (workspace, unwatched runs too)"], Answers(owner)));

        var other = Open("ada", run);
        other.WaitForAssertion(() => Assert.Equal(["Allow", "Deny"], Answers(other)));
    }

    /// <summary>
    /// Answered in another page, by another person or through the inbox: the card leaves every page that showed it - left
    /// up, it offered buttons for a question already decided.
    /// </summary>
    [Fact]
    public async Task A_question_answered_elsewhere_leaves_the_card()
    {
        var run = await StartedAsync();
        var asked = _script.AskAsync(WriteFile(run.Workspace));
        await WaitUntil(() => run.Questions.Pending is not null);
        var page = Open("alice", run);
        page.WaitForAssertion(() => Assert.NotEmpty(Answers(page)));

        Assert.Null(Runs.Answer(As("ada"), run.Id, run.Questions.Pending!.Request.Id, "Deny"));

        Assert.Equal("deny", (await asked.WaitAsync(TimeSpan.FromSeconds(10))).OptionId);
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".decision")));
    }

    /// <summary>An answer pressed on the page is the service's to take; a second, too late, is told so under the card.</summary>
    [Fact]
    public async Task An_answer_on_the_page_answers_the_run()
    {
        var run = await StartedAsync();
        var asked = _script.AskAsync(WriteFile(run.Workspace));
        await WaitUntil(() => run.Questions.Pending is not null);
        var page = Open("alice", run);
        page.WaitForAssertion(() => Assert.NotEmpty(Answers(page)));

        page.FindAll(".decision button").Single(b => b.TextContent.Trim() == "Allow").Click();

        Assert.Equal("allow", (await asked.WaitAsync(TimeSpan.FromSeconds(10))).OptionId);
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".decision")));
    }

    [Fact]
    public async Task The_tiles_follow_the_panel()
    {
        var run = await StartedAsync();
        var page = Open("alice", run);
        page.WaitForAssertion(() => Assert.Equal("—", Tile(page, "TOKENS")));

        _script.Emit(Script.Ev(EventKind.Routed, "Worker -> model ollama/qwen", WorkEventPayload.RoutePayload("worker", "ollama", "qwen")));
        _script.Emit(Step(EventKind.ToolInvoked, 1, """write_file {"path":"report.md"}"""));
        _script.Emit(Script.Ev(EventKind.UsageReported, "usage", WorkEventPayload.UsagePayload(1200, 300, 1, providerId: "ollama")));
        _script.Emit(Script.Ev(EventKind.ArtifactProduced, "Wrote: report.md", WorkEventPayload.ArtifactPayload("document", "report.md")));
        _script.Emit(Script.Ev(EventKind.ArtifactProduced, "Wrote: report.md", WorkEventPayload.ArtifactPayload("document", "report.md")));
        _script.Emit(Script.Ev(EventKind.ArtifactProduced, "Wrote: notes.md", WorkEventPayload.ArtifactPayload("document", "notes.md")));

        page.WaitForAssertion(() =>
        {
            Assert.Equal("2", Tile(page, "ARTIFACTS"));
            Assert.Equal("1", Tile(page, "TOOL CALLS"));
            Assert.Equal("1.5k", Tile(page, "TOKENS"));
        }, TimeSpan.FromSeconds(5));
        Assert.Equal("1.2k in · 300 out", TileElement(page, "TOKENS").GetAttribute("title"));
        Assert.Matches(@"^\d+s$", Tile(page, "ELAPSED"));
        Assert.Equal("Artifacts (2)", page.FindAll("[role=tab]")[1].TextContent.Trim());
        Assert.Contains("ollama/qwen", page.Find(".routing").TextContent);
    }

    private static AngleSharp.Dom.IElement TileElement(IRenderedComponent<LiveRun> page, string label)
        => page.FindAll(".tile").Single(t => t.QuerySelector(".tile-label")!.TextContent.Trim() == label);

    private static string Tile(IRenderedComponent<LiveRun> page, string label)
        => TileElement(page, label).QuerySelector(".tile-value")!.TextContent.Trim();

    /// <summary>
    /// "also watching" names the others with the run open, and changes as they come and go - not only when the run next
    /// moves: a run waiting on a question has no events, and somebody who had opened it was not shown.
    /// </summary>
    [Fact]
    public async Task Other_watchers_are_named()
    {
        var run = await StartedAsync();
        var page = Open("alice", run);
        page.WaitForAssertion(() => Assert.Equal(["alice"], run.Session.Watchers));
        Assert.Empty(page.FindAll(".also-watching"));

        // Sooner than the page's clock ticks (LiveRun, a second): drawn because bob came, not because a second passed.
        var soon = TimeSpan.FromMilliseconds(500);
        var bob = run.Session.Subscribe(run.Session.Seq, "bob");
        page.WaitForAssertion(() => Assert.Equal("also watching: bob", page.Find(".also-watching").TextContent.Trim()), soon);

        run.Session.Unsubscribe(bob);
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".also-watching")), soon);
    }

    /// <summary>
    /// The page is never what the run waits for: with its redraw held, the run's 2,000 events go through and it ends; let go,
    /// the page reads the run as it ended. A follower that waited on its page would have held the engine - a browser tab
    /// asleep would hold up a run somebody else relies on.
    /// </summary>
    [Fact]
    public async Task A_slow_redraw_does_not_hold_up_the_run()
    {
        var session = new RunSession("C:\\work", "alice", "a long reply");
        var events = Channel.CreateUnbounded<WorkEvent>();
        var drained = session.DrainAsync(events.Reader.ReadAllAsync(), CancellationToken.None);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var redrawing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long lastRead = -1;
        using var follower = new RunFollower(session, () =>
            {
                var seq = session.Seq;
                Interlocked.Exchange(ref lastRead, seq);
                return seq;
            },
            async () =>
            {
                redrawing.TrySetResult();
                await held.Task;
            }, "alice");
        follower.Start();

        events.Writer.TryWrite(Script.Ev(EventKind.StepStarted, "step 1"));
        await redrawing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 2_000; i++)
            events.Writer.TryWrite(Script.Ev(EventKind.AssistantDelta, "x"));
        events.Writer.Complete();

        // The run ends while the page is still held in its redraw.
        await drained.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2_001, session.Seq);
        Assert.False(held.Task.IsCompleted);

        held.SetResult();
        await follower.Following.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2_001, Interlocked.Read(ref lastRead));
    }

    /// <summary>A file the run wrote is reviewed in the viewer, on the screen - its text, with Copy all.</summary>
    [Fact]
    public async Task Review_shows_a_written_file_in_the_viewer()
    {
        var run = await StartedAsync();
        File.WriteAllText(Path.Combine(run.Workspace, "report.md"), "# The disks\nall well\n");
        _script.Emit(Script.Ev(EventKind.ArtifactProduced, "Wrote: report.md", WorkEventPayload.ArtifactPayload("document", "report.md")));
        await WaitUntil(() => run.Session.Seq == 1);
        var page = Open("vera", run);

        page.WaitForAssertion(() => Assert.Equal("Artifacts (1)", page.FindAll("[role=tab]")[1].TextContent.Trim()));
        page.FindAll("[role=tab]")[1].Click();
        Assert.Equal("report.md", page.Find(".artifact .path").TextContent.Trim());
        page.FindAll(".artifact button").Single(b => b.TextContent.Trim() == "Review").Click();

        var dialog = page.Find("[role=dialog]");
        Assert.Equal("report.md", dialog.QuerySelector(".dialog-title")!.TextContent.Trim());
        Assert.Equal("# The disks\nall well\n", dialog.QuerySelector("pre")!.TextContent);
        dialog.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Copy all").Click();
        Assert.Equal("# The disks\nall well\n", JSInterop.VerifyInvoke("navigator.clipboard.writeText").Arguments.Single());
    }

    /// <summary>
    /// Source shows only where there is something to switch between, as the window's viewer: beside a caller's rendering,
    /// which must never be the only way to read the text. With the text alone, a Source button would switch to itself.
    /// </summary>
    [Fact]
    public void The_viewer_offers_the_source_only_beside_a_rendering()
    {
        var plain = Render<Enactive.Server.Components.Ui.TextViewer>(p => p.Add(v => v.Title, "notes.md").Add(v => v.Text, "# Notes"));
        Assert.DoesNotContain(plain.FindAll("button"), b => b.TextContent.Trim() == "Source");
        Assert.Equal("# Notes", plain.Find("pre").TextContent);

        var rendered = Render<Enactive.Server.Components.Ui.TextViewer>(p => p.Add(v => v.Title, "notes.md").Add(v => v.Text, "# Notes")
            .Add(v => v.Rendered, (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddMarkupContent(0, "<h1>Notes</h1>"))));
        Assert.Equal("Notes", rendered.Find(".viewer-rendered h1").TextContent);
        rendered.FindAll("button").Single(b => b.TextContent.Trim() == "Source").Click();
        Assert.Equal("# Notes", rendered.Find("pre").TextContent);
        Assert.Empty(rendered.FindAll(".viewer-rendered"));
    }

    /// <summary>
    /// Once the run has ended its kept proposals are in its Artifacts tab, each with its diff, to apply or reject (an
    /// Operator; ServerStaging rules) - the window's staged cards. While the run goes they are not offered: the store is
    /// still being written.
    /// </summary>
    [Fact]
    public async Task Once_ended_the_run_s_proposals_are_applied_from_its_artifacts()
    {
        var run = await StartedAsync();
        File.WriteAllText(Path.Combine(run.Workspace, "notes.md"), "old\n");
        var staging = Services.GetRequiredService<ServerStaging>();
        var folder = staging.Begin(run.Workspace, run.Id, "alice", run.Prompt);
        using (var store = new StagingArtifactStore(run.Workspace, folder))
        {
            await store.CreateAsync("notes.md", ArtifactKind.FileSet, "notes",
                s => s.WriteAsync(Encoding.UTF8.GetBytes("new\n")).AsTask(), CancellationToken.None);
            await staging.EndAsync(run.Workspace, folder, "check the disks", null, store);
        }
        _script.End();
        await run.Ended.WaitAsync(TimeSpan.FromSeconds(10));

        var page = Open("alice", run);
        page.FindAll("[role=tab]")[1].Click();
        page.WaitForAssertion(() => Assert.Equal("notes.md", page.Find(".proposal .path").TextContent.Trim()));

        page.FindAll(".proposal button").Single(b => b.TextContent.Trim() == "Diff").Click();
        Assert.Contains(page.FindAll(".proposal .diff-line"), l => l.TextContent == "- old" && l.ClassList.Contains("tone-failed"));
        Assert.Contains(page.FindAll(".proposal .diff-line"), l => l.TextContent == "+ new" && l.ClassList.Contains("tone-done"));

        page.FindAll(".proposal button").Single(b => b.TextContent.Trim() == "Apply").Click();
        page.WaitForAssertion(() => Assert.StartsWith("Applied: notes.md.", page.Find(".artifacts .said").TextContent.Trim()));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(run.Workspace, "notes.md")));
    }

    /// <summary>
    /// The Runs screen opens a run at its own address under the command bar - whose Stop is then that run's - and says so
    /// when the service has no such run.
    /// </summary>
    [Fact]
    public async Task The_runs_screen_shows_the_run_at_its_address_under_the_bar()
    {
        var run = await StartedAsync();
        SignIn("alice");

        var screen = Render<RunsScreen>(p => p.Add(s => s.RunId, run.Id));

        screen.WaitForAssertion(() => Assert.Equal("check the disks", screen.Find(".task-title").TextContent.Trim()));
        Assert.False(screen.FindAll(".command-bar button").Single(b => b.TextContent.Trim() == "Stop").HasAttribute("disabled"));

        var none = Render<RunsScreen>(p => p.Add(s => s.RunId, Guid.NewGuid()));
        Assert.Contains("There is no such run on the service", none.Markup);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }

    /// <summary>The engine, scripted: the test says what the run does - its events, its questions - and when it ends.</summary>
    private sealed class Script(ApprovalStore approvals) : IRunExecutor
    {
        public static readonly Guid RunId = Guid.NewGuid();

        private readonly Channel<WorkEvent> _events = Channel.CreateUnbounded<WorkEvent>();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IDecisionHandler? _decisions;
        private CancellationToken _ct;

        public string? Problem => null;
        public SessionApprovals Session { get; } = new();
        public ApprovalStore Approvals { get; } = approvals;

        /// <summary>The run has its turn and drains what is emitted.</summary>
        public Task Started => _started.Task;

        public static WorkEvent Ev(EventKind kind, string summary, string? payload = null)
            => new(Guid.NewGuid(), Guid.NewGuid(), RunId, DateTimeOffset.UtcNow, kind, summary, payload);

        public void Emit(WorkEvent ev) => Assert.True(_events.Writer.TryWrite(ev));

        public void End() => _events.Writer.TryComplete();

        /// <summary>The run asks, as a step's tool call would: through the handler it was composed with.</summary>
        public Task<DecisionOutcome> AskAsync(DecisionRequest request) => _decisions!.RequestAsync(request, _ct);

        public async Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct)
        {
            (_decisions, _ct) = (decisions, ct);
            var drained = run.Session.DrainAsync(_events.Reader.ReadAllAsync(ct), ct);
            _started.TrySetResult();
            await drained;
        }
    }
}
