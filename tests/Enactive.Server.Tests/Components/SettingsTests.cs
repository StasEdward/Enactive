namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Core.Providers;
using Enactive.Server.Components.Pages;
using Enactive.Server.Runs;
using Enactive.Settings;
using Enactive.Tools.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Settings as the window's (SettingsWindow.axaml): the sections on the left - General, Writable folders, SMTP, Web,
/// About, and under AI General, Providers, Team, Phases, MCP and Templates - the one chosen on the right, Save and Cancel
/// at the bottom. The page binds the window's own view model (SettingsViewModel) over a copy of the service's settings
/// that holds no secret. The settings are the service's own file in its data folder (the harness's temp root), never the
/// runner's %APPDATA%.
/// </summary>
public sealed class SettingsTests : ComponentHost
{
    private const string Key = "sk-page-must-never-see";
    private const string MailPassword = "mail-page-must-never-see";
    private const string Token = "tk-page-must-never-see";
    private const string Variable = "env-page-must-never-see";
    private const string Header = "hdr-page-must-never-see";

    private readonly string _roots = Path.Combine(Path.GetTempPath(), "enactive-settings-roots", Guid.NewGuid().ToString("N"));

    public SettingsTests()
    {
        Directory.CreateDirectory(_roots);
        Services.AddSingleton(new WorkspaceRoots([_roots]));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Enactive.Tests.SqlitePools.ClearUnder(_roots);
            try { Directory.Delete(_roots, recursive: true); } catch { /* a temp folder */ }
        }
    }

    private ServerEngine Engine => Services.GetRequiredService<ServerEngine>();

    /// <summary>The service's settings with one of every secret a page edits, stored as the service stores them.</summary>
    private void StoreSecrets()
    {
        var settings = AppSettings.Load(Engine.SettingsFile);
        settings.Providers.Add(new ProviderConfig { Id = "cloud", Kind = ProviderKind.Anthropic, BaseUrl = "https://api.example.com", ApiKey = Key, Models = ["big"],
            Headers = new() { ["x-api-key"] = Header } });
        settings.Smtp.Host = "mail.example.com";
        settings.Smtp.Password = MailPassword;
        settings.McpServers.Add(new McpServerConfig
        {
            Id = "tracker", Transport = McpTransportKind.Http, Url = "https://mcp.example.com/",
            Environment = new() { ["TRACKER_TOKEN"] = Variable },
            Headers = new() { ["Authorization"] = "Bearer " + Token }
        });
        Assert.True(settings.Save(Engine.SettingsFile, replaceUnreadable: true), settings.LastSaveError);
    }

    private IRenderedComponent<SettingsScreen> Screen(string who, string? section = null)
    {
        SignIn(who);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/" + Enactive.Server.Components.Links.Settings(section));
        var screen = Render<SettingsScreen>(p => p.Add(s => s.Section, section));
        screen.WaitForAssertion(() => Assert.True(screen.FindAll(".settings-pane, .said").Count > 0));
        return screen;
    }

    private static void Press(IRenderedComponent<SettingsScreen> screen, string button)
        => screen.FindAll("button").Single(b => b.TextContent.Trim() == button).Click();

    /// <summary>Opens another section as the rail's link would: the same page, another address.</summary>
    private static void Go(IRenderedComponent<SettingsScreen> screen, string section)
        => screen.Render(p => p.Add(s => s.Section, section));

    /// <summary>The card of a section's list whose title is exactly this.</summary>
    private static AngleSharp.Dom.IElement Card(IRenderedComponent<SettingsScreen> screen, string title)
        => screen.FindAll(".settings-card").Single(c => c.QuerySelector(".card-title")!.TextContent.Trim() == title);

    private static string[] Sections(IRenderedComponent<SettingsScreen> screen)
        => screen.FindAll(".settings-nav a").Select(a => a.TextContent.Trim()).ToArray();

    [Fact]
    public void Only_an_admin_opens_settings()
    {
        // The address is the Admin's (the router refuses it to anybody else, as it refused /admin/settings) ...
        Assert.Contains(typeof(SettingsScreen).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false),
            a => ((AuthorizeAttribute)a).Policy == "Admin");

        // ... and the service refuses the settings to anybody else, whatever page asks.
        var refused = Screen("alice");
        Assert.Contains("Your role does not include the service's settings.", refused.Find(".said").TextContent);
        Assert.Empty(refused.FindAll(".settings-nav"));
        Assert.DoesNotContain(refused.FindAll("button"), b => b.TextContent.Trim() == "Save");

        var admin = Screen("ada");
        Assert.NotEmpty(Sections(admin));
        Assert.Single(admin.FindAll("button"), b => b.TextContent.Trim() == "Save");
    }

    [Fact]
    public void Writable_folders_are_the_service_s_roots_read_only()
    {
        var screen = Screen("ada", "writable-folders");

        var pane = screen.Find(".settings-pane");
        Assert.Equal([_roots], pane.QuerySelectorAll(".settings-roots .card-title").Select(e => e.TextContent.Trim()));
        Assert.Contains("WorkspaceRoots in server.json", pane.TextContent);
        // Nothing to add, revoke or type: they are set at install.
        Assert.Empty(pane.QuerySelectorAll("button, input, textarea, select"));
    }

    [Fact]
    public void There_is_no_account_or_remote_access_section()
    {
        var screen = Screen("ada");

        Assert.Equal(["General", "Writable folders", "SMTP", "Web", "About", "General", "Providers", "Team", "Phases", "MCP", "Templates"],
            Sections(screen));
        Assert.Equal(["settings/general", "settings/writable-folders", "settings/smtp", "settings/web", "settings/about",
                "settings/ai-general", "settings/ai-providers", "settings/ai-team", "settings/ai-phases", "settings/ai-mcp", "settings/ai-templates"],
            screen.FindAll(".settings-nav a").Select(a => a.GetAttribute("href")));
        Assert.DoesNotContain("Remote access", screen.Markup);
        Assert.DoesNotContain(">Account<", screen.Markup);
    }

    /// <summary>
    /// No stored secret reaches the page - not the key, the mail password, an MCP token or variable, nor the ciphertext any
    /// of them is stored as - in any section, or in the editors that open over them.
    /// </summary>
    [Fact]
    public void A_key_is_never_sent_back_to_the_page()
    {
        StoreSecrets();
        var stored = AppSettings.Load(Engine.SettingsFile);
        string[] secrets = [Key, MailPassword, Token, Variable, Header, stored.Providers.Single(p => p.Id == "cloud").ApiKeyProtected,
            stored.Smtp.PasswordProtected, stored.McpServers.Single().SecretsProtected];

        var screen = Screen("ada");
        var seen = new List<string>();
        foreach (var section in Enactive.Server.Components.Settings.SettingsSection.All)
        {
            Go(screen, section.Slug);
            seen.Add(screen.Markup);
        }

        // What each section with a secret says instead: that one is kept.
        Go(screen, "smtp");
        Assert.Contains("A password is stored.", screen.Find(".settings-pane").TextContent);
        Assert.Equal("", screen.Find("#smtp-password").GetAttribute("value") ?? "");

        Go(screen, "ai-providers");
        Card(screen, "cloud").QuerySelector("button[title='Edit']")!.Click();
        screen.WaitForAssertion(() => Assert.Contains("A key is stored.", screen.Find("[role='dialog']").TextContent));
        Assert.Equal("", screen.Find("#provider-key").GetAttribute("value") ?? "");
        Assert.Contains("Set: x-api-key: …", screen.Find("[role='dialog']").TextContent);
        seen.Add(screen.Markup);
        screen.Find("[role='dialog'] .dialog-actions").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();

        Go(screen, "ai-mcp");
        Card(screen, "tracker").QuerySelector("button[title='Edit']")!.Click();
        screen.WaitForAssertion(() => Assert.Contains("TRACKER_TOKEN=…", screen.Find("[role='dialog']").TextContent));
        Assert.Contains("Authorization: …", screen.Find("[role='dialog']").TextContent);
        seen.Add(screen.Markup);

        Assert.All(seen, markup => Assert.All(secrets, secret => Assert.DoesNotContain(secret, markup)));
        // Nor the handles the copy holds in their place: a name for a secret is not for the page either.
        Assert.All(seen, markup => Assert.DoesNotContain("kept:", markup));
    }

    /// <summary>
    /// One copy for every section, as the window has: a change in one is kept while another is open, and nothing is
    /// written until Save - which writes it, says so, and opens the settings again as they now are.
    /// </summary>
    [Fact]
    public void Edits_are_kept_across_sections_and_written_only_on_save()
    {
        var screen = Screen("ada", "web");
        screen.Find("#web-search").Input("http://localhost:8888");
        Go(screen, "smtp");
        screen.Find("#smtp-from").Change("enactive@example.com");
        Go(screen, "web");

        Assert.Equal("http://localhost:8888", screen.Find("#web-search").GetAttribute("value"));
        Assert.Equal("", AppSettings.Load(Engine.SettingsFile).Web.SearchUrl);

        Press(screen, "Save");

        screen.WaitForAssertion(() => Assert.StartsWith("Saved.", screen.Find(".settings-said").TextContent.Trim()));
        var saved = AppSettings.Load(Engine.SettingsFile);
        Assert.Equal(("http://localhost:8888", "enactive@example.com"), (saved.Web.SearchUrl, saved.Smtp.From));
        Assert.Contains(Users.AuditTrail(), e => e is { Account: "ada", Action: "settings saved" });
    }

    /// <summary>Cancel drops every change and opens the settings again, as they are on disk.</summary>
    [Fact]
    public void Cancel_discards_the_changes_and_reopens_the_stored_settings()
    {
        var screen = Screen("ada", "web");
        screen.Find("#web-search").Input("http://localhost:9999");

        Press(screen, "Cancel");

        screen.WaitForAssertion(() => Assert.Equal("", screen.Find("#web-search").GetAttribute("value") ?? ""));
        Assert.Equal("", AppSettings.Load(Engine.SettingsFile).Web.SearchUrl);
    }

    [Fact]
    public void An_address_that_names_no_section_opens_the_first()
    {
        var screen = Screen("ada", "remote-access");

        Assert.Equal("General", screen.Find(".settings-nav a[aria-current='page']").TextContent.Trim());
        Assert.Equal("General", screen.Find(".settings-pane").GetAttribute("aria-label"));
    }

    /// <summary>
    /// The password box opens empty and keeps the stored password; Remove takes it away on Save - where the window's box,
    /// which holds a placeholder, is emptied to remove it.
    /// </summary>
    [Fact]
    public void A_stored_password_is_kept_by_an_empty_box_and_removed_on_purpose()
    {
        StoreSecrets();
        var screen = Screen("ada", "smtp");
        Press(screen, "Save");
        screen.WaitForAssertion(() => Assert.StartsWith("Saved.", screen.Find(".settings-said").TextContent.Trim()));
        Assert.Equal(MailPassword, AppSettings.Load(Engine.SettingsFile).Smtp.Password);

        screen.WaitForAssertion(() => Assert.Contains(screen.FindAll(".settings-pane label.check"), l => l.TextContent.Contains("Remove the stored password")));
        screen.FindAll(".settings-pane label.check").Single(l => l.TextContent.Contains("Remove the stored password"))
            .QuerySelector("input")!.Change(true);
        Press(screen, "Save");

        screen.WaitForAssertion(() => Assert.Equal("", AppSettings.Load(Engine.SettingsFile).Smtp.Password));
        Assert.Equal("", AppSettings.Load(Engine.SettingsFile).Smtp.PasswordProtected);
    }

    /// <summary>
    /// A provider added in the window's editor, its key typed there, is in the settings once they are saved - encrypted on
    /// disk, and said as kept, never shown, when they are opened again.
    /// </summary>
    [Fact]
    public void A_provider_added_with_its_key_is_saved_and_its_key_kept()
    {
        var screen = Screen("ada", "ai-providers");
        Press(screen, "Add");
        screen.WaitForAssertion(() => screen.Find("#provider-id"));
        screen.Find("#provider-id").Change("box");
        screen.Find("#provider-url").Change("http://127.0.0.1:8080/v1");
        screen.Find("#provider-key").Input("sk-typed-here");
        screen.Find("#provider-models").Change("qwen");
        Press(screen, "Apply");
        screen.WaitForAssertion(() => Assert.Contains(screen.FindAll(".settings-card .card-title"), t => t.TextContent.Trim() == "box"));

        Press(screen, "Save");

        screen.WaitForAssertion(() => Assert.StartsWith("Saved.", screen.Find(".settings-said").TextContent.Trim()));
        var box = AppSettings.Load(Engine.SettingsFile).Providers.Single(p => p.Id == "box");
        Assert.Equal("sk-typed-here", box.ApiKey);
        Assert.Equal(["qwen"], box.Models);
        Assert.DoesNotContain("sk-typed-here", File.ReadAllText(Engine.SettingsFile));
        Assert.DoesNotContain("sk-typed-here", screen.Markup);
    }

    /// <summary>
    /// Templates are files, saved by their editor at once, and the section says so; it lists them and leads to the
    /// Templates screen's editor rather than being a second one.
    /// </summary>
    [Fact]
    public void The_templates_section_lists_them_and_leads_to_their_editor()
    {
        var workspace = Path.Combine(_roots, "project");
        Directory.CreateDirectory(workspace);
        Engine.Workspaces.Touch(workspace);
        var id = Services.GetRequiredService<WorkspaceIds>().TryIdOf(workspace)!.Value;

        var screen = Screen("ada", "ai-templates");

        var pane = screen.Find(".settings-pane");
        Assert.Contains("Templates are FILES", pane.TextContent);
        Assert.NotEmpty(pane.QuerySelectorAll(".settings-card"));
        Assert.All(pane.QuerySelectorAll(".settings-card a.card-title"),
            a => Assert.StartsWith("templates/", a.GetAttribute("href")));
        Assert.Equal(Enactive.Server.Components.Links.NewTemplate(id), screen.FindAll("a.btn").Single(a => a.TextContent.Trim() == "New template").GetAttribute("href"));
    }
}
