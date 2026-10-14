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
/// The reading pages (server plan 1.6): the workspaces the service ran in, their runs, and a file of one - for a Viewer,
/// and only of folders the service knows. These were read over HTTP from the page the service sent; the pages are live
/// now and drawn only once their connection is up (App.razor), so the response holds none of it and they are rendered
/// here, against the service's own services, as the person signed in.
/// </summary>
public sealed class ReadingPagesTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-reading", Guid.NewGuid().ToString("N"));

    /// <summary>A request's antiforgery token: bUnit has no request, and the pages' forms draw one.</summary>
    private sealed class FixedToken : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-value", "__RequestVerificationToken");
    }

    public ReadingPagesTests() => Services.AddSingleton<AntiforgeryStateProvider>(new FixedToken());

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Enactive.Tests.SqlitePools.ClearUnder(_folders);
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
        }
    }

    /// <summary>A workspace the service has run in, with one finished run that wrote one file.</summary>
    private async Task<(string Root, RunRecord Run)> KnownWorkspaceAsync()
    {
        var root = Path.Combine(_folders, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "digest.md"), "three mails <script>alert(1)</script>");
        Services.GetRequiredService<ServerEngine>().Workspaces.Touch(root);

        var at = DateTimeOffset.Now;
        var run = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "digest the inbox", "qwen", at, at.AddSeconds(5), "Completed",
            [new RunEventRecord(at, "ToolInvoked", "write_file digest.md")], ["digest.md"], []);
        await RunStoreFactory.Create(WorkspaceInfo.For(root)).SaveAsync(run, CancellationToken.None);
        return (root, run);
    }

    /// <summary>The page at this address, drawn for a Viewer once it has read what it shows.</summary>
    private string Page<TPage>(string address, string shows) where TPage : IComponent
    {
        SignIn("vera");
        Services.GetRequiredService<NavigationManager>().NavigateTo(address);
        var page = Render<TPage>();
        page.WaitForAssertion(() => Assert.Contains(shows, page.Markup));
        return page.Markup;
    }

    [Fact]
    public async Task The_workspaces_the_service_ran_in_are_listed_and_their_runs_read()
    {
        var (root, run) = await KnownWorkspaceAsync();

        // The Runs screen opens on the workspace used last, and names it in the switcher.
        Assert.Contains("project", Page<RunsScreen>("/", "digest the inbox"));
        Page<RunsScreen>($"/?ws={WorkspaceInfo.For(root).Id}", "digest the inbox");

        var past = PastRun(root, run.RunId);
        // Waited for, not read at once: the screen's repository strip draws when git answers, and a click handled while
        // it draws is drawn a moment after.
        past.FindAll(".past-run [role=tab]").Single(t => t.TextContent.Trim() == "Timeline").Click();
        past.WaitForAssertion(() => Assert.Contains("write_file digest.md", past.Find(".past-run .run-view").TextContent));
        past.FindAll(".past-run [role=tab]").Single(t => t.TextContent.Trim() == "Artifacts (1)").Click();
        past.WaitForAssertion(() => Assert.Equal("digest.md", past.Find(".past-run .artifact .path").TextContent.Trim()));
    }

    /// <summary>The Runs screen on a past run (/history/{workspace}/{run}), for a Viewer, once it is drawn.</summary>
    private IRenderedComponent<RunsScreen> PastRun(string root, Guid run)
    {
        SignIn("vera");
        var screen = Render<RunsScreen>(p => p.Add(s => s.WorkspaceId, WorkspaceInfo.For(root).Id).Add(s => s.PastRunId, run));
        screen.WaitForAssertion(() => Assert.Equal("digest the inbox", screen.Find(".past-run .task-title").TextContent.Trim()));
        return screen;
    }

    /// <summary>
    /// What the screen says under the column comes only from the service's own pages, never from its address: "?said=" put
    /// any words a link liked in the service's voice, and they outlived signing in, carried in the return address.
    /// </summary>
    [Fact]
    public async Task An_address_cannot_make_the_runs_screen_say_anything()
    {
        var (root, _) = await KnownWorkspaceAsync();

        var markup = Page<RunsScreen>($"/?ws={WorkspaceInfo.For(root).Id}&said="
            + Uri.EscapeDataString("Your session has ended. Sign in again at evil.example."), "digest the inbox");

        Assert.DoesNotContain("evil.example", markup);
    }

    /// <summary>
    /// A Viewer reads runs, not the server's disks: a folder the service never ran in is not one whose history it shows,
    /// however the address is typed.
    /// </summary>
    [Fact]
    public void A_folder_the_service_does_not_know_is_not_read()
    {
        Directory.CreateDirectory(Path.Combine(_folders, "elsewhere"));

        var elsewhere = Path.Combine(_folders, "elsewhere");

        Page<RunsScreen>($"/?ws={WorkspaceInfo.For(elsewhere).Id}", "does not know that workspace");
    }

    /// <summary>
    /// A live page kept on screen while only its query changes is the same page given new parameters, not a new one: a
    /// page that read its address once went on showing the old one (browsing into a folder still listed the roots).
    /// </summary>
    [Fact]
    public async Task A_page_shows_its_new_address_when_only_the_query_changes()
    {
        var (root, _) = await KnownWorkspaceAsync();
        SignIn("vera");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/?ws={WorkspaceInfo.For(root).Id}");
        var history = Render<RunsScreen>();
        history.WaitForAssertion(() => Assert.Contains("digest the inbox", history.Markup));

        navigation.NavigateTo($"/?ws={WorkspaceInfo.For(Path.Combine(_folders, "elsewhere")).Id}");

        history.WaitForAssertion(() => Assert.Contains("does not know that workspace", history.Markup));
        Assert.DoesNotContain("digest the inbox", history.Markup);
    }

    /// <summary>
    /// A file is reviewed as text, never as markup, and only from inside the workspace - whatever path a run record names:
    /// the service reads records for people who may not open that folder themselves.
    /// </summary>
    [Fact]
    public async Task A_file_is_shown_as_text_from_inside_the_workspace_only()
    {
        var (root, _) = await KnownWorkspaceAsync();
        File.WriteAllText(Path.Combine(_folders, "secret.txt"), "the service's own");
        var at = DateTimeOffset.Now;
        var run = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "digest the inbox", "qwen", at, at.AddSeconds(5), "Completed",
            [], ["digest.md", @"..\secret.txt"], []);
        await RunStoreFactory.Create(WorkspaceInfo.For(root)).SaveAsync(run, CancellationToken.None);
        var past = PastRun(root, run.RunId);
        past.FindAll(".past-run [role=tab]").Single(t => t.TextContent.Trim() == "Artifacts (2)").Click();
        past.WaitForAssertion(() => Assert.Equal(2, past.FindAll(".past-run .artifact").Count));

        Review(past, "digest.md");
        past.WaitForAssertion(() => Assert.Equal("three mails <script>alert(1)</script>", past.Find("[role=dialog] pre").TextContent));
        Assert.DoesNotContain("<script>alert(1)", past.Find("[role=dialog]").InnerHtml);
        past.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Close").Click();

        Review(past, @"..\secret.txt");
        past.WaitForAssertion(() => Assert.Contains("not in this workspace", past.Find("[role=dialog] .said").TextContent));
        Assert.DoesNotContain("the service's own", past.Markup);
    }

    private static void Review(IRenderedComponent<RunsScreen> past, string path)
        => past.FindAll(".past-run .artifact").Single(a => a.QuerySelector(".path")!.TextContent.Trim() == path)
            .QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Review").Click();
}
