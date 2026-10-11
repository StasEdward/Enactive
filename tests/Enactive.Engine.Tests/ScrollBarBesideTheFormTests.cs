namespace Enactive.Engine.Tests;

using System.Text.RegularExpressions;

/// <summary>
/// In the configuration windows the scroll bar stands beside the form, not over it. Fluent's ScrollViewer lays the
/// content under the bar while AllowAutoHide is on (its default), so the bar covered the form's right margin and the
/// fields ran into it - a gap on the left and none on the right (2026-10-11).
/// </summary>
public sealed class ScrollBarBesideTheFormTests
{
    private static readonly string[] ConfigurationWindows =
    [
        "SettingsWindow", "ProviderEditWindow", "WorkerEditWindow", "McpEditWindow",
        "TemplateEditWindow", "TemplatesWindow", "SchedulesWindow"
    ];

    [Fact]
    public void Every_scroll_viewer_in_a_configuration_window_keeps_its_bar_beside_the_content()
    {
        var ui = Path.Combine(TestRepository.Root, "src", "Enactive.App.Ui");
        foreach (var window in ConfigurationWindows)
        {
            var xaml = File.ReadAllText(Path.Combine(ui, window + ".axaml"));
            var viewers = Regex.Matches(xaml, @"<ScrollViewer(\s[^>]*)?>").Select(m => m.Value).ToArray();
            Assert.NotEmpty(viewers);
            Assert.All(viewers, v => Assert.Matches(@"Classes=""[^""]*\bgutter\b", v));
            // A list scrolls itself, with a ScrollViewer of its own inside its template: the same bar over the same
            // cards (the template list in Settings, 2026-10-11).
            Assert.All(Regex.Matches(xaml, @"<ListBox\s[^>]*>").Select(m => m.Value),
                l => Assert.Matches(@"Classes=""[^""]*\bgutter\b", l));
        }

        var styles = File.ReadAllText(Path.Combine(ui, "Styles", "Controls.axaml"));
        Assert.Contains("<Style Selector=\"ScrollViewer.gutter\">", styles, StringComparison.Ordinal);
        Assert.Matches(@"<Setter Property=""AllowAutoHide"" Value=""False"" />", styles);
        Assert.Contains("<Style Selector=\"ScrollViewer.gutter.bare /template/ ScrollBar#PART_VerticalScrollBar\">", styles,
            StringComparison.Ordinal);
        Assert.Contains("<Style Selector=\"ListBox.gutter\">", styles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"ScrollViewer.AllowAutoHide\" Value=\"False\" />", styles, StringComparison.Ordinal);
        Assert.Contains(
            "<Style Selector=\"ListBox.gutter /template/ ScrollViewer#PART_ScrollViewer /template/ ScrollBar#PART_VerticalScrollBar\">",
            styles, StringComparison.Ordinal);
    }
}
