namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Server.Components.Runs;
using Enactive.Server.Components.Shell;
using Enactive.Server.Components.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// A narrow screen's shell (theme.css draws it under 900 px; a phone is checked in tests/server-e2e): the Runs column a
/// drawer opened from the top bar, and the rail a bar along the bottom whose foot is behind one More. The markup is the
/// same at every width; these hold what the stylesheet works with - the button and what it names, the drawer's hold on
/// the page, More and what it holds for each role.
/// </summary>
public sealed class NarrowScreenTests : ComponentHost
{
    private static readonly string[] Main = ["/", "/timeline", "/templates", "/schedules", "/inbox", "/proposals", "/log"];

    /// <summary>A request's antiforgery token: bUnit has no request, and the top bar's sign-out form draws one.</summary>
    private sealed class FixedToken : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-value", "__RequestVerificationToken");
    }

    public NarrowScreenTests()
    {
        Services.AddSingleton<AntiforgeryStateProvider>(new FixedToken());
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    private NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    private DrawerState NewDrawer() => new(Navigation);

    // ── the top bar's button ────────────────────────────────────────────────

    [Fact]
    public void The_top_bar_offers_the_drawer_only_while_a_screen_has_one()
    {
        SignIn("alice");
        using var drawer = NewDrawer();
        var bar = Render<TopBar>(p => p.AddCascadingValue(drawer));
        Assert.Empty(bar.FindAll(".drawer-button"));

        drawer.Offer(true);

        bar.WaitForAssertion(() => Assert.Single(bar.FindAll(".drawer-button")));
        var button = bar.Find(".drawer-button");
        Assert.Equal("Workspace and runs", button.GetAttribute("aria-label"));
        Assert.Equal(DrawerState.Id, button.GetAttribute("aria-controls"));
        Assert.Equal("false", button.GetAttribute("aria-expanded"));

        button.Click();

        Assert.True(drawer.IsOpen);
        bar.WaitForAssertion(() => Assert.Equal("true", bar.Find(".drawer-button").GetAttribute("aria-expanded")));

        drawer.Offer(false);

        Assert.False(drawer.IsOpen);
        bar.WaitForAssertion(() => Assert.Empty(bar.FindAll(".drawer-button")));
    }

    /// <summary>Choosing a workspace or a run in the drawer is a link: it closes once the choice is made.</summary>
    [Fact]
    public void A_navigation_closes_the_drawer()
    {
        using var drawer = NewDrawer();
        drawer.Offer(true);
        drawer.Toggle();
        Assert.True(drawer.IsOpen);

        Navigation.NavigateTo("/runs/0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f");

        Assert.False(drawer.IsOpen);
        Assert.True(drawer.Offered);
    }

    [Fact]
    public void A_drawer_no_screen_offers_does_not_open()
    {
        using var drawer = NewDrawer();

        drawer.Toggle();

        Assert.False(drawer.IsOpen);
    }

    // ── the Runs screen's column in it ──────────────────────────────────────

    [Fact]
    public async Task The_runs_screen_puts_its_column_in_the_drawer_and_offers_it_while_shown()
    {
        SignIn("alice");
        using var drawer = NewDrawer();

        var screen = Render<RunsScreen>(p => p.AddCascadingValue(drawer));

        Assert.True(drawer.Offered);
        screen.WaitForAssertion(() => Assert.NotNull(screen.Find($"#{DrawerState.Id} .runs-column")));
        Assert.Equal("drawer", screen.Find($"#{DrawerState.Id}").GetAttribute("class"));
        Assert.Empty(screen.FindAll(".drawer-scrim"));

        drawer.Toggle();

        screen.WaitForAssertion(() => Assert.Equal("drawer open", screen.Find($"#{DrawerState.Id}").GetAttribute("class")));
        Assert.Single(screen.FindAll(".drawer-scrim"));

        await DisposeComponentsAsync();

        Assert.False(drawer.Offered);
        Assert.False(drawer.IsOpen);
    }

    // ── the drawer's hold on the page ───────────────────────────────────────

    private int _closed;

    private IRenderedComponent<Drawer> RenderDrawer(bool open) => Render<Drawer>(p => p
        .Add(d => d.Id, DrawerState.Id)
        .Add(d => d.Open, open)
        .Add(d => d.OnClose, () => _closed++)
        .AddChildContent("<aside aria-label=\"Runs column\"><button type=\"button\">Refresh</button></aside>"));

    /// <summary>
    /// Opened, it holds the page as a dialog does - the rest inert, the focus in it - with the dialog's own script, and
    /// closed it gives both back. Drawn over the screen but not in charge of it, Tab walked the screen under the scrim.
    /// </summary>
    [Fact]
    public void Opening_the_drawer_holds_the_page_and_takes_the_focus_and_closing_it_gives_them_back()
    {
        var drawer = RenderDrawer(open: false);
        Assert.Empty(DialogScript.Invocations);
        // bUnit writes an element's reference into the markup of the first drawing only.
        var frame = drawer.Find(".drawer-frame").GetAttribute("blazor:elementReference");
        var panel = drawer.Find($"#{DrawerState.Id}").GetAttribute("blazor:elementReference");

        drawer.Render(p => p.Add(d => d.Open, true));

        drawer.WaitForAssertion(() => DialogScript.VerifyInvoke("hold"));
        // The scrim held with the drawer: held alone, the drawer made its own scrim inert, and a tap went through it.
        var held = (ElementReference)DialogScript.Invocations.Single(i => i.Identifier == "hold").Arguments[1]!;
        Assert.Equal(frame, held.Id);
        // And the panel named as where the focus goes when a dialog opened in it closes and what opened that is gone.
        Assert.Equal(panel, ((ElementReference)DialogScript.Invocations.Single(i => i.Identifier == "hold").Arguments[2]!).Id);
        var focused = JSInterop.VerifyFocusAsyncInvoke();
        Assert.Equal(panel, ((ElementReference)focused.Arguments[0]!).Id);
        Assert.Equal("-1", drawer.Find($"#{DrawerState.Id}").GetAttribute("tabindex"));

        drawer.Render(p => p.Add(d => d.Open, false));

        drawer.WaitForAssertion(() => DialogScript.VerifyInvoke("release"));
        Assert.Null(drawer.Find($"#{DrawerState.Id}").GetAttribute("tabindex"));
    }

    /// <summary>
    /// Opened, closed and left within one round trip - a tap on the bottom bar while the drawer was still taking hold: the
    /// hold reached the page, the release queued behind it saw the drawer gone and was never sent, and the page stayed
    /// inert until it was reloaded.
    /// </summary>
    [Fact]
    public async Task A_drawer_left_while_its_hold_is_on_its_way_gives_the_page_back()
    {
        var hold = DialogScript.SetupVoid("hold", _ => true);
        var drawer = RenderDrawer(open: false);
        drawer.Render(p => p.Add(d => d.Open, true));
        drawer.WaitForAssertion(() => DialogScript.VerifyInvoke("hold"));
        drawer.Render(p => p.Add(d => d.Open, false));

        var leaving = DisposeComponentsAsync();
        hold.SetVoidResult();
        await leaving;

        DialogScript.VerifyInvoke("release");
    }

    [Fact]
    public void Escape_and_the_scrim_close_it()
    {
        var drawer = RenderDrawer(open: true);

        drawer.Find($"#{DrawerState.Id}").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal(1, _closed);

        drawer.Find(".drawer-scrim").Click();
        Assert.Equal(2, _closed);
    }

    /// <summary>
    /// A dialog opened in the drawer (the folder browser, a delete's question) keeps its Escape: it went on to close the
    /// drawer under the dialog as well.
    /// </summary>
    [Fact]
    public void An_escape_that_closes_a_dialog_in_the_drawer_leaves_the_drawer_open()
    {
        var dialogClosed = false;
        var drawer = Render<Drawer>(p => p
            .Add(d => d.Id, DrawerState.Id)
            .Add(d => d.Open, true)
            .Add(d => d.OnClose, () => _closed++)
            .AddChildContent<Dialog>(d => d.Add(x => x.Title, "Add workspace").Add(x => x.OnClose, () => dialogClosed = true)));

        drawer.Find("[role=dialog]").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.True(dialogClosed);
        Assert.Equal(0, _closed);
    }

    // ── the rail's More ─────────────────────────────────────────────────────

    private IRenderedComponent<Rail> RenderedRail()
    {
        var rail = Render<Rail>();
        rail.WaitForState(() => rail.FindAll("nav [data-rail]").Count > 0);
        return rail;
    }

    private static string?[] Hrefs(IEnumerable<AngleSharp.Dom.IElement> items) => items.Select(e => e.GetAttribute("data-rail")).ToArray();

    /// <summary>
    /// Eleven icons of a touch's 40 px do not fit across a phone: the bar keeps the main sections in the rail's order, and
    /// the foot of the rail - an Admin's Environment, Settings, Accounts and Audit - is behind one More.
    /// </summary>
    [Fact]
    public void An_admin_finds_the_foot_of_the_rail_behind_more()
    {
        SignIn("ada");

        var rail = RenderedRail();

        Assert.Equal(Main, Hrefs(rail.FindAll("nav > [data-rail]")));
        Assert.Equal(["/environment", "/settings", "/accounts", "/audit"], Hrefs(rail.FindAll("#rail-more-items > [data-rail]")));
        var more = rail.Find(".rail-more-button");
        Assert.Equal("More sections", more.GetAttribute("aria-label"));
        Assert.Equal("rail-more-items", more.GetAttribute("aria-controls"));
        Assert.Equal("false", more.GetAttribute("aria-expanded"));
        // A disclosure of links: aria-haspopup announced a menu, whose keys it does not have.
        Assert.Null(more.GetAttribute("aria-haspopup"));
        // Each of them with its name in words, for More's list.
        Assert.Equal(["Environment", "Settings", "Accounts", "Audit"],
            rail.FindAll("#rail-more-items .rail-text").Select(e => e.TextContent.Trim()));
    }

    /// <summary>A Viewer's More holds Environment alone: the bar the same eight places for everybody.</summary>
    [Theory]
    [InlineData("vera")]
    [InlineData("alice")]
    public void A_viewer_and_an_operator_find_the_environment_behind_more(string who)
    {
        SignIn(who);

        var rail = RenderedRail();

        Assert.Equal(Main, Hrefs(rail.FindAll("nav > [data-rail]")));
        Assert.Equal(["/environment"], Hrefs(rail.FindAll("#rail-more-items > [data-rail]")));
    }

    [Fact]
    public void More_opens_and_escape_a_tap_away_or_a_navigation_closes_it()
    {
        SignIn("ada");
        var rail = RenderedRail();

        rail.Find(".rail-more-button").Click();
        Assert.Equal("true", rail.Find(".rail-more-button").GetAttribute("aria-expanded"));
        Assert.Contains("open", rail.Find(".rail-more").ClassList);

        rail.Find(".rail-more").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal("false", rail.Find(".rail-more-button").GetAttribute("aria-expanded"));

        rail.Find(".rail-more-button").Click();
        rail.Find(".rail-away").Click();
        Assert.Empty(rail.FindAll(".rail-more.open"));

        rail.Find(".rail-more-button").Click();
        Navigation.NavigateTo("/settings");
        rail.WaitForAssertion(() => Assert.Equal("false", rail.Find(".rail-more-button").GetAttribute("aria-expanded")));
    }

    /// <summary>"You are here" on More when the section is one it holds: hidden behind it, the mark said nothing.</summary>
    [Theory]
    [InlineData("http://localhost/settings/ai-providers", true)]
    [InlineData("http://localhost/audit", true)]
    [InlineData("http://localhost/environment", true)]
    [InlineData("http://localhost/inbox", false)]
    [InlineData("http://localhost/", false)]
    public void More_is_marked_while_you_are_in_a_section_it_holds(string address, bool marked)
    {
        SignIn("ada");
        Navigation.NavigateTo(address);

        var rail = RenderedRail();

        Assert.Equal(marked, rail.Find(".rail-more-button").ClassList.Contains("on"));
    }
}
