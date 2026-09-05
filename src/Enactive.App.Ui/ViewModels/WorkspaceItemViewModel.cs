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
        // The one you are in is marked, not repainted - the same quiet edge a selected row gets.
        CurrentBrush = isCurrent ? Brand.Accent : Brushes.Transparent;

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
    public IBrush CurrentBrush { get; }

    public RelayCommand SwitchCommand { get; }
    public RelayCommand ForgetCommand { get; }
}
