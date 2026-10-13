namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Server.Components.Runs;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The Runs screen's left column, as the window's (MainWindow.axaml, the context column): the workspace and its switcher,
/// what runs now, the filter with "Delete these N", the unfinished runs, and the history - and what each role is offered.
/// </summary>
public sealed class WorkspaceColumnTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-column", Guid.NewGuid().ToString("N"));

    /// <summary>A request's antiforgery token: bUnit has no request, and the start form draws one.</summary>
    private sealed class FixedToken : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-value", "__RequestVerificationToken");
    }

    public WorkspaceColumnTests() => Services.AddSingleton<AntiforgeryStateProvider>(new FixedToken());

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Enactive.Tests.SqlitePools.ClearUnder(_folders);
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
        }
    }

    /// <summary>A folder the service knows as a workspace.</summary>
    private string Known(string name)
    {
        var root = Path.Combine(_folders, name);
        Directory.CreateDirectory(root);
        Services.GetRequiredService<ServerEngine>().Workspaces.Touch(root);
        return root;
    }

    private static Guid IdOf(string root) => WorkspaceInfo.For(root).Id;

    private static async Task<RunRecord> RecordedAsync(string root, string task, DateTimeOffset at, string status = "Completed")
    {
        var run = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), task, "qwen", at, at.AddSeconds(5), status, [], [], []);
        await RunStoreFactory.Create(WorkspaceInfo.For(root)).SaveAsync(run, CancellationToken.None);
        return run;
    }

    private static async Task<RunCheckpoint> InterruptedAsync(string root, string title)
    {
        var first = Guid.NewGuid();
        var checkpoint = new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now.AddMinutes(-10), DateTimeOffset.Now,
            title, title, null, Spec: null,
            Steps:
            [
                new CheckpointStep(first, "first", [], "Normal", "Done", "Succeeded"),
                new CheckpointStep(Guid.NewGuid(), "second", [first], "Normal", "Pending")
            ],
            Digest: [], Transcript: [], Artifacts: [], StepsRun: 1, TokensSpent: 0);
        await new JsonCheckpointStore(WorkspaceInfo.For(root)).SaveAsync(checkpoint, CancellationToken.None);
        return checkpoint;
    }

    /// <summary>The Runs screen at this address, for this person, once it has read what it shows.</summary>
    private IRenderedComponent<RunsScreen> Screen(string who, string address, string shows)
    {
        SignIn(who);
        Services.GetRequiredService<NavigationManager>().NavigateTo(address);
        var screen = Render<RunsScreen>();
        screen.WaitForAssertion(() => Assert.Contains(shows, screen.Markup));
        return screen;
    }

    private static IEnumerable<string> Offered(IRenderedComponent<RunsScreen> screen)
        => screen.FindAll("button").Select(b => (b.GetAttribute("aria-label") ?? b.TextContent).Trim());

    [Fact]
    public void The_switcher_lists_every_known_workspace_with_its_edge()
    {
        var alpha = Known("alpha");
        var beta = Known("beta");
        var gone = Known("gone");
        Directory.Delete(gone);

        var screen = Screen("vera", $"/?ws={IdOf(alpha)}", "alpha");
        // The card says what it is, not only a folder's name and path.
        Assert.Equal("Workspace: alpha", screen.Find("button.ws-card").GetAttribute("aria-label"));
        screen.Find("button.ws-card").Click();

        var rows = screen.FindAll(".switcher .ws-row");
        Assert.Equal(3, rows.Count);
        var current = Assert.Single(rows, r => r.TextContent.Contains("alpha"));
        var other = Assert.Single(rows, r => r.TextContent.Contains("beta"));
        var missing = Assert.Single(rows, r => r.TextContent.Contains("gone"));
        Assert.Contains("edge-current", current.ClassList);
        Assert.Contains("edge-other", other.ClassList);
        Assert.Contains("edge-missing", missing.ClassList);
        Assert.Contains("Folder not found · ", missing.TextContent);
        // A workspace is named in an address by its id, never its path.
        Assert.Equal($"/?ws={IdOf(beta)}", other.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public async Task A_viewer_is_offered_no_add_folder_delete_or_carry_on()
    {
        var root = Known("project");
        await RecordedAsync(root, "digest the inbox", DateTimeOffset.Now.AddHours(-1));
        await InterruptedAsync(root, "tidy the notes");

        // What an Operator is offered on the same screen - so the Viewer's absence of it is the role, not an empty page.
        var operating = Screen("alice", $"/?ws={IdOf(root)}", "tidy the notes");
        operating.Find("button.ws-card").Click();
        Assert.Contains("Add folder…", Offered(operating));
        Assert.Contains("Delete these 1", Offered(operating));
        Assert.Contains(Offered(operating), o => o.StartsWith("Resume", StringComparison.Ordinal));
        Assert.Contains("Delete: it can no longer be resumed", Offered(operating));
        Assert.Contains("Delete this run from the history", Offered(operating));

        var viewing = Screen("vera", $"/?ws={IdOf(root)}", "tidy the notes");
        Assert.Contains("digest the inbox", viewing.Markup);
        viewing.Find("button.ws-card").Click();
        var offered = Offered(viewing).ToList();
        Assert.DoesNotContain("Add folder…", offered);
        Assert.DoesNotContain(offered, o => o.StartsWith("Delete", StringComparison.Ordinal));
        Assert.DoesNotContain(offered, o => o.StartsWith("Resume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_delete_button_says_how_many_it_takes()
    {
        var root = Known("project");
        var old = await RecordedAsync(root, "last week's digest", DateTimeOffset.Now.AddDays(-10));
        await RecordedAsync(root, "today's digest", DateTimeOffset.Now.AddHours(-2));
        await RecordedAsync(root, "the report", DateTimeOffset.Now.AddHours(-1), "Failed");

        var screen = Screen("alice", $"/?ws={IdOf(root)}", "the report");
        Assert.Equal("Delete these 3", screen.Find("button.delete-shown").TextContent.Trim());

        screen.Find("select.filter").Change(nameof(RunFilter.OlderThanAWeek));
        screen.WaitForAssertion(() => Assert.Equal("Delete these 1", screen.Find("button.delete-shown").TextContent.Trim()));
        Assert.DoesNotContain("today's digest", screen.Find(".history").TextContent);

        // Asked first, in the window's words, with the count (RunHistoryService.DeleteMany).
        screen.Find("button.delete-shown").Click();
        Assert.Equal("Delete 1 run?", screen.Find("[role=dialog] h2").TextContent.Trim());
        screen.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Delete 1").Click();

        screen.WaitForAssertion(() => Assert.Equal("Delete these 0", screen.Find("button.delete-shown").TextContent.Trim()));
        Assert.True(screen.Find("button.delete-shown").HasAttribute("disabled"));
        var left = await RunStoreFactory.Create(WorkspaceInfo.For(root)).LoadSummariesAsync(CancellationToken.None);
        Assert.Equal(2, left.Count);
        Assert.DoesNotContain(left, r => r.RunId == old.RunId);
    }

    [Fact]
    public void A_missing_workspace_is_red_and_its_run_is_refused()
    {
        var gone = Known("gone");
        Directory.Delete(gone);

        var screen = Screen("alice", $"/?ws={IdOf(gone)}", "gone");

        Assert.Contains("edge-missing", screen.Find("button.ws-card").ClassList);
        Assert.Contains("This folder is not on disk. Nothing will run here until you pick another one.", screen.Markup);
        // Shown, not brought back: reading a history that is not there made its store's folders - and so the workspace's.
        Assert.False(Directory.Exists(gone));

        // The command bar runs in the workspace the column shows, and the run is refused in the launcher's words.
        screen.Find(".command-bar textarea").Input("write the digest");
        screen.FindAll(".command-bar button").Single(b => b.TextContent.Trim() == "Run").Click();
        screen.WaitForAssertion(() => Assert.Contains("No such folder: " + gone, screen.Find(".command-bar .said").TextContent));
        Assert.Empty(Services.GetRequiredService<IRunControl>().List(
            Enactive.Server.Accounts.ServerAuth.PrincipalOf(Users.Find("alice")!)));
    }

    /// <summary>
    /// An address naming a workspace the service does not know shows none - not the history of another one, and not a
    /// folder read off the disk because an id was typed.
    /// </summary>
    [Fact]
    public async Task A_workspace_the_service_does_not_know_is_said_not_shown()
    {
        var root = Known("project");
        await RecordedAsync(root, "digest the inbox", DateTimeOffset.Now.AddHours(-1));

        var screen = Screen("vera", $"/?ws={Guid.NewGuid()}", "does not know that workspace");

        Assert.DoesNotContain("digest the inbox", screen.Markup);
    }

    /// <summary>
    /// A workspace's templates and schedules are the rail's, each a screen with the workspace switcher of its own: the Runs
    /// screen leads to neither. Its link to the schedules, kept while they had no screen, led to the old page by the
    /// folder's path.
    /// </summary>
    [Fact]
    public void The_runs_screen_leaves_templates_and_schedules_to_the_rail()
    {
        Known("alpha");
        var beta = Known("beta");

        var screen = Screen("vera", $"/?ws={IdOf(beta)}", "beta");

        Assert.DoesNotContain(screen.FindAll("a"), a => a.TextContent.Contains("Schedules", StringComparison.Ordinal));
        Assert.DoesNotContain(screen.FindAll("a"), a => a.TextContent.Contains("Templates", StringComparison.Ordinal));
        Assert.DoesNotContain(screen.FindAll("a"), a => (a.GetAttribute("href") ?? "").StartsWith("library", StringComparison.Ordinal));
    }

    /// <summary>
    /// A filter of rows folded by task: one row for a task tried twice, saying so ("×2"), and the count on the delete
    /// button is every run the filter takes, the folded attempt included - what the window's DeleteShownLabel counts.
    /// </summary>
    [Fact]
    public async Task Attempts_at_one_task_are_one_row_that_says_how_many()
    {
        var root = Known("project");
        var first = await RecordedAsync(root, "the report", DateTimeOffset.Now.AddHours(-3), "Failed");
        var again = first with { RunId = Guid.NewGuid(), StartedAt = DateTimeOffset.Now.AddHours(-1), Status = "Completed" };
        await RunStoreFactory.Create(WorkspaceInfo.For(root)).SaveAsync(again, CancellationToken.None);

        var screen = Screen("vera", $"/?ws={IdOf(root)}", "the report");

        Assert.Single(screen.FindAll(".history > li"));
        Assert.Equal("×2", screen.Find(".history .attempts").TextContent.Trim());
        screen.Find(".history .attempts").Click();
        Assert.Equal(2, screen.FindAll(".history > li").Count);
    }

    /// <summary>
    /// A run in flight says when it started as the window writes a time, "HH:mm", in every culture: the service's own
    /// culture wrote it, and fi-FI wrote "09.05".
    /// </summary>
    [Fact]
    public void A_running_row_says_when_it_started_in_every_culture()
    {
        var root = Known("project");
        var run = new ServerRun("alice", root, "write the digest", new Enactive.Application.RunQueue(1).Enqueue(root));
        var was = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fi-FI");
            Assert.EndsWith(" · started " + run.StartedAt.ToString("HH':'mm", System.Globalization.CultureInfo.InvariantCulture),
                WorkspaceColumnState.Row(run).Tooltip);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = was;
        }
    }

    /// <summary>
    /// The switcher's rows carry the window's Rename and Forget for an Operator, in the window's words: a name of the
    /// service's own, and the list forgetting a workspace - its name and how runs are set up in it - while the folder and
    /// its run history stay where they are. Each is done by the service, audited. They were not offered at all.
    /// </summary>
    [Fact]
    public async Task An_operator_renames_and_forgets_a_workspace_and_nothing_on_disk_goes()
    {
        var alpha = Known("alpha");
        var beta = Known("beta");
        await RecordedAsync(beta, "digest the inbox", DateTimeOffset.Now.AddHours(-1));
        var registry = Services.GetRequiredService<ServerEngine>().Workspaces;

        var screen = Screen("alice", $"/?ws={IdOf(alpha)}", "alpha");
        screen.Find("button.ws-card").Click();
        var rename = screen.FindAll(".switcher button.ws-rename").Single(b => b.GetAttribute("aria-label") == "Rename beta");
        Assert.Equal("Rename. What Enactive calls it — the folder is not touched.", rename.GetAttribute("title"));
        rename.Click();
        var dialog = screen.Find("[role=dialog]");
        Assert.Equal("Name this workspace", dialog.QuerySelector(".dialog-title")!.TextContent.Trim());
        Assert.Equal("beta", screen.Find("input.ws-rename-name").GetAttribute("value"));
        screen.Find("input.ws-rename-name").Input("Beta docs");
        screen.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "OK").Click();

        screen.WaitForAssertion(() => Assert.Equal("Beta docs", registry.Find(beta)!.Name));
        Assert.Contains("is called “Beta docs” now", screen.Find(".said").TextContent);
        Assert.Contains(Users.AuditTrail(), e => e is { Account: "alice", Action: "workspace renamed" });

        screen.Find("button.ws-card").Click();
        var forget = screen.FindAll(".switcher button.ws-forget").Single(b => b.GetAttribute("aria-label") == "Forget Beta docs");
        Assert.Equal("Forget this workspace. The folder and its history are left where they are.", forget.GetAttribute("title"));
        forget.Click();
        Assert.Equal("Forget “Beta docs”?", screen.Find("[role=dialog] .dialog-title").TextContent.Trim());
        Assert.StartsWith("The folder and everything in it, including its run history, are left exactly where they are.",
            screen.Find("[role=dialog] .body").TextContent.Trim());
        screen.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Forget").Click();

        screen.WaitForAssertion(() => Assert.Null(registry.Find(beta)));
        Assert.Contains(Users.AuditTrail(), e => e is { Account: "alice", Action: "workspace forgotten" });
        // Nothing on disk: the folder, and the history in it, are where they were.
        Assert.True(Directory.Exists(beta));
        Assert.Single(await RunStoreFactory.Create(WorkspaceInfo.For(beta)).LoadSummariesAsync(CancellationToken.None));
        screen.Find("button.ws-card").Click();
        Assert.DoesNotContain(screen.FindAll(".switcher .ws-row"), r => r.TextContent.Contains("Beta docs"));
    }

    /// <summary>A Viewer is offered neither, and the service refuses both to anybody below an Operator, whatever page asks.</summary>
    [Fact]
    public void A_viewer_can_neither_rename_nor_forget_a_workspace()
    {
        var alpha = Known("alpha");
        var screen = Screen("vera", $"/?ws={IdOf(alpha)}", "alpha");
        screen.Find("button.ws-card").Click();
        Assert.Empty(screen.FindAll(".switcher .ws-rename, .switcher .ws-forget"));

        var history = Services.GetRequiredService<ServerHistory>();
        var vera = Enactive.Server.Accounts.ServerAuth.PrincipalOf(Users.Find("vera")!);
        Assert.Contains("role does not include", history.RenameWorkspace(vera, alpha, "mine"));
        Assert.Contains("role does not include", history.ForgetWorkspace(vera, alpha));
        var registry = Services.GetRequiredService<ServerEngine>().Workspaces;
        Assert.Equal("alpha", registry.Find(alpha)!.Name);

        // Nor is a folder the service does not know renamed into its list.
        var alice = Enactive.Server.Accounts.ServerAuth.PrincipalOf(Users.Find("alice")!);
        var stranger = Path.Combine(_folders, "stranger");
        Directory.CreateDirectory(stranger);
        Assert.Contains("does not know that folder", history.RenameWorkspace(alice, stranger, "mine"));
        Assert.Null(registry.Find(stranger));
    }
}
