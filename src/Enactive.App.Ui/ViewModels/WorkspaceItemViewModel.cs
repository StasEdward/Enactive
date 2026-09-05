namespace Enactive.App.Ui.ViewModels;

using Avalonia.Media;
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
    public WorkspaceItemViewModel(WorkspaceEntry entry, bool exists, bool isCurrent,
                                  Action<string> switchTo, Action<string> forget)
    {
        Entry = entry;
        Exists = exists;
        IsCurrent = isCurrent;

        Name = entry.Name;
        Path = entry.RootPath;
        Detail = exists ? entry.RootPath : "Folder not found · " + entry.RootPath;
        DetailBrush = exists ? Brand.TextMuted : Brand.Danger;
        NameBrush = exists ? Brand.Text : Brand.TextMuted;

        // EVERY row gets an edge, not just the current one. An edge that appears on one row only
        // reads as decoration on that row; an edge on all of them is a column you can scan - which
        // is the whole point, because "which of these is gone" is what you came here to see.
        // A missing folder outranks being current: the name is already red, and the edge agrees.
        EdgeBrush = !exists ? Brand.Danger
            : isCurrent ? Brand.Accent
            : Brand.LineStrong;

        // The row you are in sits a little brighter. Same light, more of it - the edge carries the
        // meaning, the fill only says where you are.
        FillBrush = isCurrent ? Brand.CardFillActive : Brand.CardFill;

        SwitchCommand = new RelayCommand(() => switchTo(entry.RootPath));
        ForgetCommand = new RelayCommand(() => forget(entry.RootPath));
    }

    public WorkspaceEntry Entry { get; }
    public bool Exists { get; }
    public bool IsCurrent { get; }

    public string Name { get; }
    public string Path { get; }
    public string Detail { get; }
    public IBrush DetailBrush { get; }
    public IBrush NameBrush { get; }
    public IBrush EdgeBrush { get; }
    public IBrush FillBrush { get; }

    public RelayCommand SwitchCommand { get; }
    public RelayCommand ForgetCommand { get; }
}
