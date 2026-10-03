namespace Enactive.Engine.Tests;

using Enactive.App.Ui;
using Enactive.Core.Tools;
using Enactive.Settings;
using Xunit;

/// <summary>
/// Saving in Settings writes the window's copy of the settings and puts it in place of the live object. Two
/// things were lost across that swap: what the app wrote into the live object while the window was open, and
/// every window still holding the object from before.
/// </summary>
public sealed class LiveSettingsOutliveTheEditorTests
{
    [Fact]
    public void A_schedule_saved_after_commands_were_turned_off_is_not_given_the_rule_from_before()
    {
        var live = new AppSettings { ShellCommands = ShellCommandPolicy.Follow };
        var policy = new SchedulePolicy(() => live);
        Assert.DoesNotContain(policy.For("autonomous").Deny, ShellTools.IsShell);

        // What a Settings save does: a new object, not a change to the old one.
        var saved = live.Clone();
        saved.ShellCommands = ShellCommandPolicy.Off;
        live = saved;

        Assert.Contains(policy.For("autonomous").Deny, ShellTools.IsShell);
    }

    [Fact]
    public void Where_the_window_was_moved_while_Settings_was_open_survives_its_save()
    {
        var live = new AppSettings { WindowX = 10, WindowY = 20, WindowWidth = 900, WindowHeight = 700 };
        var edited = live.Clone();

        // The main window is moved and hidden behind the open Settings window: bounds go to the live object.
        (live.WindowX, live.WindowY, live.WindowWidth, live.WindowHeight) = (300, 40, 1200, 800);
        edited.CloseToTray = !edited.CloseToTray;

        edited.KeepWhatTheAppWrote(live);

        Assert.Equal((300, 40, 1200, 800), (edited.WindowX, edited.WindowY, edited.WindowWidth, edited.WindowHeight));
        Assert.NotEqual(live.CloseToTray, edited.CloseToTray);   // what the person changed is theirs
    }
}
