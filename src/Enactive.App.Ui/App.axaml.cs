using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace Enactive.App.Ui;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // After the XAML, so these win over anything the theme brought with it.
        Brand.PublishTo(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The window closing no longer means the app is over - it goes to the tray, where a run
            // it started keeps going. Only "Exit" ends the process, so shutdown is explicit.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var window = new MainWindow();
            desktop.MainWindow = window;
            InstallTrayIcon(window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The tray icon is the app while the window is away. Two items, because there are exactly two
    /// things left to say to a hidden window: come back, or stop.
    /// </summary>
    private void InstallTrayIcon(MainWindow window)
    {
        try
        {
            var show = new NativeMenuItem("Show window");
            show.Click += (_, _) => window.ShowFromTray();

            var exit = new NativeMenuItem("Exit");
            exit.Click += (_, _) => window.RequestExit();

            var menu = new NativeMenu();
            menu.Add(show);
            menu.Add(exit);

            var tray = new TrayIcon
            {
                // Says where the app went, for the one moment it matters: the first close.
                ToolTipText = "Enactive — running in the tray",
                Menu = menu,
                IsVisible = true
            };

            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://enactive-ui/Assets/icon.png"));
                tray.Icon = new WindowIcon(stream);
            }
            catch
            {
                // A tray icon with no picture is still a tray icon.
            }

            // A plain click is the same as "Show window": that is what every tray app does, and a
            // hidden window with no way back short of a right-click menu is a lost window.
            tray.Clicked += (_, _) => window.ShowFromTray();

            TrayIcon.SetIcons(this, new TrayIcons { tray });
        }
        catch
        {
            // No tray on this desktop. The window then has nowhere to hide, which MainWindow
            // handles by closing for real - see HasTray.
            window.HasTray = false;
        }
    }
}
