namespace Enactive.Server.Tests.Components;

using Enactive.Server.Accounts;
using Enactive.Server.Components.Runs;
using Enactive.Server.Runs;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The workspace a tab is on (ChosenWorkspace), as the screens ask it: the address first, then the one chosen last here,
/// and the one used last only when nothing was chosen - or what was chosen is no longer a workspace of the service.
/// </summary>
public sealed class ChosenWorkspaceTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-chosen", Guid.NewGuid().ToString("N"));

    private string Workspace(string name)
    {
        var root = Path.Combine(_folders, name);
        Directory.CreateDirectory(root);
        Services.GetRequiredService<ServerEngine>().Workspaces.Touch(root);
        return root;
    }

    private System.Security.Claims.ClaimsPrincipal As(string name) => ServerAuth.PrincipalOf(Users.Find(name)!);

    private IReadOnlyList<Enactive.Workspace.WorkspaceEntry> Known()
        => Services.GetRequiredService<ServerHistory>().Workspaces(As("alice"));

    [Fact]
    public void A_screen_with_no_workspace_in_its_address_opens_on_the_one_chosen_not_the_one_used_last()
    {
        var chosenRoot = Workspace("chosen");
        Workspace("used-last");
        var ids = Services.GetRequiredService<WorkspaceIds>();
        var chosen = Services.GetRequiredService<ChosenWorkspace>();
        var known = Known();
        var picked = known.Single(k => k.RootPath == chosenRoot);

        // Nothing chosen yet: the one used last, as before.
        Assert.Null(chosen.Wanted(null, known, ids, As("alice")));

        chosen.Shown(picked, ids, As("alice"));
        Assert.Equal(ids.TryIdOf(chosenRoot), chosen.Wanted(null, known, ids, As("alice")));
        // An address that names a workspace still wins - the switcher's links name the one picked.
        var other = ids.TryIdOf(known.First(k => k.RootPath != chosenRoot).RootPath);
        Assert.Equal(other, chosen.Wanted(other, known, ids, As("alice")));
    }

    [Fact]
    public void A_workspace_chosen_and_then_forgotten_is_passed_over_for_the_one_used_last()
    {
        var chosenRoot = Workspace("forgotten");
        Workspace("kept");
        var ids = Services.GetRequiredService<WorkspaceIds>();
        var chosen = Services.GetRequiredService<ChosenWorkspace>();
        chosen.Shown(Known().Single(k => k.RootPath == chosenRoot), ids, As("alice"));

        // Forgotten since: not among the ones the service knows, so not asked for - "not known" would answer a question
        // nobody on this screen asked.
        var left = Known().Where(k => k.RootPath != chosenRoot).ToList();

        Assert.Null(chosen.Wanted(null, left, ids, As("alice")));
    }

    [Fact]
    public void A_tab_opened_anew_starts_on_the_workspace_its_person_chose_last_and_nobody_else_s()
    {
        var chosenRoot = Workspace("before-the-drop");
        Workspace("used-last");
        var ids = Services.GetRequiredService<WorkspaceIds>();
        new ChosenWorkspace(Users).Shown(Known().Single(k => k.RootPath == chosenRoot), ids, As("alice"));

        // Another circuit - the connection dropped, the service restarted, alice signed in again - over another store on
        // the same server.db (the service's own, where the test chose through the test's): the same choice.
        var again = new ChosenWorkspace(Services.GetRequiredService<UserStore>());
        Assert.Equal(ids.TryIdOf(chosenRoot), again.Wanted(null, Known(), ids, As("alice")));
        // vera chose nothing: the one used last, never what alice chose.
        Assert.Null(again.Wanted(null, Known(), ids, As("vera")));
    }

    [Fact]
    public void A_tab_keeps_its_own_choice_when_another_tab_of_the_same_person_chooses_another()
    {
        var mine = Workspace("this-tab");
        var theirs = Workspace("other-tab");
        var ids = Services.GetRequiredService<WorkspaceIds>();
        var known = Known();
        var thisTab = new ChosenWorkspace(Users);
        var otherTab = new ChosenWorkspace(Users);

        thisTab.Shown(known.Single(k => k.RootPath == mine), ids, As("alice"));
        otherTab.Shown(known.Single(k => k.RootPath == theirs), ids, As("alice"));

        Assert.Equal(ids.TryIdOf(mine), thisTab.Wanted(null, known, ids, As("alice")));
        // A tab opened after both starts from the latest.
        Assert.Equal(ids.TryIdOf(theirs), new ChosenWorkspace(Users).Wanted(null, known, ids, As("alice")));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_folders, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
