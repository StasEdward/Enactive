using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Enactive.App.Ui;

/// <summary>
/// The note <see cref="TrayHint"/> decides on, in the corner of the screen the window was on. It takes no focus,
/// asks nothing and goes by itself; a click on it brings the window back.
/// </summary>
public sealed partial class TrayHintWindow : Window
{
    // Long enough to read three sentences, short enough not to outstay a note nobody clicked.
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(3);

    // From the screen's edges, in device-independent pixels: off the taskbar, clear of the corner.
    private const double EdgeGap = 12;

    public TrayHintWindow()
    {
        InitializeComponent();
    }

    /// <param name="screen">The screen the main window was on, taken before it was hidden; null for the primary.</param>
    /// <param name="showMain">What a click on the note does: brings the main window back.</param>
    /// <returns>The note, so that the window coming back can take it down.</returns>
    public static TrayHintWindow ShowNote(Screen? screen, Action showMain)
    {
        var hint = new TrayHintWindow();
        hint.DetailText.Text = TrayHint.Detail(OperatingSystem.IsWindows());

        var expiry = DispatcherTimer.RunOnce(hint.Close, Lifetime);
        hint.Closed += (_, _) => expiry.Dispose();
        hint.PointerPressed += (_, _) =>
        {
            hint.Close();
            showMain();
        };
        // Placed again whenever its size changes, not only when it opens: at Opened, SizeToContent has not yet
        // fitted the height, and the note placed by the height it had then (about 714 instead of 156) stood
        // halfway up the screen, nowhere near the corner it was described as being in.
        Screen? Target() => screen ?? hint.Screens.Primary;
        hint.Opened += (_, _) => hint.PlaceIn(Target());
        hint.SizeChanged += (_, _) => hint.PlaceIn(Target());

        hint.Show();
        return hint;
    }

    private void PlaceIn(Screen? screen)
    {
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        Position = new PixelPoint(
            area.Right - (int)Math.Ceiling((Bounds.Width + EdgeGap) * scale),
            area.Bottom - (int)Math.Ceiling((Bounds.Height + EdgeGap) * scale));
    }
}
