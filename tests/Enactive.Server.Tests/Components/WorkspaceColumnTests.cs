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
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
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

        // The start form names the workspace the column shows, and the run is refused in the launcher's words.
        screen.Find("textarea").Change("write the digest");
        screen.Find("form.start").Submit();
        screen.WaitForAssertion(() => Assert.Contains("No such folder:", screen.Find(".start .said").TextContent));
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
}
