namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Server.Accounts;
using Enactive.Server.Components.Shell;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The shell the desktop window has - a dark top bar, a 52 px icon rail - on the web: who sees which section, what the
/// inbox icon says is unread, and that the top bar names the person signed in with a way out that posts safely.
/// </summary>
public sealed class ShellTests : ComponentHost
{
    private static readonly string[] ForEveryone = ["Runs", "Timeline · project memory", "Templates · a saved task",
        "Schedules · run it on a day and a time", "Inbox", "Proposals", "Global log", "Environment"];

    private static readonly string[] ForAdmins = ["Settings", "Accounts", "Audit"];

    /// <summary>A request's antiforgery token: bUnit has no request, so the form would be drawn without the field a post needs.</summary>
    private sealed class FixedToken : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-value", "__RequestVerificationToken");
    }

    public ShellTests()
    {
        Services.AddSingleton<AntiforgeryStateProvider>(new FixedToken());
        // The shell is drawn live in the service (App.razor), and the top bar says the connection only there.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    /// <summary>The names the rail's items are announced by - the tooltip, which is also the icon's only text.</summary>
    private static string[] Names(IRenderedComponent<Rail> rail)
        => rail.FindAll("nav [data-rail]").Select(e => e.GetAttribute("aria-label")!).ToArray();

    /// <summary>The rail reads the inbox and the staged proposals on a thread of its own; wait for it to have drawn.</summary>
    private IRenderedComponent<Rail> RenderedRail()
    {
        var rail = Render<Rail>();
        rail.WaitForState(() => rail.FindAll("nav [data-rail]").Count > 0);
        return rail;
    }

    [Fact]
    public void A_viewer_is_shown_no_admin_section()
    {
        Assert.DoesNotContain(RailItem.For(Role.Viewer), i => i.Needs == Role.Admin);
        Assert.DoesNotContain(RailItem.For(Role.Operator), i => i.Needs == Role.Admin);

        SignIn("vera");
        var names = Names(RenderedRail());

        Assert.DoesNotContain(names, n => n.StartsWith("Settings") || n.StartsWith("Accounts") || n.StartsWith("Audit"));
        Assert.Equal(ForEveryone.Length, names.Length);
    }

    [Fact]
    public void An_admin_is_shown_every_section()
    {
        var items = RailItem.For(Role.Admin);
        Assert.Equal(
            ["/", "/timeline", "/templates", "/schedules", "/inbox", "/proposals", "/log", "/environment", "/settings", "/accounts", "/audit"],
            items.Select(i => i.Href));
        Assert.Equal(ForAdmins, items.Where(i => i.Needs == Role.Admin).Select(i => i.Label));

        SignIn("ada");
        var names = Names(RenderedRail());

        Assert.Equal(ForEveryone.Concat(ForAdmins), names);
    }

    [Fact]
    public async Task The_inbox_icon_counts_what_is_unread()
    {
        var folder = Path.Combine(Path.GetTempPath(), "enactive-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Services.GetRequiredService<ServerEngine>().Workspaces.Touch(folder);
            var workspace = WorkspaceInfo.For(folder);
            var inbox = InboxStoreFactory.Create(workspace);
            foreach (var status in new[] { "unread", "unread", "read" })
                await inbox.AppendAsync(new InboxItem(Guid.NewGuid(), workspace.Id, "result", "digest", "done", Guid.Empty, status,
                    DateTimeOffset.UtcNow), CancellationToken.None);
            SignIn("alice");

            var rail = RenderedRail();

            rail.WaitForAssertion(() => Assert.Equal("2", rail.Find("[data-rail='/inbox'] .badge").TextContent.Trim()));
            Assert.Equal("Inbox — 2 unread", rail.Find("[data-rail='/inbox']").GetAttribute("aria-label"));
        }
        finally
        {
            Enactive.Tests.SqlitePools.ClearUnder(folder);
            try { Directory.Delete(folder, recursive: true); } catch { /* a temp folder */ }
        }
    }

    [Fact]
    public void Nothing_unread_shows_no_count_and_nothing_proposed_shows_no_dot()
    {
        SignIn("alice");

        var rail = RenderedRail();

        Assert.Empty(rail.FindAll(".badge"));
        Assert.Empty(rail.FindAll(".dot"));
    }

    [Fact]
    public void The_proposals_icon_has_a_dot_while_a_run_has_proposals_kept()
    {
        var folder = Path.Combine(Path.GetTempPath(), "enactive-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Services.GetRequiredService<ServerEngine>().Workspaces.Touch(folder);
            Services.GetRequiredService<ServerStaging>().Begin(folder, Guid.NewGuid(), "alice", "tidy the notes");
            SignIn("alice");

            var rail = RenderedRail();

            Assert.Single(rail.FindAll("[data-rail='/proposals'] .dot"));
        }
        finally
        {
            Enactive.Tests.SqlitePools.ClearUnder(folder);
            try { Directory.Delete(folder, recursive: true); } catch { /* a temp folder */ }
        }
    }

    /// <summary>
    /// The dot goes when the last waiting proposal is decided - on a page that does not navigate, and by anybody: it was read
    /// only at a navigation, and kept saying "waiting" over a set that was applied a moment ago.
    /// </summary>
    [Fact]
    public async Task The_proposals_dot_goes_when_the_last_waiting_change_is_decided()
    {
        var folder = Path.Combine(Path.GetTempPath(), "enactive-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Services.GetRequiredService<ServerEngine>().Workspaces.Touch(folder);
            var staging = Services.GetRequiredService<ServerStaging>();
            var run = Guid.NewGuid();
            var kept = staging.Begin(folder, run, "alice", "tidy the notes");
            using (var store = new StagingArtifactStore(folder, kept))
            {
                await store.CreateAsync("notes.md", Enactive.Core.Artifacts.ArtifactKind.FileSet, "notes",
                    s => s.WriteAsync("new\n"u8.ToArray()).AsTask(), CancellationToken.None);
                await staging.EndAsync(folder, kept, "tidy the notes", run, store);
            }
            SignIn("alice");
            var rail = RenderedRail();
            Assert.Single(rail.FindAll("[data-rail='/proposals'] .dot"));

            var alice = ServerAuth.PrincipalOf(Users.Find("alice")!);
            var change = staging.Open(alice, folder, run.ToString("N")).Files.Single().Id;
            await staging.ActAsync(alice, folder, run.ToString("N"), change, apply: true, CancellationToken.None);

            rail.WaitForAssertion(() => Assert.Empty(rail.FindAll("[data-rail='/proposals'] .dot")));
        }
        finally
        {
            Enactive.Tests.SqlitePools.ClearUnder(folder);
            try { Directory.Delete(folder, recursive: true); } catch { /* a temp folder */ }
        }
    }

    /// <summary>Every section the rail shows is a link: none is drawn as a section to come any more.</summary>
    [Fact]
    public void Every_section_is_a_link()
    {
        SignIn("ada");

        var rail = RenderedRail();

        Assert.All(rail.FindAll("nav [data-rail]"), item =>
        {
            Assert.Equal("A", item.TagName);
            Assert.False(string.IsNullOrEmpty(item.GetAttribute("href")));
        });
    }

    /// <summary>
    /// The log, the Timeline and the Environment lead to their own pages. The log led to the old page at /logs, and the
    /// Timeline and the Environment were shown disabled while they had none.
    /// </summary>
    [Fact]
    public void Log_timeline_and_environment_lead_to_their_own_pages()
    {
        SignIn("vera");

        var rail = RenderedRail();

        foreach (var (href, now) in new[] { ("/log", "log"), ("/timeline", "timeline"), ("/environment", "environment") })
        {
            var item = rail.Find($"[data-rail='{href}']");
            Assert.Equal(now, item.GetAttribute("href"));
            Assert.Null(item.GetAttribute("aria-disabled"));
        }
    }

    /// <summary>
    /// Templates and Schedules lead to their own pages. Schedules led to the Runs screen while it had none, and marked
    /// itself on the old schedules page's address, which is a redirect now.
    /// </summary>
    [Fact]
    public void Templates_and_schedules_lead_to_their_own_pages()
    {
        SignIn("alice");

        var rail = RenderedRail();

        Assert.Equal("templates", rail.Find("[data-rail='/templates']").GetAttribute("href"));
        Assert.Equal("schedules", rail.Find("[data-rail='/schedules']").GetAttribute("href"));
    }

    /// <summary>Settings lead to their own page. They led to the old page at /admin/settings, which is a redirect now.</summary>
    [Fact]
    public void Settings_lead_to_their_own_page()
    {
        SignIn("ada");

        var rail = RenderedRail();

        Assert.Equal("settings", rail.Find("[data-rail='/settings']").GetAttribute("href"));
    }

    /// <summary>
    /// Accounts and Audit lead to their own pages. They led to the old pages at /admin/users and /admin/audit, which are
    /// redirects now - the last items that led anywhere but their own address.
    /// </summary>
    [Fact]
    public void Accounts_and_audit_lead_to_their_own_pages()
    {
        SignIn("ada");

        var rail = RenderedRail();

        Assert.Equal("accounts", rail.Find("[data-rail='/accounts']").GetAttribute("href"));
        Assert.Equal("audit", rail.Find("[data-rail='/audit']").GetAttribute("href"));
        // Every item leads to its own address: the base-relative form of the address it is meant to have.
        Assert.All(rail.FindAll("nav [data-rail]"),
            item => Assert.Equal(item.GetAttribute("data-rail") == "/" ? "/" : item.GetAttribute("data-rail")!.TrimStart('/'), item.GetAttribute("href")));
    }

    [Theory]
    [InlineData("http://localhost/", "/")]
    [InlineData("http://localhost/runs/0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f", "/")]
    // A past run is the Runs screen too, at its own address: no section was marked on it.
    [InlineData("http://localhost/history/0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f/0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f", "/")]
    [InlineData("http://localhost/inbox", "/inbox")]
    [InlineData("http://localhost/proposals/0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f/abc", "/proposals")]
    [InlineData("http://localhost/templates?ws=0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f", "/templates")]
    [InlineData("http://localhost/templates/digest?ws=0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f", "/templates")]
    [InlineData("http://localhost/schedules?ws=0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f", "/schedules")]
    [InlineData("http://localhost/schedules/0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f?ws=0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f", "/schedules")]
    [InlineData("http://localhost/log", "/log")]
    [InlineData("http://localhost/timeline?ws=0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f", "/timeline")]
    [InlineData("http://localhost/environment", "/environment")]
    [InlineData("http://localhost/settings", "/settings")]
    [InlineData("http://localhost/settings/ai-providers", "/settings")]
    [InlineData("http://localhost/accounts", "/accounts")]
    [InlineData("http://localhost/audit", "/audit")]
    public void The_section_you_are_in_is_marked(string address, string marked)
    {
        SignIn("ada");
        Services.GetRequiredService<NavigationManager>().NavigateTo(address);

        var rail = RenderedRail();

        Assert.Equal(marked, Assert.Single(rail.FindAll("[data-rail][aria-current='page']")).GetAttribute("data-rail"));
    }

    /// <summary>
    /// A page that throws is shown as failed, with Reload, inside the shell - the rail and the top bar still work around
    /// it. With nothing to catch it, the exception ended the page's connection and left it as it was, still saying
    /// "Connected", every button dead.
    /// </summary>
    [Fact]
    public void A_page_that_throws_says_it_stopped_working_and_offers_reload()
    {
        SignIn("vera");

        var layout = Render<Enactive.Server.Components.Layout.MainLayout>(p => p.Add(l => l.Body, body =>
        {
            body.OpenComponent<Throws>(0);
            body.CloseComponent();
        }));

        var failed = layout.Find("main .page-failed[role=alert]");
        Assert.Equal("This page stopped working", failed.QuerySelector(".label")!.TextContent.Trim());
        Assert.Equal("location.reload()", failed.QuerySelector("button")!.GetAttribute("onclick"));
        Assert.NotEmpty(layout.FindAll("nav [data-rail]"));
    }

    /// <summary>
    /// Moving to another page clears the failure: the layout is given another body, and the boundary would otherwise go on
    /// showing the last page's error over it, so no section could be opened again without a reload.
    /// </summary>
    [Fact]
    public void Moving_to_another_page_clears_the_failure()
    {
        SignIn("vera");
        var layout = Render<Enactive.Server.Components.Layout.MainLayout>(p => p.Add(l => l.Body, body =>
        {
            body.OpenComponent<Throws>(0);
            body.CloseComponent();
        }));
        Assert.NotEmpty(layout.FindAll("main .page-failed"));

        layout.Render(p => p.Add(l => l.Body, body =>
        {
            body.OpenComponent<Draws>(0);
            body.CloseComponent();
        }));

        layout.WaitForAssertion(() => Assert.Equal("another page", layout.Find("main .another-page").TextContent));
        Assert.Empty(layout.FindAll(".page-failed"));
    }

    /// <summary>A page that draws as it should.</summary>
    private sealed class Draws : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "class", "another-page");
            builder.AddContent(2, "another page");
            builder.CloseElement();
        }
    }

    /// <summary>A page that fails as it is drawn.</summary>
    private sealed class Throws : ComponentBase
    {
        protected override void OnInitialized() => throw new InvalidOperationException("a page's own fault");
    }

    [Fact]
    public void The_top_bar_names_who_is_signed_in_and_their_role()
    {
        SignIn("alice");

        var bar = Render<TopBar>();

        // Each said for what it is - a name and a role that match read as one word twice.
        Assert.Equal("User: alice", bar.Find(".who").TextContent.Trim());
        Assert.Equal("alice", bar.Find(".who-name").TextContent.Trim());
        Assert.Equal("Role: Operator", bar.Find(".who-role").TextContent.Trim());
        Assert.Equal("Operator", bar.Find(".pill").TextContent.Trim());
        Assert.Equal("localhost", bar.Find(".host").TextContent.Trim());
        var signOut = bar.Find("form.signout");
        Assert.Equal("post", signOut.GetAttribute("method"));
        Assert.Equal("/signout", signOut.GetAttribute("action"));
        Assert.NotNull(signOut.QuerySelector("input[name='__RequestVerificationToken']"));
        Assert.Equal("Sign out", signOut.QuerySelector("button")!.TextContent.Trim());
    }

    /// <summary>
    /// The colour theme is chosen in the top bar, signed in or not, by a plain button that wwwroot/theme.js answers: the
    /// choice is the browser's, so the button posts nothing and asks the service for nothing - it is not a submit (in a
    /// form it would sign out) and has no handler of the page's.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("alice")]
    public void The_top_bar_offers_the_colour_theme(string? who)
    {
        if (who is not null)
            SignIn(who);

        var bar = Render<TopBar>();

        var toggle = bar.Find("button[data-theme-toggle]");
        Assert.Equal("button", toggle.GetAttribute("type"));
        Assert.False(string.IsNullOrEmpty(toggle.GetAttribute("aria-label")));
        Assert.Null(toggle.Closest("form"));
        Assert.Equal(3, toggle.QuerySelectorAll("svg[aria-hidden='true']").Length);
    }

    [Fact]
    public void The_top_bar_with_nobody_signed_in_has_the_mark_and_no_way_out()
    {
        var bar = Render<TopBar>();

        Assert.Contains("Enactive", bar.Markup);
        Assert.Empty(bar.FindAll("form.signout"));
        Assert.Empty(bar.FindAll(".who"));
    }

    /// <summary>
    /// Signing in sets a cookie, and only a real response can: a live sign-in page had no response to set it on. The
    /// attribute keeps the page out of the app's interactive routing (App.razor), so it is rendered and posted statically.
    /// </summary>
    [Fact]
    public void Sign_in_is_not_an_interactive_page()
        => Assert.Single(typeof(Enactive.Server.Components.Pages.SignIn)
            .GetCustomAttributes(typeof(ExcludeFromInteractiveRoutingAttribute), inherit: true));

    /// <summary>
    /// The render mode is the app's, set once in App.razor; a page that set its own as well was one more place for a
    /// page to be drawn ahead of its connection.
    /// </summary>
    [Fact]
    public void No_page_sets_a_render_mode_of_its_own()
    {
        var pages = typeof(ServerApp).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Length > 0).ToList();

        Assert.NotEmpty(pages);
        Assert.Empty(pages.Where(t => t.GetCustomAttributes(typeof(RenderModeAttribute), inherit: true).Length > 0)
            .Select(t => t.Name));
    }

    /// <summary>
    /// The connection said in the top bar, on the element Blazor itself marks while the connection is down
    /// (components-reconnect-modal): the page cannot draw anything then, so each state's text is there already and the
    /// stylesheet shows the one the mark calls for.
    /// </summary>
    [Fact]
    public void The_top_bar_says_the_connection_on_the_element_blazor_marks()
    {
        SignIn("alice");

        var bar = Render<TopBar>();

        var state = bar.Find(".top #components-reconnect-modal");
        Assert.Equal(["Connected", "Reconnecting…", "Disconnected"],
            state.QuerySelectorAll(".conn-text").Select(e => e.TextContent.Trim()));
        Assert.Single(state.QuerySelectorAll(".conn-dot"));
        Assert.Single(state.QuerySelectorAll(".conn-block"));
        Assert.Equal("Reload", state.QuerySelector("button.conn-reload")!.TextContent.Trim());
    }

    /// <summary>A page drawn statically (sign-in) has no connection; the top bar there said "Connected" all the same.</summary>
    [Fact]
    public void The_top_bar_of_a_static_page_says_no_connection()
    {
        SetRendererInfo(new RendererInfo("Static", isInteractive: false));

        var bar = Render<TopBar>();

        Assert.Empty(bar.FindAll("#components-reconnect-modal"));
        Assert.DoesNotContain("Connected", bar.Markup);
    }
}
