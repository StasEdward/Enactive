namespace Enactive.Application;

using System.Collections.ObjectModel;
using Enactive.Application.Mvvm;
using Enactive.Core.Permissions;
using Enactive.Workspace;

/// <summary>One standing grant, as a row somebody can look at and take back.</summary>
/// <param name="Workspace">
/// The workspace that holds it, in the words of the list on the main window, or a sentence saying
/// the workspace is gone.
/// </param>
/// <param name="Folder">The folder outside that workspace which runs there may write to.</param>
/// <param name="Key">
/// The store's key for the workspace (GrantedTo.Key): what a host that revokes elsewhere - the service, as the person -
/// names the grant by, as an orphan has no path left to name it with.
/// </param>
internal sealed record WritableRootRow(
    string Workspace, string Folder, bool IsOrphan, RelayCommand RevokeCommand, string Key = "")
{
    /// <summary>Said under an orphan's row: what its Revoke takes away. Every host says it in these words.</summary>
    public string OrphanNote =>
        "Still in force. Revoking removes every folder kept for that workspace, because there is no path left to name one by.";
}

internal sealed partial class SettingsViewModel
{
    private const int SectionWritableRoots = 10;

    private WritableRoots _writableRoots = WritableRoots.Default;

    public bool IsWritableRoots => Section == SectionWritableRoots;

    public RelayCommand ShowWritableRootsCommand { get; private set; } = null!;

    /// <summary>Every folder outside a workspace that some workspace may still write to.</summary>
    public ObservableCollection<WritableRootRow> WritableRootRows { get; } = new();

    /// <summary>
    /// Said when the list is empty, which is the ordinary state and must not look like a failure to
    /// load. Bound rather than a literal in the .axaml for the reason the hints moved to Core: a
    /// sentence in a WinExe is a sentence no test can reach.
    /// </summary>
    public string WritableRootsEmpty =>
        "No workspace has been given a folder outside itself. This list fills up only when you "
        + "answer \"keep for this workspace\" on the card that asks about a command writing "
        + "somewhere else.";

    /// <summary>What the section is: where the grants come from, and that a revoke is at once. Every host says it so.</summary>
    public string WritableRootsHint =>
        "A run may only write inside its workspace. When a command tries to write somewhere else you are asked, and one of "
        + "the answers is \"keep for this workspace\" — those are listed here. Revoking takes effect at once and is not "
        + "undone by Cancel: taking a permission away is not a setting.";

    private Func<IEnumerable<string>> _knownWorkspaces = () => WorkspaceRegistry.Load().Entries.Select(e => e.RootPath);

    private void InitializeWritableRoots(WritableRoots? writableRoots, Func<IEnumerable<string>>? knownWorkspaces)
    {
        _writableRoots = writableRoots ?? WritableRoots.Default;
        _knownWorkspaces = knownWorkspaces ?? _knownWorkspaces;
        ShowWritableRootsCommand = new(() => Section = SectionWritableRoots);
        ReloadWritableRoots();
    }

    /// <summary>
    /// Rebuilt from the store each time rather than mutated in place. The file is shared with every
    /// run of the engine, which adds to it while this window is open; a list edited in memory would
    /// be showing a state that was true when the window opened.
    ///
    /// <para>Internal for a host that revokes elsewhere (the service, as the person) and shows the list again after.</para>
    /// </summary>
    internal void ReloadWritableRoots()
    {
        WritableRootRows.Clear();

        // The store is keyed by the id a PATH gives and holds no paths, on purpose - see
        // WritableRoots.Review. The registry is what knows the workspaces, so the join happens here.
        var known = _knownWorkspaces().ToArray();

        foreach (var entry in _writableRoots.Review(known))
        {
            foreach (var folder in entry.Roots)
            {
                var captured = entry;
                var target = folder;

                WritableRootRows.Add(new WritableRootRow(
                    captured.IsOrphan
                        ? "A workspace this app no longer knows — removed from the list, moved, or renamed"
                        : captured.WorkspaceRoot!,
                    target,
                    captured.IsOrphan,
                    new RelayCommand(() => RevokeWritableRoot(captured, target)),
                    captured.Key));
            }
        }
    }

    /// <summary>
    /// Withdraws one grant, at once.
    ///
    /// <para><b>Not deferred to Save, deliberately.</b> Everything else in this window is edited
    /// against a copy and committed by the Save button, and a withdrawal that waited for it would
    /// leave the permission standing while the screen showed it gone - and would be undone by a
    /// Cancel somebody pressed about something else entirely. Taking a permission away is not a
    /// setting; the pane says so in as many words.</para>
    ///
    /// <para>An orphan has no workspace path left to revoke by, so the whole entry is forgotten by
    /// its key. That is the only handle it has.</para>
    /// </summary>
    private void RevokeWritableRoot(GrantedTo entry, string folder)
    {
        // Not for a host whose grants are not changed from its settings (editsFilesHere): no page reaches the file.
        if (!_editsFilesHere)
            return;

        if (entry.IsOrphan)
            _writableRoots.Forget(entry.Key);
        else
            _writableRoots.Revoke(entry.WorkspaceRoot!, folder);

        ReloadWritableRoots();
    }
}
