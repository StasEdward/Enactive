namespace Enactive.Application;

using Enactive.Application.Mvvm;

/// <summary>
/// The Checks section (Plan Phase 3.5): the global checks, as a <see cref="ChecksViewModel"/> the host makes - the window's
/// writes the files next to its settings; the service's is made by its own page over its data folder and changed as the
/// person (ServerChecks), so it is not this copy's. Like templates they are files, saved at once: the section says so.
/// </summary>
internal sealed partial class SettingsViewModel
{
    private const int SectionChecks = 13;

    public bool IsChecks => Section == SectionChecks;

    public RelayCommand ShowChecksCommand => _showChecks ??= new RelayCommand(() => Section = SectionChecks);

    private RelayCommand? _showChecks;

    /// <summary>
    /// The global checks list, set by a host that shows it in this view model's window; null, none here. Set again when the
    /// window's workspace changes: the list is read over it - what that workspace shadows, its implicit build and test - and
    /// one left on the workspace before said things about a folder no longer open.
    /// </summary>
    public ChecksViewModel? Checks
    {
        get => _checks;
        set => Set(ref _checks, value);
    }

    private ChecksViewModel? _checks;
}
