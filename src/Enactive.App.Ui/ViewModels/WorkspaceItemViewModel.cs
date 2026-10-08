namespace Enactive.App.Ui.ViewModels;

using Enactive.App.Ui.Mvvm;
using Enactive.Workspace;

/// <summary>
/// One workspace in the switcher: its name, its path, and whether the folder is still there.
///
/// A folder that has gone is MARKED, not hidden. Hiding it would quietly erase a project from the
/// list the first time a drive was not mounted, and the user would be left wondering where it went.
/// </summary>
internal sealed class WorkspaceItemViewModel
{
    // How the row is drawn - its name and detail by Exists, its fill by IsCurrent, its edge by Place - is the
    // view's (Palette.Workspace*).

    public WorkspaceItemViewModel(WorkspaceEntry entry, bool exists, bool isCurrent,
                                  Action<string> switchTo, Action<string> rename, Action<string> forget)
    {
        Entry = entry;
        Exists = exists;
        IsCurrent = isCurrent;

        Name = entry.Name;
        Path = entry.RootPath;
        Detail = exists ? entry.RootPath : "Folder not found · " + entry.RootPath;

        SwitchCommand = new RelayCommand(() => switchTo(entry.RootPath));
        RenameCommand = new RelayCommand(() => rename(entry.RootPath));
        ForgetCommand = new RelayCommand(() => forget(entry.RootPath));
    }

    public WorkspaceEntry Entry { get; }
    public bool Exists { get; }
    public bool IsCurrent { get; }

    public string Name { get; }
    public string Path { get; }
    public string Detail { get; }

    /// <summary>A missing folder outranks being current: the name is already red, and the edge agrees (Palette.WorkspaceEdge).</summary>
    public WorkspacePlace Place => !Exists ? WorkspacePlace.Missing : IsCurrent ? WorkspacePlace.Current : WorkspacePlace.Other;

    public RelayCommand SwitchCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand ForgetCommand { get; }
}
