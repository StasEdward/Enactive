namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Server.Components.Pages;
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
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
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

        Page<Workspaces>("/workspaces", "project");
        Page<HistoryPage>("/history?ws=" + Uri.EscapeDataString(root), "digest the inbox");

        var page = Page<PastRunPage>($"/history/run?ws={Uri.EscapeDataString(root)}&id={run.RunId}", "digest the inbox");
        Assert.Contains("write_file digest.md", page);
        Assert.Contains("digest.md", page);
    }

    /// <summary>
    /// A Viewer reads runs, not the server's disks: a folder the service never ran in is not one whose history it shows,
    /// however the address is typed.
    /// </summary>
    [Fact]
    public void A_folder_the_service_does_not_know_is_not_read()
    {
        Directory.CreateDirectory(Path.Combine(_folders, "elsewhere"));

        Page<HistoryPage>("/history?ws=" + Uri.EscapeDataString(Path.Combine(_folders, "elsewhere")), "does not know that folder");
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
        navigation.NavigateTo("/history?ws=" + Uri.EscapeDataString(root));
        var history = Render<HistoryPage>();
        history.WaitForAssertion(() => Assert.Contains("digest the inbox", history.Markup));

        navigation.NavigateTo("/history?ws=" + Uri.EscapeDataString(Path.Combine(_folders, "elsewhere")));

        history.WaitForAssertion(() => Assert.Contains("does not know that folder", history.Markup));
        Assert.DoesNotContain("digest the inbox", history.Markup);
    }

    /// <summary>A file is shown as text, never as markup, and only from inside the workspace.</summary>
    [Fact]
    public async Task A_file_is_shown_as_text_from_inside_the_workspace_only()
    {
        var (root, _) = await KnownWorkspaceAsync();
        File.WriteAllText(Path.Combine(_folders, "secret.txt"), "the service's own");

        var file = Page<FilePage>($"/file?ws={Uri.EscapeDataString(root)}&path=digest.md", "three mails");
        Assert.Contains("three mails &lt;script&gt;", file);
        Assert.DoesNotContain("<script>alert(1)", file);

        var outside = Page<FilePage>($"/file?ws={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString("..\\secret.txt")}",
            "not in this workspace");
        Assert.DoesNotContain("the service's own", outside);
    }
}
