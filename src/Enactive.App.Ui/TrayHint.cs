namespace Enactive.App.Ui;

/// <summary>
/// When the close button hiding the window to the tray says so, and what it says.
///
/// <para>Closing to the tray is the default, so the X looked like it did nothing: the window went, the process
/// stayed, and Windows keeps a new tray icon behind the ^ arrow (2026-10-05). Avalonia's tray icon has no balloon
/// to say so, hence a small window of the app's own.</para>
///
/// <para>Said on every hide, once per hide: the mark is set when the window goes and cleared when it comes back.
/// It used to be said once ever, remembered in the settings - and once was not enough to learn it (2026-10-05):
/// after the first close, every later one looked like nothing again.</para>
/// </summary>
public sealed class TrayHint
{
    private bool _said;

    /// <summary>True when the window is hidden to the tray and the note has not been said since it was last shown.</summary>
    public bool TakeOnHide()
    {
        if (_said)
            return false;
        _said = true;
        return true;
    }

    /// <summary>The window is back: the next hide is said again.</summary>
    public void WindowShown() => _said = false;

    /// <summary>Where the window went, how to get it back, and how to make the button quit instead.</summary>
    public static string Detail(bool windows)
        => "The window is in the system tray, and anything it was doing carries on. "
           + (windows ? "Its icon may be behind the ^ arrow next to the clock. " : "")
           + "Click the icon to bring the window back; Exit on its menu quits. "
           + "Settings · General can make the close button quit instead.";
}
