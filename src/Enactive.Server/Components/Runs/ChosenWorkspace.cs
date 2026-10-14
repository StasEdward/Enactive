namespace Enactive.Server.Components.Runs;

using System.Security.Claims;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;
using Enactive.Workspace;

/// <summary>
/// The workspace a person is on: the one they chose last, carried from screen to screen as the window's one folder is -
/// Runs, Templates, Schedules, Timeline, Environment and Settings are all about it. A rail link names no workspace, and
/// each screen took the one used last by the service - the folder of the latest run anybody started - so choosing one
/// in the switcher and opening Settings went back to the first of the list.
///
/// <para>One per circuit (scoped), over the account's own choice kept on the service (UserStore.ChosenWorkspace): a tab
/// goes on with what it chose even when another tab of the same person chooses another, as a second window would, and a
/// tab opened anew - after a dropped connection, a restart of the service, a sign-in, on another machine - starts from
/// the last one the person chose anywhere. Never another person's: each account keeps its own.</para>
/// </summary>
internal sealed class ChosenWorkspace(UserStore users)
{
    private Guid? _id;

    /// <summary>
    /// The workspace to show: the one <paramref name="named"/> by the address (Guid.Empty for one that does not parse,
    /// which names none), or - with none named - the one chosen last, in this tab or else by this person anywhere, while
    /// the service still knows it for them; null for the one used last. A chosen one since forgotten is passed over
    /// without a word: the person did not ask for it on this screen, so "not known" would answer a question nobody asked.
    /// </summary>
    public Guid? Wanted(Guid? named, IReadOnlyList<WorkspaceEntry> known, WorkspaceIds ids, ClaimsPrincipal user)
    {
        if (named is not null)
            return named;
        var chosen = _id ?? (NameOf(user) is { } name ? users.ChosenWorkspace(name) : null);
        return chosen is { } id && WorkspaceColumnState.Choose(known, id, ids) is not null ? id : null;
    }

    /// <summary>The workspace a screen shows, the chosen one from now on - here and for this person; none changes nothing.</summary>
    public void Shown(WorkspaceEntry? shown, WorkspaceIds ids, ClaimsPrincipal user)
    {
        if (shown is null || ids.TryIdOf(shown.RootPath) is not { } id)
            return;
        // Written only when it changes: every screen says what it shows each time it is drawn for an address.
        if (_id == id)
            return;
        _id = id;
        if (NameOf(user) is { } name)
            users.ChooseWorkspace(name, id);
    }

    private static string? NameOf(ClaimsPrincipal user)
        => user.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.Name) : null;
}
