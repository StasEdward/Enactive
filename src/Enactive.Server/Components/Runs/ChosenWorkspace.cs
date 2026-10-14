namespace Enactive.Server.Components.Runs;

using Enactive.Server.Runs;
using Enactive.Workspace;

/// <summary>
/// The workspace this tab is on: the one its person chose last, carried from screen to screen as the window's one folder
/// is - Runs, Templates, Schedules, Timeline, Environment and Settings are all about it. A rail link names no workspace,
/// and each screen took the one used last by the service - the folder of the latest run anybody started - so choosing
/// one in the switcher and opening Settings went back to the first of the list.
///
/// <para>One per circuit (scoped): what one person chose is never another's, and a second tab chooses on its own, as a
/// second window would. A reload starts again from the address, which names the workspace wherever it was chosen.</para>
/// </summary>
internal sealed class ChosenWorkspace
{
    private Guid? _id;

    /// <summary>
    /// The workspace to show: the one <paramref name="named"/> by the address (Guid.Empty for one that does not parse,
    /// which names none), or - with none named - the one chosen last here while the service still knows it for this
    /// person; null for the one used last. A chosen one since forgotten is passed over without a word: the person did not
    /// ask for it on this screen, so "not known" would answer a question nobody asked.
    /// </summary>
    public Guid? Wanted(Guid? named, IReadOnlyList<WorkspaceEntry> known, WorkspaceIds ids)
        => named ?? (_id is { } id && WorkspaceColumnState.Choose(known, id, ids) is not null ? id : null);

    /// <summary>The workspace a screen shows, the chosen one from now on; none shown changes nothing.</summary>
    public void Shown(WorkspaceEntry? shown, WorkspaceIds ids)
    {
        if (shown is not null && ids.TryIdOf(shown.RootPath) is { } id)
            _id = id;
    }
}
