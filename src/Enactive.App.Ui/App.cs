using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Enactive.App.Ui;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;

        // Fluent builds every accented control - slider thumb, checkbox tick, focus ring,
        // selection - out of these seven keys. Overriding them is what makes the stock
        // theme speak Ember instead of Windows blue.
        Resources["SystemAccentColor"] = Brand.Ember500;
        Resources["SystemAccentColorLight1"] = Brand.Ember400;
        Resources["SystemAccentColorLight2"] = Brand.Ember300;
        Resources["SystemAccentColorLight3"] = Brand.Ember200;
        Resources["SystemAccentColorDark1"] = Brand.Ember600;
        Resources["SystemAccentColorDark2"] = Brand.Ember700;
        Resources["SystemAccentColorDark3"] = Brand.Ember800;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
