namespace Enactive.Server.Tests.Components;

using System.Threading.Channels;
using Bunit;
using Enactive.Application;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Server.Accounts;
using Enactive.Server.Components.Pages;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The check manifests from the browser (Plan Phase 3.5), as the window's Checks lists: the service's global ones in Settings
/// - an Admin's, in the service's own data folder, the one its engine reads, with no engine needed - and a workspace's on its
/// Checks screen - an Operator's - read by anybody signed in. Every change is ServerChecks', as the person, audited; and a
/// workspace's checks are not changed while a run works there, because the run puts them back at its end.
/// </summary>
public sealed class ChecksTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-checks-web", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly Holds _engine;

    public ChecksTests()
    {
        _engine = new Holds(new ApprovalStore(Path.Combine(_folders, "permissions.json")));
        Services.AddSingleton<IRunExecutor>(_engine);
        Services.AddSingleton(new WorkspaceRoots([_folders]));
        _root = Path.Combine(_folders, "project");
        Directory.CreateDirectory(_root);
        Services.GetRequiredService<ServerEngine>().Workspaces.Touch(_root);
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

    private string DataChecks => Path.Combine(Services.GetRequiredService<ServerFolders>().Data, "checks");
    private string WorkspaceChecks => Path.Combine(_root, ".enactive", "checks");

    private System.Security.Claims.ClaimsPrincipal As(string who) => ServerAuth.PrincipalOf(Users.Find(who)!);

    private IRenderedComponent<ChecksScreen> Screen(string who)
    {
        SignIn(who);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/checks?ws={WorkspaceInfo.For(_root).Id}");
        var screen = Render<ChecksScreen>();
        screen.WaitForAssertion(() => Assert.NotEmpty(screen.FindAll(".checks-list, .said")));
        return screen;
    }


    /// <summary>Adds lint through the open page's form, as a person would.</summary>
    private static void AddLint<T>(IRenderedComponent<T> page, string matches = "src/**") where T : IComponent
    {
        page.Find(".checks-add").Click();
        // The form is drawn after the click is handled, not by it: found at once, it was not there yet on a slower CI
        // runner (PR #58).
        page.WaitForAssertion(() => page.Find("#check-id"));
        page.Find("#check-id").Change("lint");
        page.Find("#check-command").Change("npm run lint");
        page.Find("#check-matches").Change(matches);
        page.Find(".check-save").Click();
    }

    private (DateTimeOffset At, string Account, string Action, string? Detail, string? Remote)[] Audited(string action)
        => Users.AuditTrail().Where(a => a.Action == action).ToArray();

    [Fact]
    public void An_admin_edits_the_global_checks_in_the_service_s_data_folder_with_no_engine_built()
    {
        Assert.Null(Services.GetRequiredService<ServerEngine>().Current);
        SignIn("ada");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/ai-checks");
        var screen = Render<SettingsScreen>(p => p.Add(s => s.Section, "ai-checks"));
        screen.WaitForAssertion(() => Assert.NotEmpty(screen.FindAll(".checks-list")));
        Assert.Equal(DataChecks, screen.Find(".checks-folder").TextContent.Trim());

        AddLint(screen);

        screen.WaitForAssertion(() => Assert.True(File.Exists(Path.Combine(DataChecks, "lint.json"))));
        Assert.Equal(DataChecks, Services.GetRequiredService<ServerChecks>().GlobalFolder);
        var audited = Assert.Single(Audited("check saved"));
        Assert.Equal("ada", audited.Account);
        Assert.Contains($"lint (Global) in {DataChecks}", audited.Detail);
        Assert.Contains("Global", screen.Find(".check-row .check-origin").TextContent);
    }

    [Fact]
    public void An_operator_edits_the_workspace_s_checks_and_deletes_them()
    {
        var screen = Screen("alice");

        AddLint(screen);
        screen.WaitForAssertion(() => Assert.True(File.Exists(Path.Combine(WorkspaceChecks, "lint.json"))));
        Assert.Single(Audited("check saved"));

        screen.Find(".check-row .check-remove").Click();
        // Asked first, in a dialog; Delete deletes it.
        screen.Find("[role=dialog]").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Delete").Click();
        screen.WaitForAssertion(() => Assert.False(File.Exists(Path.Combine(WorkspaceChecks, "lint.json"))));
        Assert.Equal("alice", Assert.Single(Audited("check deleted")).Account);
    }

    [Fact]
    public void A_viewer_reads_the_checks_and_cannot_change_them()
    {
        new CheckStore(_root, DataChecks).Save(
            new CheckDefinition("lint", null, "npm run lint", ["src/**"], CheckFire.Step, CheckSeverity.Advisory, 60, null), CheckScope.Workspace);

        var screen = Screen("vera");

        Assert.Equal("lint", screen.Find(".check-row .card-title").TextContent.Trim());
        Assert.Empty(screen.FindAll(".checks-add, .check-edit, .check-toggle, .check-remove"));
        // Nor through the service, whatever a page sent.
        var checks = Services.GetRequiredService<ServerChecks>();
        Assert.Equal("Your role does not include changing a workspace's checks.",
            checks.Delete(As("vera"), _root, "lint", CheckScope.Workspace));
        Assert.True(File.Exists(Path.Combine(WorkspaceChecks, "lint.json")));
    }

    /// <summary>The global checks judge every workspace's runs, as the settings do: an Admin's, not an Operator's.</summary>
    [Fact]
    public void An_operator_cannot_change_the_global_checks()
    {
        var checks = Services.GetRequiredService<ServerChecks>();
        var lint = new CheckManifestSaid(Command: "npm run lint", Matches: ["src/**"]);

        Assert.Equal("Your role does not include changing the service's global checks.", checks.Save(As("alice"), _root, "lint", lint, CheckScope.Global));
        Assert.False(Directory.Exists(DataChecks) && Directory.EnumerateFiles(DataChecks).Any());
        var (list, _) = checks.Global(As("alice"), _root);
        Assert.False(list!.CanChange);
        Assert.Null(checks.Save(As("ada"), _root, "lint", lint, CheckScope.Global));
    }

    /// <summary>
    /// A run working in the workspace puts its checks back at its end as they were when it started: a change made meanwhile
    /// would be undone without a word, so it is refused, and the page says why.
    /// </summary>
    [Fact]
    public async Task Nothing_is_changed_in_a_workspace_while_a_run_works_there()
    {
        var started = Services.GetRequiredService<IRunControl>().Start(As("alice"), _root, "tidy the docs");
        Assert.NotNull(started.Run);
        await _engine.Started;

        var screen = Screen("alice");
        Assert.Equal(CheckChangeWords.RunActive, screen.Find(".checks-not-now").TextContent.Trim());
        AddLint(screen);

        screen.WaitForAssertion(() => Assert.Equal(CheckChangeWords.RunActive, screen.Find(".check-problems").TextContent.Trim()));
        Assert.False(File.Exists(Path.Combine(WorkspaceChecks, "lint.json")));
        Assert.Empty(Audited("check saved"));
    }

    /// <summary>
    /// A run the service does not know of - another program's, or one of its own once composed - holds the workspace's lease
    /// (WorkspaceLease), and its end puts the checks back too: while the lease is held, nothing is changed.
    /// </summary>
    [Fact]
    public void Nothing_is_changed_in_a_workspace_while_its_lease_is_held()
    {
        var screen = Screen("alice");
        Assert.Empty(screen.FindAll(".checks-not-now"));

        using (Enactive.Core.Storage.WorkspaceLease.Take(_root))
        {
            var checks = Services.GetRequiredService<ServerChecks>();
            Assert.Equal(CheckChangeWords.RunActive, checks.Save(As("alice"), _root, "lint",
                new CheckManifestSaid(Command: "npm run lint", Matches: ["src/**"]), CheckScope.Workspace));
            Assert.False(File.Exists(Path.Combine(WorkspaceChecks, "lint.json")));
            Assert.Empty(Audited("check saved"));
        }

        Assert.Null(Services.GetRequiredService<ServerChecks>().WhyNot(As("alice"), _root, CheckScope.Workspace));
    }

    /// <summary>The question before a reset - a workspace manifest that changes the global check - says Reset on its button.</summary>
    [Fact]
    public void A_reset_is_asked_with_reset()
    {
        Directory.CreateDirectory(DataChecks);
        File.WriteAllText(Path.Combine(DataChecks, "audit.json"), """{"id":"audit","command":"npm audit","fireOn":"Run"}""");
        Directory.CreateDirectory(WorkspaceChecks);
        File.WriteAllText(Path.Combine(WorkspaceChecks, "audit.json"), """{"severity":"Blocking"}""");
        var screen = Screen("alice");

        var reset = screen.Find(".check-row .check-remove");
        Assert.Equal("Reset", reset.TextContent.Trim());
        reset.Click();

        Assert.Equal("Reset", screen.Find("[role=dialog] .check-confirm").TextContent.Trim());
        screen.Find("[role=dialog] .check-confirm").Click();
        screen.WaitForAssertion(() => Assert.False(File.Exists(Path.Combine(WorkspaceChecks, "audit.json"))));
        Assert.True(File.Exists(Path.Combine(DataChecks, "audit.json")));
    }

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
