namespace Enactive.Server.Components.Settings;

using Enactive.Application;
using Enactive.Application.Mvvm;

/// <summary>
/// One section of the settings, as the window's list on the left has them (SettingsWindow.axaml): its address on the
/// web, its name in the list, whether it sits under AI, and the view model's command that opens it - which is the
/// view model's to run, because opening Providers is what asks each provider whether it is there, once.
///
/// <para>Not offered: Account and Remote access. The window's Account says nothing yet, and Remote access pairs the
/// desktop with a gateway - which a service does not do.</para>
/// </summary>
internal sealed record SettingsSection(string Slug, string Label, bool UnderAi, Func<SettingsViewModel, RelayCommand> Show)
{
    public static IReadOnlyList<SettingsSection> All { get; } =
    [
        new("general", "General", false, vm => vm.ShowGeneralCommand),
        new("writable-folders", "Writable folders", false, vm => vm.ShowWritableRootsCommand),
        new("smtp", "SMTP", false, vm => vm.ShowSmtpCommand),
        new("web", "Web", false, vm => vm.ShowWebCommand),
        new("about", "About", false, vm => vm.ShowAboutCommand),
        new("ai-general", "General", true, vm => vm.ShowAiGeneralCommand),
        new("ai-providers", "Providers", true, vm => vm.ShowAiProvidersCommand),
        new("ai-team", "Team", true, vm => vm.ShowAiTeamCommand),
        new("ai-phases", "Phases", true, vm => vm.ShowAiPhasesCommand),
        new("ai-mcp", "MCP", true, vm => vm.ShowMcpCommand),
        new("ai-templates", "Templates", true, vm => vm.ShowTemplatesCommand),
    ];

    /// <summary>
    /// The section an address names, or the first one: an address is typed by people, and one that names no section
    /// opens where the window opens - at the top of the list - rather than on a page that says nothing.
    /// </summary>
    public static SettingsSection Find(string? slug)
        => All.FirstOrDefault(s => string.Equals(s.Slug, slug, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>Its address, relative to the page's base.</summary>
    public string Href => Links.Settings(Slug);
}
