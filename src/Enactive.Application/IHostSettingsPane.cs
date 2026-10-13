namespace Enactive.Application;

using Enactive.Settings;

/// <summary>
/// A settings pane only one host has: the desktop's Remote access, which pairs this computer with a gateway and applies
/// a connection code to keys the window's own service holds. A service does neither, and this assembly may not reach
/// the remote host's code, so the shared settings know such a pane only as far as their Save needs to.
///
/// <para>It was a part of the settings' view model itself while that lived in the window. A part of a class cannot
/// live in another assembly, so the view model could not be shared without leaving this pane behind - which is what
/// this is: the pane, left behind on purpose, and told when to write itself into the settings.</para>
/// </summary>
internal interface IHostSettingsPane
{
    /// <summary>
    /// Doing something Save must wait for. The window's Remote access applies a connection code at once, and a Save
    /// while it ran could write one computer's settings over another's keys.
    /// </summary>
    bool Busy { get; }

    /// <summary>Raised when <see cref="Busy"/> changes, so Save is offered again.</summary>
    event Action? BusyChanged;

    /// <summary>The pane was opened.</summary>
    void Shown();

    /// <summary>Writes the pane into the settings about to be saved.</summary>
    void Save(AppSettings working);
}
