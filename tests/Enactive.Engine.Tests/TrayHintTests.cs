namespace Enactive.Engine.Tests;

using Enactive.App.Ui;
using Xunit;

/// <summary>
/// Hiding the window to the tray says so every time, once per hide. It was said once ever, remembered in the
/// settings, and after the first close every later one looked like the X had done nothing again (2026-10-05).
/// </summary>
public sealed class TrayHintTests
{
    [Fact]
    public void Every_hide_to_the_tray_is_said_once_until_the_window_comes_back()
    {
        var hint = new TrayHint();

        Assert.True(hint.TakeOnHide());
        Assert.False(hint.TakeOnHide());   // still hidden: already said

        hint.WindowShown();
        Assert.True(hint.TakeOnHide());    // the next close is said again
    }

    [Fact]
    public void The_note_says_where_the_window_went_and_how_to_quit()
    {
        var text = TrayHint.Detail(windows: true);

        Assert.Contains("system tray", text);
        Assert.Contains("^ arrow", text);
        Assert.Contains("Exit", text);
        Assert.DoesNotContain("^ arrow", TrayHint.Detail(windows: false));
    }
}
