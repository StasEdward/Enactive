namespace Enactive.App.Ui.Tests;

using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Enactive.Application;

/// <summary>
/// The window's evidence budget box: blank is automatic (EngineOptions.EvidenceBudget), and a blank box shows what automatic
/// comes to for the model bound to Review - the view model's EvidenceBudgetAuto - where it used to say "6000 = the default",
/// which is no longer what blank means.
/// </summary>
public sealed class EvidenceBudgetBoxTests
{
    [Fact]
    public void A_blank_box_shows_what_automatic_comes_to()
    {
        var window = XDocument.Load(Path.Combine(Root(), "src", "Enactive.App.Ui", "SettingsWindow.axaml"));
        var box = window.Descendants().Single(e => e.Name.LocalName == "TextBox"
            && (string?)e.Attribute("Text") == $"{{Binding {nameof(SettingsViewModel.EvidenceBudgetText)}}}");

        Assert.Equal($"{{Binding {nameof(SettingsViewModel.EvidenceBudgetAuto)}}}", (string?)box.Attribute("PlaceholderText"));
    }

    private static string Root([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
