namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Core.Permissions;
using Enactive.Server.Accounts;
using Enactive.Server.Components.Runs;
using Enactive.Server.Runs;
using Enactive.Settings;
using Enactive.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The Runs screen's command bar, as the window's (MainWindow.axaml, lines 459-545): the verb chips and the keys hint,
/// the box, Run / Stop / Run in background, and the workspace's run settings beside them - the worker, staging and the
/// level with its label on the slider. Every action is the service's to decide; the bar says what it said, under it.
/// </summary>
public sealed class CommandBarTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-bar", Guid.NewGuid().ToString("N"));
    private readonly Holds _executor;

    public CommandBarTests()
    {
        // Registered after the host's: a run that goes on until it is cancelled (or the test ends), so there is a run to
        // stop and a run in the background to see in the list.
        _executor = new Holds(new ApprovalStore(Path.Combine(_folders, "permissions.json")));
        Services.AddSingleton<IRunExecutor>(_executor);
        // The service runs only under its roots, and the host's options name none: every run would be refused for that.
        Services.AddSingleton(new WorkspaceRoots([_folders]));
    }

    protected override void Dispose(bool disposing)
    {
        _executor.Release();
        base.Dispose(disposing);
        if (disposing)
        {
            Enactive.Tests.SqlitePools.ClearUnder(_folders);
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
        }
    }

    private ServerEngine Engine => Services.GetRequiredService<ServerEngine>();
    private IRunControl Runs => Services.GetRequiredService<IRunControl>();

    /// <summary>A folder the service knows as a workspace, with these settings saved for it.</summary>
    private WorkspaceEntry Known(string name, int autonomy = 2, string? worker = null, bool stage = false)
    {
        var root = Path.Combine(_folders, name);
        Directory.CreateDirectory(root);
        Engine.Workspaces.Touch(root);
        Engine.Workspaces.SaveSettings(root, autonomy, worker, stage);
        return Engine.Workspaces.Find(root)!;
    }

    /// <summary>The service's settings name these workers (id, role), as an Admin saved them.</summary>
    private void Workers(params (string Id, string Role)[] workers)
    {
        var settings = new AppSettings();
        settings.Workers.Clear();
        foreach (var (id, role) in workers)
            settings.Workers.Add(new WorkerConfig { Id = id, Role = role });
        settings.Save(Engine.SettingsFile, replaceUnreadable: true);
        Engine.Reload();
    }

    private IRenderedComponent<CommandBar> Bar(string who, WorkspaceEntry? workspace, Guid? openRun = null)
    {
        SignIn(who);
        return Render<CommandBar>(p => p.Add(b => b.Workspace, workspace).Add(b => b.OpenRun, openRun));
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<CommandBar> bar, string name)
        => bar.FindAll("button").Single(b => b.TextContent.Trim() == name);

    private static string Said(IRenderedComponent<CommandBar> bar) => bar.Find(".command-bar .said").TextContent.Trim();

    private System.Security.Claims.ClaimsPrincipal As(string name) => ServerAuth.PrincipalOf(Users.Find(name)!);

    [Fact]
    public void A_viewer_sees_the_settings_read_only_and_no_run_buttons()
    {
        Workers(("writer", "Writer"), ("coder", "Coder"));
        var bar = Bar("vera", Known("project", autonomy: 1, worker: "coder", stage: true));

        // What the workspace runs with, said - and nothing to change it with.
        Assert.Equal("1", bar.Find("input[type=range]").GetAttribute("value"));
        Assert.True(bar.Find("input[type=range]").HasAttribute("disabled"));
        Assert.Equal("coder", bar.Find("select.worker").GetAttribute("value"));
        Assert.True(bar.Find("select.worker").HasAttribute("disabled"));
        Assert.True(bar.Find("input[type=checkbox]").HasAttribute("checked"));
        Assert.True(bar.Find("input[type=checkbox]").HasAttribute("disabled"));

        Assert.DoesNotContain(bar.FindAll("button"), b => b.TextContent.Trim() is "Run" or "Stop" or "Run in background");
        Assert.Empty(bar.FindAll("textarea"));
    }

    [Fact]
    public void The_bar_is_the_window_s_chips_hint_box_and_buttons()
    {
        var bar = Bar("alice", Known("project"));

        Assert.Equal(["Fix", "Build", "Investigate", "Explain", "Refactor"],
            bar.FindAll(".verbs button").Select(b => b.TextContent.Trim()));
        Assert.Equal("Ctrl+K to focus · Enter to run · Ctrl+Enter = newline", bar.Find(".command-bar .hint").TextContent.Trim());
        Assert.Equal("What do you want to accomplish?", bar.Find("textarea").GetAttribute("placeholder"));
        Assert.Equal(["Run", "Stop", "Run in background"],
            bar.FindAll(".run-buttons button").Select(b => b.TextContent.Trim()));
        // Nothing open to stop on the Runs screen: Stop is greyed, as the window's is when no run is going.
        Assert.True(Button(bar, "Stop").HasAttribute("disabled"));
        Assert.False(Button(bar, "Run").HasAttribute("disabled"));
    }

    [Fact]
    public void A_verb_chip_puts_the_verb_in_front_of_what_was_typed()
    {
        var bar = Bar("alice", Known("project"));

        bar.Find("textarea").Input("the build");
        Button(bar, "Fix").Click();

        Assert.Equal("Fix the build", bar.Find("textarea").GetAttribute("value"));
    }

    [Theory]
    [InlineData(0, "Observe — read only, asks before changes")]
    [InlineData(2, "Execute — edits freely, asks before run_command")]
    public void The_label_on_the_slider_names_the_workspace_s_level_in_its_colour(int level, string words)
    {
        var bar = Bar("alice", Known("project", autonomy: level));

        var label = bar.Find(".autonomy-label");
        Assert.Equal(words, label.TextContent.Trim());
        Assert.Contains($"tier-{level}", label.ClassList);
        // As many positions as there are tiers.
        Assert.Equal("3", bar.Find("input[type=range]").GetAttribute("max"));
    }

    [Fact]
    public void Moving_the_slider_saves_the_workspace_s_level()
    {
        var workspace = Known("project", autonomy: 2);
        var bar = Bar("alice", workspace);

        bar.Find("input[type=range]").Change("3");

        Assert.Equal(3, Engine.Workspaces.Find(workspace.RootPath)!.Autonomy);
        Assert.Equal("Autonomous — runs everything without asking", bar.Find(".autonomy-label").TextContent.Trim());
        Assert.Contains("tier-3", bar.Find(".autonomy-label").ClassList);
        Assert.Empty(bar.FindAll(".command-bar .said"));
    }

    [Fact]
    public void Staging_and_the_worker_are_saved_for_the_workspace()
    {
        Workers(("writer", "Writer"), ("coder", "Coder"));
        var workspace = Known("project");
        var bar = Bar("alice", workspace);

        bar.Find("input[type=checkbox]").Change(true);
        bar.Find("select.worker").Change("coder");

        var saved = Engine.Workspaces.Find(workspace.RootPath)!;
        Assert.Equal(("coder", true), (saved.WorkerId, saved.StageChanges));
    }

    /// <summary>
    /// The window saved a worker's role name until 2026-10-08: such a folder's worker is still the one picked, by the
    /// rule a run reads it by (WorkerNames.Resolve) - read by id alone, the picker fell back to the first worker and the
    /// next save quietly changed the workspace's worker.
    /// </summary>
    [Fact]
    public void A_worker_saved_by_its_role_name_is_the_one_picked()
    {
        Workers(("writer", "Writer"), ("coder", "Coder"));
        var bar = Bar("alice", Known("project", worker: "Coder"));

        Assert.Equal("coder", bar.Find("select.worker").GetAttribute("value"));
        Assert.Equal(["Writer", "Coder"], bar.FindAll("select.worker option").Select(o => o.TextContent.Trim()));
    }

    /// <summary>
    /// A run goes with the level the person saw. Another Operator raised it after this bar read it: Run is refused in words
    /// that say what is saved now, the bar shows it, and the next Run - made knowing it - goes. Started with the saved
    /// level, the first Run was a run at "Autonomous" from a bar that said "Suggest".
    /// </summary>
    [Fact]
    public void Run_is_refused_when_the_level_was_changed_since_the_bar_read_it()
    {
        var workspace = Known("project", autonomy: 1);
        var bar = Bar("alice", workspace);
        Assert.Null(Services.GetRequiredService<ServerHistory>().SaveSettings(As("ada"), workspace.RootPath, 3, null, false));

        bar.Find("textarea").Input("write the digest");
        Button(bar, "Run").Click();

        Assert.Equal($"The level was changed to “{Enactive.Agents.AutonomyTiers.Describe(3)}” since this page read it. "
            + "The bar shows it now; run again to run with it.", Said(bar));
        Assert.Empty(Runs.List(As("alice")));
        Assert.Equal("3", bar.Find("input[type=range]").GetAttribute("value"));

        Button(bar, "Run").Click();
        Assert.Single(Runs.List(As("alice")));
    }

    /// <summary>
    /// The worker and staging, as the level: what the bar showed against what is saved, each by the rule the bar reads it
    /// by - a worker saved by its role name is the one shown, and none saved is the first listed.
    /// </summary>
    [Fact]
    public void Settings_changed_since_a_page_read_them_are_said()
    {
        IReadOnlyList<(string Id, string Name)> workers = [("writer", "Writer"), ("coder", "Coder")];
        var saved = BarSettings.Of(new WorkspaceEntry("w", "C:\\w", DateTimeOffset.Now, Autonomy: 2, WorkerId: "Coder", StageChanges: true), workers);

        Assert.Null(saved.ChangedSince(new BarSettings(2, "coder", true), workers));
        Assert.Equal("The worker was changed to “Coder” since this page read it. The bar shows it now; run again to run with it.",
            saved.ChangedSince(new BarSettings(2, null, true), workers));
        Assert.Equal("Stage changes was turned on since this page read it. The bar shows it now; run again to run with it.",
            saved.ChangedSince(new BarSettings(2, "coder", false), workers));
        Assert.Null(BarSettings.Of(null, workers).ChangedSince(new BarSettings(2, null, false), workers));
    }

    [Fact]
    public void Run_starts_the_run_and_opens_it()
    {
        var workspace = Known("project");
        var bar = Bar("alice", workspace);

        bar.Find("textarea").Input("write the digest");
        Button(bar, "Run").Click();

        var run = Assert.Single(Runs.List(As("alice")));
        Assert.Equal(("write the digest", workspace.RootPath, false), (run.Prompt, run.Workspace, run.Background));
        Assert.EndsWith($"/runs/{run.Id}", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void Run_in_background_starts_and_stays()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        var bar = Bar("alice", Known("project"));
        var here = navigation.Uri;

        bar.Find("textarea").Input("digest the inbox");
        Button(bar, "Run in background").Click();

        var run = Assert.Single(Runs.List(As("alice")));
        Assert.Equal(("digest the inbox", true), (run.Prompt, run.Background));
        Assert.Equal(here, navigation.Uri);
        // Cleared, for the next one: the run is on its way, and the box is free again.
        Assert.Equal("", bar.Find("textarea").GetAttribute("value") ?? "");
    }

    [Fact]
    public void A_refusal_is_said_under_the_bar()
    {
        var gone = Known("gone");
        Directory.Delete(gone.RootPath);
        var bar = Bar("alice", gone);

        bar.Find("textarea").Input("write the digest");
        Button(bar, "Run").Click();

        Assert.StartsWith("No such folder:", Said(bar));
        Assert.Empty(Runs.List(As("alice")));
        // What was typed stays, to be run again somewhere else.
        Assert.Equal("write the digest", bar.Find("textarea").GetAttribute("value"));
    }

    /// <summary>
    /// A folder that stages cannot have a run nobody watches: Run in background there is refused at once, in the composer's
    /// words (the service's EngineExecutor says them), under the bar - and what was typed stays, to be run and watched.
    /// </summary>
    [Fact]
    public void Run_in_background_in_a_folder_that_stages_is_refused_under_the_bar()
    {
        var workspace = Known("project", stage: true);
        var composer = new EngineExecutor(Engine, new ServerStaging(Users, Services.GetRequiredService<KnownWorkspaces>()));
        _executor.RefuseBackground = root => composer.BackgroundRefusal(root, "alice");
        var bar = Bar("alice", workspace);

        bar.Find("textarea").Input("digest the inbox");
        Button(bar, "Run in background").Click();

        Assert.Contains("a run nobody is watching cannot stage", Said(bar));
        Assert.Empty(Runs.List(As("alice")));
        Assert.Equal("digest the inbox", bar.Find("textarea").GetAttribute("value"));
    }

    /// <summary>The service decides, whatever the page still offers: an account disabled a second ago starts nothing.</summary>
    [Fact]
    public void An_account_disabled_while_the_page_is_open_is_told_its_session_ended()
    {
        var bar = Bar("alice", Known("project"));
        Users.SetDisabled("alice", true);

        bar.Find("textarea").Input("write the digest");
        Button(bar, "Run").Click();

        Assert.Equal("Your session has ended. Sign in again.", Said(bar));
        Users.SetDisabled("alice", false);
        Assert.Empty(Runs.List(As("alice")));
    }

    /// <summary>A role lowered while the page is open: its next save is refused, and the saved level stays.</summary>
    [Fact]
    public void A_role_lowered_while_the_page_is_open_cannot_save_the_settings()
    {
        var workspace = Known("project", autonomy: 1);
        var bar = Bar("alice", workspace);
        Users.SetRole("alice", Role.Viewer);

        bar.Find("input[type=range]").Change("3");

        Assert.Equal("Your session has ended. Sign in again.", Said(bar));
        Assert.Equal(1, Engine.Workspaces.Find(workspace.RootPath)!.Autonomy);
        // The slider goes back to what is saved: left at 3, it said a level the next run would not have.
        Assert.Equal("1", bar.Find("input[type=range]").GetAttribute("value"));
    }

    [Fact]
    public void Stop_cancels_the_open_run_and_greys_the_buttons_that_start_one_while_it_goes()
    {
        var workspace = Known("project");
        SignIn("alice");
        var run = Runs.Start(As("alice"), workspace.RootPath, "write the digest").Run!;
        var bar = Bar("alice", workspace, run.Id);

        // One run in the foreground at a time, as the window: while it goes, Run and Run in background are greyed.
        Assert.True(Button(bar, "Run").HasAttribute("disabled"));
        Assert.True(Button(bar, "Run in background").HasAttribute("disabled"));

        Button(bar, "Stop").Click();

        Assert.True(run.Ended.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal("alice", run.CancelledBy);
        bar.WaitForAssertion(() => Assert.True(Button(bar, "Stop").HasAttribute("disabled")));
        Assert.False(Button(bar, "Run").HasAttribute("disabled"));
    }

    /// <summary>Enter in the box runs (CommandBar.razor.js calls back with what is in it), as the window's Enter does.</summary>
    [Fact]
    public async Task Enter_in_the_box_runs_what_it_holds()
    {
        var bar = Bar("alice", Known("project"));
        CommandBarScript.VerifyInvoke("attach");

        await bar.InvokeAsync(() => bar.Instance.RunFromKeyboard("write the digest"));

        Assert.Equal("write the digest", Assert.Single(Runs.List(As("alice"))).Prompt);
    }

    /// <summary>
    /// The Runs screen's bar runs in the workspace its column shows - and in none when the address names one the service
    /// does not know: the old start form fell back to the first workspace there, a run in a folder nobody had on screen.
    /// </summary>
    [Fact]
    public void On_the_runs_screen_the_bar_runs_in_the_workspace_shown_and_never_another()
    {
        Known("alpha");
        var beta = Known("beta");
        SignIn("alice");
        var navigation = Services.GetRequiredService<NavigationManager>();

        navigation.NavigateTo($"/?ws={Enactive.Core.Context.WorkspaceInfo.For(beta.RootPath).Id}");
        var screen = Render<RunsScreen>();
        screen.Find(".command-bar textarea").Input("write the digest");
        screen.FindAll(".command-bar button").Single(b => b.TextContent.Trim() == "Run").Click();
        Assert.Equal(beta.RootPath, Assert.Single(Runs.List(As("alice"))).Workspace);

        navigation.NavigateTo($"/?ws={Guid.NewGuid()}");
        screen = Render<RunsScreen>();
        screen.Find(".command-bar textarea").Input("write the digest");
        screen.FindAll(".command-bar button").Single(b => b.TextContent.Trim() == "Run").Click();
        Assert.Equal("Name the folder the run is to work in.", screen.Find(".command-bar .said").TextContent.Trim());
        Assert.Single(Runs.List(As("alice")));
    }

    /// <summary>
    /// Enter pressed twice before the page has left for the run: one run. The second press still sent the text the box
    /// held - the browser had not drawn it cleared yet - and started the same task a second time.
    /// </summary>
    [Fact]
    public async Task A_second_enter_before_the_run_opens_starts_nothing_more()
    {
        var bar = Bar("alice", Known("project"));

        await bar.InvokeAsync(() => bar.Instance.RunFromKeyboard("write the digest"));
        await bar.InvokeAsync(() => bar.Instance.RunFromKeyboard("write the digest"));

        Assert.Single(Runs.List(As("alice")));
    }

    /// <summary>
    /// Run's tooltip is the window's (RunButton.Tip): what it does, and - while the open run goes - why it is greyed. The bar
    /// said "Run this here, and watch it." over a button the run under it had greyed.
    /// </summary>
    [Fact]
    public void The_run_button_says_why_it_is_greyed_while_the_open_run_goes()
    {
        var workspace = Known("project");
        var idle = Bar("alice", workspace);
        Assert.Equal(Enactive.Application.RunButton.Tip(busy: false), Button(idle, "Run").GetAttribute("title"));

        var run = Runs.Start(As("alice"), workspace.RootPath, "write the digest").Run!;
        var busy = Bar("alice", workspace, run.Id);
        Assert.Equal(Enactive.Application.RunButton.Tip(busy: true, CommandBarState.RunHeld), Button(busy, "Run").GetAttribute("title"));
        // The window's reason is not the service's: the bar is held by the run open under it alone, and greys Run in
        // background too.
        Assert.DoesNotContain("one foreground run at a time", Button(busy, "Run").GetAttribute("title"));
        Assert.DoesNotContain("not held by it", Button(busy, "Run").GetAttribute("title"));
    }

    /// <summary>
    /// The bar and the run share a page: once the run Run opened is on screen and has ended, Enter runs again. The guard
    /// against a second Enter before the page left was never let go, and with Run staying on the page every later Enter
    /// did nothing.
    /// </summary>
    [Fact]
    public async Task Once_the_run_it_opened_is_shown_and_over_enter_runs_again()
    {
        var workspace = Known("project");
        var bar = Bar("alice", workspace);

        await bar.InvokeAsync(() => bar.Instance.RunFromKeyboard("write the digest"));
        var first = Assert.Single(Runs.List(As("alice")));
        // The screen arrives at the run's address and gives the bar the run it shows.
        bar.Render(p => p.Add(b => b.OpenRun, first.Id));
        Assert.Null(Runs.Cancel(As("alice"), first.Id));
        Assert.True(first.Ended.Wait(TimeSpan.FromSeconds(10)));

        await bar.InvokeAsync(() => bar.Instance.RunFromKeyboard("write the digest again"));

        Assert.Equal(2, Runs.List(As("alice")).Count);
    }

    /// <summary>
    /// Whether the open run is going is asked once a drawing: every disabled button asked the service again, seven times a
    /// draw, each a lock and a search of every run - and the bar is drawn again with every event of the run beside it.
    /// </summary>
    [Fact]
    public void The_open_run_is_looked_up_once_a_drawing()
    {
        // The service's runs, counted: wrapped where they are registered, before anything is asked of the services.
        var registered = Services.Last(d => d.ServiceType == typeof(IRunControl));
        Services.Remove(registered);
        Services.AddSingleton<IRunControl>(sp => new CountingRuns((IRunControl)registered.ImplementationFactory!(sp)));
        var workspace = Known("project");
        var run = Runs.Start(As("alice"), workspace.RootPath, "write the digest").Run!;
        var bar = Bar("alice", workspace, run.Id);
        var counting = (CountingRuns)Runs;

        counting.Finds = 0;
        bar.Render();

        Assert.Equal(1, counting.Finds);
    }

    /// <summary>The service's runs, counting how often a run is looked up.</summary>
    private sealed class CountingRuns(IRunControl inner) : IRunControl
    {
        public int Finds;

        public ServerRun? Find(System.Security.Claims.ClaimsPrincipal user, Guid id)
        {
            Interlocked.Increment(ref Finds);
            return inner.Find(user, id);
        }

        public TimeSpan? QuestionWait => inner.QuestionWait;
        public StartResult Start(System.Security.Claims.ClaimsPrincipal user, string workspace, string prompt, BarSettings? shown = null) => inner.Start(user, workspace, prompt, shown);
        public StartResult StartBackground(System.Security.Claims.ClaimsPrincipal user, string workspace, string prompt, BarSettings? shown = null) => inner.StartBackground(user, workspace, prompt, shown);
        public Task<StartResult> ResumeAsync(System.Security.Claims.ClaimsPrincipal user, string workspace, Guid runId) => inner.ResumeAsync(user, workspace, runId);
        public StartResult Again(System.Security.Claims.ClaimsPrincipal user, string workspace, Enactive.Application.Rerun again, string action) => inner.Again(user, workspace, again, action);
        public StartResult StartScheduled(Enactive.Application.DueRun due) => inner.StartScheduled(due);
        public string? Refusal(System.Security.Claims.ClaimsPrincipal user, string workspace) => inner.Refusal(user, workspace);
        public Task<StartResult> ContinueAsync(System.Security.Claims.ClaimsPrincipal user, string workspace, Enactive.Agents.ParkedDecision parked, string title) => inner.ContinueAsync(user, workspace, parked, title);
        public IReadOnlyList<ServerRun> List(System.Security.Claims.ClaimsPrincipal user) => inner.List(user);
        public string? Cancel(System.Security.Claims.ClaimsPrincipal user, Guid id) => inner.Cancel(user, id);
        public string? Answer(System.Security.Claims.ClaimsPrincipal user, Guid id, Guid question, string choice) => inner.Answer(user, id, question, choice);
    }

    /// <summary>A run that goes on until it is cancelled, or the test is over.</summary>
    private sealed class Holds(ApprovalStore approvals) : IRunExecutor
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Problem => null;
        public SessionApprovals Session { get; } = new();
        public ApprovalStore Approvals { get; } = approvals;

        /// <summary>What a background run in a folder is refused with - none by default, the composer's in one test.</summary>
        public Func<string, string?> RefuseBackground { get; set; } = _ => null;

        public string? BackgroundRefusal(string root, string owner) => RefuseBackground(root);

        public void Release() => _released.TrySetResult();

        public async Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct)
            => await _released.Task.WaitAsync(ct);
    }
}
