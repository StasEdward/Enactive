namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Agents;
using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Diagnostics;
using Enactive.Core.Memory;
using Enactive.Server.Accounts;
using Enactive.Server.Components;
using Enactive.Server.Components.Pages;
using Enactive.Server.Components.Ui;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The log, the Timeline and the Environment as the window's (LogWindow.axaml, LogAnalysisWindow.axaml, the viewer): the log
/// binds the window's own view model over the service's one hub - its Clear is this page's alone, its raw-wire switch an
/// Admin's and its analysis an Operator's, each ServerLogs' to decide; the Timeline is the workspace's project memory and
/// the Environment the service's machine, in the viewer, with the window's words when there is none to show.
/// </summary>
public sealed class LogTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-log-pages", Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public LogTests()
    {
        _root = Path.Combine(_folders, "project");
        Directory.CreateDirectory(_root);
        // The page's script - following the tail, Export's download - is the browser's: its calls are recorded, and answer nothing.
        LogScript = JSInterop.SetupModule("./Components/Pages/LogScreen.razor.js");
        LogScript.Mode = JSRuntimeMode.Loose;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
    }

    private BunitJSModuleInterop LogScript { get; }

    private LogHub Hub => Services.GetRequiredService<ServerEngine>().Log;

    private Guid WorkspaceId => WorkspaceInfo.For(_root).Id;

    private void Known() => Services.GetRequiredService<ServerEngine>().Workspaces.Touch(_root);

    /// <summary>Logs a line on the service's hub and waits for its pump to have taken it.</summary>
    private void Logged(string message, LogLevel level = LogLevel.Info)
    {
        Hub.Log(new LogEntry(0, DateTimeOffset.UtcNow, level, LogSource.Host, null, null, message, null, null));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Hub.Snapshot().All(e => e.Message != message))
        {
            Assert.True(DateTime.UtcNow < deadline, "The hub never took the line.");
            Thread.Sleep(10);
        }
    }

    private IRenderedComponent<LogScreen> Log(string who)
    {
        SignIn(who);
        var log = Render<LogScreen>();
        log.WaitForAssertion(() => Assert.NotEmpty(log.Find(".log-status").TextContent.Trim()));
        return log;
    }

    private static string[] Lines(IRenderedComponent<LogScreen> log) => log.FindAll(".log-line").Select(l => l.TextContent).ToArray();

    /// <summary>
    /// Does something to the page, found again if it was drawn anew meanwhile: the page redraws as it drains the lines that
    /// arrive, and a control found a redraw ago has a handler the page no longer has.
    /// </summary>
    private static void Act(Action act)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                act();
                return;
            }
            catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < 10)
            {
            }
        }
    }

    private static void Press(IRenderedComponent<LogScreen> log, string button)
        => Act(() => log.FindAll("button").Single(b => b.TextContent.Trim() == button).Click());

    private static string[] Offered(IRenderedComponent<LogScreen> log)
        => log.FindAll("button, label").Select(b => b.TextContent.Trim()).ToArray();

    /// <summary>
    /// Capturing the raw wire changes what the service records for everybody - every prompt and reply whole - so it is an
    /// Admin's: offered to nobody else, and when an Admin turns it on the hub records Trace, the audit trail says who, and
    /// off it goes back to the level the service runs at. AI Analyze spends a model's time: an Operator's.
    /// </summary>
    [Fact]
    public void Capture_raw_wire_is_an_admin_s()
    {
        Assert.DoesNotContain(Offered(Log("vera")), o => o.StartsWith("Capture raw wire") || o == "AI Analyze");
        var operatorPage = Log("alice");
        operatorPage.WaitForAssertion(() => Assert.Contains("AI Analyze", Offered(operatorPage)));
        Assert.DoesNotContain(Offered(operatorPage), o => o.StartsWith("Capture raw wire"));

        var log = Log("ada");
        log.WaitForAssertion(() => Assert.Contains(Offered(log), o => o.StartsWith("Capture raw wire")));
        Assert.Equal(LogLevel.Debug, Hub.MinLevel);
        Act(() => log.Find(".log-check input[type=checkbox]").Change(true));

        Assert.Equal(LogLevel.Trace, Hub.MinLevel);
        Assert.Contains(Users.AuditTrail(10), a => a.Account == "ada" && a.Action == "raw wire capture on");

        Act(() => log.Find(".log-check input[type=checkbox]").Change(false));
        Assert.Equal(LogLevel.Debug, Hub.MinLevel);
        Assert.Contains(Users.AuditTrail(10), a => a.Account == "ada" && a.Action == "raw wire capture off");
    }

    /// <summary>
    /// An Admin demoted while the page is open ticks Capture: the service refuses, the hub stays where it was, the status
    /// says why, and the box is drawn unticked - drawn one way, a refused tick stayed ticked in the browser over capture that
    /// was off. What the browser's own box shows is tests/server-e2e's to see: bUnit holds only what the page drew.
    /// </summary>
    [Fact]
    public void A_refused_capture_is_drawn_unticked()
    {
        var log = Log("ada");
        log.WaitForAssertion(() => Assert.Contains(Offered(log), o => o.StartsWith("Capture raw wire")));
        Users.SetRole("ada", Role.Operator);

        Act(() => log.Find(".log-check input[type=checkbox]").Change(true));

        Assert.Equal(LogLevel.Debug, Hub.MinLevel);
        Assert.DoesNotContain(Users.AuditTrail(10), a => a.Action == "raw wire capture on");
        log.WaitForAssertion(() => Assert.False(log.Find(".log-check input[type=checkbox]").HasAttribute("checked")));
    }

    /// <summary>
    /// Clear empties this page's view and nothing else: the service's log is everybody's, and the window's Clear - which
    /// empties its own hub - emptied every other open log page, and the next analysis' material, with it.
    /// </summary>
    [Fact]
    public void Clear_empties_this_view_only()
    {
        Logged("everybody's line");
        var log = Log("vera");
        log.WaitForAssertion(() => Assert.Contains(Lines(log), l => l.EndsWith("everybody's line")));

        Press(log, "Clear");

        log.WaitForAssertion(() => Assert.Empty(Lines(log)));
        Assert.Contains(Hub.Snapshot(), e => e.Message == "everybody's line");
        var other = Render<LogScreen>();
        other.WaitForAssertion(() => Assert.Contains(other.FindAll(".log-line"), l => l.TextContent.EndsWith("everybody's line")));
    }

    /// <summary>A line logged while the page is open arrives on it - drained in batches, not one redraw per line.</summary>
    [Fact]
    public void A_line_logged_while_the_page_is_open_arrives_on_it()
    {
        var log = Log("vera");

        Logged("arrived after the page opened", LogLevel.Warn);

        log.WaitForAssertion(() => Assert.Contains(log.FindAll(".log-line"),
            l => l.TextContent.EndsWith("arrived after the page opened") && l.ClassList.Contains(Looks.Log(LogLevel.Warn))));
    }

    /// <summary>A line chosen shows its whole payload under the list, as the window's detail box.</summary>
    [Fact]
    public void A_chosen_line_shows_its_whole_payload()
    {
        Hub.Log(new LogEntry(0, DateTimeOffset.UtcNow, LogLevel.Info, LogSource.Llm, null, null, "response", "the whole reply", "chat"));
        Logged("a later line");
        var log = Log("vera");
        log.WaitForAssertion(() => Assert.Contains(Lines(log), l => l.EndsWith("response")));

        Act(() => log.FindAll(".log-line").Single(l => l.TextContent.EndsWith("response")).Click());

        log.WaitForAssertion(() => Assert.Contains("the whole reply", log.Find("textarea.log-detail").GetAttribute("value")));
    }

    /// <summary>Export hands the visible log to the browser as a file: what LogText makes of the lines shown.</summary>
    [Fact]
    public void Export_downloads_the_visible_log()
    {
        Logged("a line to export");
        var log = Log("vera");
        log.WaitForAssertion(() => Assert.Contains(Lines(log), l => l.EndsWith("a line to export")));

        Press(log, "Export");

        log.WaitForAssertion(() =>
        {
            var download = Assert.Single(LogScript.Invocations, i => i.Identifier == "download");
            Assert.Matches(@"^export-\d{8}-\d{6}\.log$", (string)download.Arguments[0]!);
            Assert.Contains("a line to export", (string)download.Arguments[1]!);
        });
        log.WaitForAssertion(() => Assert.StartsWith("Exported ", log.Find(".log-status").TextContent.Trim()));
    }

    /// <summary>With no engine there is no model to ask; the page says so as the window says a failed analysis.</summary>
    [Fact]
    public void An_analysis_with_no_engine_says_why_it_did_not_run()
    {
        Logged("something to ask about");
        var log = Log("alice");
        log.WaitForAssertion(() => Assert.Contains(Lines(log), l => l.EndsWith("something to ask about")));

        Press(log, "AI Analyze");

        log.WaitForAssertion(() => Assert.StartsWith("The analysis did not run: The service has no engine",
            log.Find(".log-status").TextContent.Trim()));
    }

    /// <summary>
    /// The analysis as LogAnalysisWindow lays it out: what was read above the answer, the answer rendered, Source shows the
    /// Markdown as the model wrote it, Copy hands over the Markdown, and what it cost beside the buttons.
    /// </summary>
    [Fact]
    public void The_analysis_shows_what_was_read_the_answer_rendered_and_its_source()
    {
        var result = new LogAnalysisResult("### Problems\n- the **build** failed", "a-model", 10, 40, 4000, 200);

        var viewer = Render<TextViewer>(p => p.Add(v => v.Title, "Log analysis").Add(v => v.Lead, result.Provenance)
            .Add(v => v.Text, result.Answer).Add(v => v.Note, result.Cost).Add(v => v.CopyLabel, "Copy")
            .Add(v => v.Rendered, (RenderFragment)(b => { b.OpenComponent<MarkdownView>(0); b.AddAttribute(1, nameof(MarkdownView.Text), result.Answer); b.CloseComponent(); })));

        Assert.Equal(result.Provenance, viewer.Find(".viewer-lead").TextContent);
        Assert.Equal("Problems", viewer.Find(".viewer-rendered h5").TextContent);
        Assert.Equal("build", viewer.Find(".viewer-rendered strong").TextContent);
        Assert.Equal("4,200 tokens", viewer.Find(".viewer-status").TextContent);
        Assert.Contains(viewer.FindAll("button"), b => b.TextContent.Trim() == "Copy");

        viewer.FindAll("button").Single(b => b.TextContent.Trim() == "Source").Click();
        Assert.Equal(result.Answer, viewer.Find("pre.viewer-text").TextContent);
    }

    /// <summary>
    /// The analysis' buttons as LogAnalysisWindow has them: Copy says it copies the Markdown whichever view is showing, Close
    /// is the primary button, and "Copied." stands beside what the answer cost rather than in its place.
    /// </summary>
    [Fact]
    public void The_analysis_s_copy_says_what_it_copies_close_is_primary_and_copied_keeps_the_cost()
    {
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetVoidResult();
        var result = new LogAnalysisResult("All fine.", "a-model", 10, 10, 4000, 200);

        var viewer = Render<TextViewer>(p => p.Add(v => v.Title, "Log analysis").Add(v => v.Text, result.Answer)
            .Add(v => v.Note, result.Cost).Add(v => v.CopyLabel, "Copy")
            .Add(v => v.CopyTip, "Copies the answer as Markdown, whichever view is showing.").Add(v => v.CloseIsPrimary, true)
            .Add(v => v.OnClose, () => { }));

        var copy = viewer.FindAll("button").Single(b => b.TextContent.Trim() == "Copy");
        Assert.Equal("Copies the answer as Markdown, whichever view is showing.", copy.GetAttribute("title"));
        Assert.Contains("primary", viewer.FindAll("button").Single(b => b.TextContent.Trim() == "Close").ClassList);

        copy.Click();

        viewer.WaitForAssertion(() =>
        {
            var status = viewer.Find(".viewer-status").TextContent;
            Assert.Contains("Copied.", status);
            Assert.Contains("4,200 tokens", status);
        });
    }

    /// <summary>A model's answer that quotes markup shows the markup it quoted: text goes in as text.</summary>
    [Fact]
    public void Markdown_puts_text_in_as_text()
    {
        var view = Render<MarkdownView>(p => p.Add(v => v.Text, "The page said <script>alert(1)</script> and **stopped**."));

        Assert.Empty(view.FindAll("script"));
        Assert.Contains("<script>alert(1)</script>", view.Find("p").TextContent);
    }

    /// <summary>
    /// The Timeline is the workspace's project memory as the window's viewer shows it - ProjectMemory over its runs and its
    /// memory - with Copy all and nothing to switch to: the window shows it as its own text.
    /// </summary>
    [Fact]
    public async Task The_timeline_is_the_workspace_s_project_memory()
    {
        Known();
        var workspace = WorkspaceInfo.For(_root);
        await MemoryStoreFactory.Create(workspace).AppendAsync(
            new MemoryEntry(Guid.NewGuid(), workspace.Id, "decision", "Keep the digest weekly.", null, DateTimeOffset.UtcNow), CancellationToken.None);
        SignIn("vera");

        Services.GetRequiredService<NavigationManager>().NavigateTo("/" + Links.Timeline(WorkspaceId));
        var timeline = Render<TimelineScreen>();

        var expected = ProjectMemory.Render(await RunStoreFactory.Create(workspace).LoadSummariesAsync(CancellationToken.None),
            await MemoryStoreFactory.Create(workspace).LoadAllAsync(CancellationToken.None), workspace.RootPath);
        // The page's text as the browser has it: an HTML parser reads a CRLF as one line break.
        timeline.WaitForAssertion(() => Assert.Equal(expected.ReplaceLineEndings("\n"), timeline.Find(".viewer-inline pre.viewer-text").TextContent));
        Assert.Equal("Project Memory", timeline.Find(".viewer-title").TextContent);
        var buttons = timeline.FindAll(".viewer-actions button").Select(b => b.TextContent.Trim()).ToArray();
        Assert.Equal(["Copy all"], buttons);
    }

    /// <summary>With no workspace the service knows, the window's words: "Set a workspace first."</summary>
    [Fact]
    public void With_no_workspace_the_reports_say_to_set_one()
    {
        SignIn("vera");

        var environment = Render<EnvironmentScreen>();

        environment.WaitForAssertion(() => Assert.Equal("Set a workspace first.", environment.Find("pre.viewer-text").TextContent));
    }

    /// <summary>
    /// The Environment is the service's machine, probed for the workspace once - not on every redraw, the probe runs git,
    /// docker and wsl - and a probe that fails is said in the window's words.
    /// </summary>
    [Fact]
    public void The_environment_is_probed_once_and_a_failed_probe_is_said()
    {
        var probes = 0;
        Services.AddSingleton<ProbeEnvironment>((_, _) =>
        {
            Interlocked.Increment(ref probes);
            throw new InvalidOperationException("docker is not answering");
        });
        Known();
        SignIn("vera");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/" + Links.Environment(WorkspaceId));

        var environment = Render<EnvironmentScreen>();
        environment.WaitForAssertion(() => Assert.Equal("Probe failed: docker is not answering", environment.Find("pre.viewer-text").TextContent));
        environment.Render();
        environment.Render();

        Assert.Equal(1, probes);
    }

    /// <summary>A workspace the address names that the service does not know is said, and nothing is read.</summary>
    [Fact]
    public void An_unknown_workspace_is_said()
    {
        SignIn("vera");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/" + Links.Timeline(Guid.NewGuid()));

        var timeline = Render<TimelineScreen>();

        timeline.WaitForAssertion(() => Assert.Equal("The service does not know that workspace.", timeline.Find(".said").TextContent));
        Assert.Empty(timeline.FindAll("pre.viewer-text"));
    }
}
