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
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
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

        Assert.Empty(bar.FindAll("button").Where(b => b.TextContent.Trim() is "Run" or "Stop" or "Run in background"));
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

    /// <summary>A run that goes on until it is cancelled, or the test is over.</summary>
    private sealed class Holds(ApprovalStore approvals) : IRunExecutor
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Problem => null;
        public SessionApprovals Session { get; } = new();
        public ApprovalStore Approvals { get; } = approvals;

        public void Release() => _released.TrySetResult();

        public async Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct)
            => await _released.Task.WaitAsync(ct);
    }
}
