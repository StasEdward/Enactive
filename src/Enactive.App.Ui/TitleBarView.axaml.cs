using Avalonia.Controls;

namespace Enactive.App.Ui;

/// <summary>
/// The title bar every window wears. It was MainWindow's alone, which is why every other window
/// still showed the system one - a strip in Windows' grey above a dark app.
///
/// <para>Moving the window is the platform's job (the strip declares TitleBar, which is HTCAPTION
/// on Win32). The buttons are NOT delegated: the Win32 button roles hand the click to Windows and
/// nothing comes back, so they declare DecorationsElement - documented to pass input through - and
/// are handled here.</para>
/// </summary>
public sealed partial class TitleBarView : UserControl
{
    private bool _watchingState;

    public TitleBarView()
    {
        InitializeComponent();

        MinimiseButton.Click += (_, _) => With(w => w.WindowState = WindowState.Minimized);
        MaximiseButton.Click += (_, _) => With(w =>
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        CloseButton.Click += (_, _) => With(w => w.Close());

        // The window is not reachable until this is in the tree, so the glyph is synced then and
        // whenever the state changes after. Once only: attaching twice would subscribe twice.
        AttachedToVisualTree += (_, _) => With(window =>
        {
            if (_watchingState)
                return;
            _watchingState = true;

            SyncMaximiseGlyph(window);
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty)
                    SyncMaximiseGlyph(window);
            };
        });
    }

    private void With(Action<Window> action)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
            action(window);
    }

    private void SyncMaximiseGlyph(Window window)
    {
        var maximised = window.WindowState == WindowState.Maximized;
        MaximiseGlyph.IsVisible = !maximised;
        RestoreGlyph.IsVisible = maximised;
        MaximiseButton.SetValue(ToolTip.TipProperty, maximised ? "Restore" : "Maximise");
    }
}
