namespace Enactive.Server.Tests.Components;

using Enactive.Core.Permissions;

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

    /// <summary>
    /// Presses a card's Edit, found and pressed in one go on the page's own dispatcher: a section shown a moment ago may still
    /// be drawing, and a button found before its last drawing has a handler that is gone by the press (bUnit's
    /// UnknownEventHandlerIdException, seen under a loaded test run).
    /// </summary>
    private static void Edit(IRenderedComponent<SettingsScreen> screen, string card)
        => screen.InvokeAsync(() => Card(screen, card).QuerySelector("button[title='Edit']")!.Click()).GetAwaiter().GetResult();

    /// <summary>Presses the open dialog's Cancel, as <see cref="Edit"/> presses Edit.</summary>
    private static void CancelDialog(IRenderedComponent<SettingsScreen> screen)
        => screen.InvokeAsync(() => screen.Find("[role='dialog'] .dialog-actions").QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "Cancel").Click()).GetAwaiter().GetResult();

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
        // Nothing to add or type: they are set at install. With no grant kept, nothing to revoke either - and the window's
        // words for that.
        Assert.Empty(pane.QuerySelectorAll("button, input, textarea, select"));
        Assert.StartsWith("No workspace has been given a folder outside itself.", pane.QuerySelector(".settings-grants-empty")!.TextContent.Trim());
    }

    /// <summary>
    /// The grants an Operator kept from a decision card ("keep for this workspace") are listed under the roots as the
    /// window lists them - the folder, its workspace, the window's words - and an Admin's Revoke takes one back at once,
    /// through the service, audited. Before, the section showed only the roots: a grant made on the service could be
    /// neither seen nor taken back from a page.
    /// </summary>
    [Fact]
    public void Writable_folders_list_the_kept_grants_and_an_admin_revokes_one()
    {
        var workspace = Path.Combine(_roots, "project");
        var outside = Path.Combine(_roots, "shared");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        Engine.Workspaces.Touch(workspace);
        var grants = Services.GetRequiredService<WritableRoots>();
        Assert.Null(grants.Add(workspace, outside));

        var screen = Screen("ada", "writable-folders");
        var pane = screen.Find(".settings-pane");
        Assert.Contains("those are listed here. Revoking takes effect at once", pane.TextContent);
        var card = Assert.Single(pane.QuerySelectorAll(".settings-grants .settings-card"));
        Assert.Equal(outside, card.QuerySelector(".card-title")!.TextContent.Trim(), ignoreCase: true);
        Assert.Equal(workspace, card.QuerySelector(".card-meta")!.TextContent.Trim(), ignoreCase: true);

        screen.InvokeAsync(() => screen.Find(".settings-grants button").Click()).GetAwaiter().GetResult();

        screen.WaitForAssertion(() => Assert.Empty(screen.FindAll(".settings-grants .settings-card")));
        Assert.StartsWith("Revoked:", screen.Find(".settings-grants-said").TextContent.Trim());
        Assert.Empty(grants.For(workspace));
        Assert.Contains(Users.AuditTrail(), e => e is { Account: "ada", Action: "writable folder revoked" });
    }

    [Fact]
    public void There_is_no_account_or_remote_access_section()
    {
        var screen = Screen("ada");

        Assert.Equal(["General", "Writable folders", "SMTP", "Web", "About", "General", "Providers", "Team", "Phases", "MCP", "Templates", "Checks"],
            Sections(screen));
        Assert.Equal(["settings/general", "settings/writable-folders", "settings/smtp", "settings/web", "settings/about",
                "settings/ai-general", "settings/ai-providers", "settings/ai-team", "settings/ai-phases", "settings/ai-mcp", "settings/ai-templates", "settings/ai-checks"],
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
        Edit(screen, "cloud");
        screen.WaitForAssertion(() => Assert.Contains("A key is stored.", screen.Find("[role='dialog']").TextContent));
        Assert.Equal("", screen.Find("#provider-key").GetAttribute("value") ?? "");
        Assert.Contains("Set: x-api-key: …", screen.Find("[role='dialog']").TextContent);
        seen.Add(screen.Markup);
        CancelDialog(screen);

        Go(screen, "ai-mcp");
        Edit(screen, "tracker");
        screen.WaitForAssertion(() => Assert.Contains("TRACKER_TOKEN=…", screen.Find("[role='dialog']").TextContent));
        Assert.Contains("Authorization: …", screen.Find("[role='dialog']").TextContent);
        seen.Add(screen.Markup);

        Assert.All(seen, markup => Assert.All(secrets, secret => Assert.DoesNotContain(secret, markup)));
        // Nor the handles the copy holds in their place: a name for a secret is not for the page either.
        Assert.All(seen, markup => Assert.DoesNotContain("kept:", markup));
    }

    /// <summary>
    /// Where a credential could be typed into an address or a command line - kept in the clear, shown on the page, named in
    /// the audit trail - the page says where it goes instead.
    /// </summary>
    [Fact]
    public void Where_an_address_could_carry_a_credential_the_page_says_where_it_goes()
    {
        StoreSecrets();
        var screen = Screen("ada", "web");
        Assert.Contains("No key or password in the address", screen.Find(".settings-pane .settings-credentials").TextContent);
        // Said over the field's name - the "?" beside it, its words the tooltip - not as a line under the field: a line
        // under every field pushed the form down a screen for words most people read once.
        Assert.Contains("No key or password in the address",
            screen.Find(".settings-pane .hint-mark.settings-credentials summary").GetAttribute("title"));
        Assert.Empty(screen.FindAll("p.settings-credentials"));

        Go(screen, "ai-providers");
        Edit(screen, "cloud");
        screen.WaitForAssertion(() => Assert.Contains("A key goes in API key, any other credential in a header below.",
            screen.Find("[role='dialog'] .settings-credentials").TextContent));
        CancelDialog(screen);

        Go(screen, "ai-mcp");
        Edit(screen, "tracker");
        screen.WaitForAssertion(() => Assert.Contains("A credential goes in a header below.",
            screen.Find("[role='dialog'] .settings-credentials").TextContent));
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
    /// A provider added in the window's editor, its key typed there, is in the settings when the editor's Save is pressed -
    /// written at once, not left in the page's copy for a Save below the sections; encrypted on disk, and said as kept,
    /// never shown, when they are opened again.
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
        screen.FindAll("[role='dialog'] button").Single(b => b.TextContent.Trim() == "Save").Click();

        screen.WaitForAssertion(() => Assert.Contains(screen.FindAll(".settings-card .card-title"), t => t.TextContent.Trim() == "box"));
        screen.WaitForAssertion(() => Assert.StartsWith("Saved.", screen.Find(".settings-said").TextContent.Trim()));
        var box = AppSettings.Load(Engine.SettingsFile).Providers.Single(p => p.Id == "box");
        Assert.Equal("sk-typed-here", box.ApiKey);
        Assert.Equal(["qwen"], box.Models);
        Assert.DoesNotContain("sk-typed-here", File.ReadAllText(Engine.SettingsFile));
        Assert.DoesNotContain("sk-typed-here", screen.Markup);
    }

    /// <summary>
    /// Templates are files, saved by their editor at once, and the section says so; it lists them and opens the Templates
    /// screen's editor over itself rather than being a second one.
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
            a => Assert.StartsWith("settings/ai-templates/", a.GetAttribute("href")));
        Assert.Equal(Enactive.Server.Components.Links.SettingsTemplate(id, Enactive.Server.Components.Links.NewTemplateId),
            screen.FindAll("a.btn").Single(a => a.TextContent.Trim() == "New template").GetAttribute("href"));
    }

    /// <summary>
    /// Each template's card has the window's row icons (SettingsWindow.axaml): Edit and Duplicate lead to the editor's
    /// addresses, and Remove - not offered on a built-in - asks in the window's words and is done by the service as the
    /// person, audited. The cards had none of them.
    /// </summary>
    [Fact]
    public void A_template_s_card_offers_edit_duplicate_and_remove_as_the_window_s()
    {
        var workspace = Path.Combine(_roots, "project");
        Directory.CreateDirectory(workspace);
        Engine.Workspaces.Touch(workspace);
        var id = Services.GetRequiredService<WorkspaceIds>().TryIdOf(workspace)!.Value;
        var (saved, said) = Services.GetRequiredService<ServerLibrary>().SaveTemplate(
            Enactive.Server.Accounts.ServerAuth.PrincipalOf(Users.Find("alice")!), workspace,
            """{ "id": "digest", "name": "Digest", "goal": "Write the digest." }""", Enactive.Workspace.TemplateScope.Workspace);
        Assert.True(saved, said);

        var screen = Screen("ada", "ai-templates");
        var card = Card(screen, "Digest");
        Assert.Equal(Enactive.Server.Components.Links.SettingsTemplate(id, "digest"), card.QuerySelector("a.template-edit")!.GetAttribute("href"));
        Assert.Equal(Enactive.Server.Components.Links.SettingsTemplate(id, "digest", copy: true),
            card.QuerySelector("a.template-duplicate")!.GetAttribute("href"));
        Assert.Equal("Delete this template", card.QuerySelector("button.template-remove")!.GetAttribute("title"));
        // A built-in is customised, never removed: Edit and Duplicate only.
        var builtin = screen.FindAll(".settings-card").First(c => c.TextContent.Contains("Built-in"));
        Assert.NotNull(builtin.QuerySelector("a.template-edit"));
        Assert.Null(builtin.QuerySelector("button.template-remove"));

        screen.InvokeAsync(() => Card(screen, "Digest").QuerySelector("button.template-remove")!.Click()).GetAwaiter().GetResult();
        Assert.Equal("Delete “Digest”?", screen.Find("[role=dialog] .dialog-title").TextContent.Trim());
        Assert.Contains("Cancel on this page does not undo it.", screen.Find("[role=dialog] .body").TextContent);
        screen.InvokeAsync(() => screen.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Remove").Click())
            .GetAwaiter().GetResult();

        screen.WaitForAssertion(() => Assert.DoesNotContain(screen.FindAll(".settings-card .card-title"), t => t.TextContent.Trim() == "Digest"));
        Assert.StartsWith("Deleted 'digest'", screen.Find(".settings-templates-said").TextContent.Trim());
        Assert.DoesNotContain(new Enactive.Workspace.TemplateStore(workspace).Inventory(),
            t => t.Template.Id == "digest" && t.Scope == Enactive.Workspace.TemplateScope.Workspace);
        Assert.Contains(Users.AuditTrail(), e => e is { Account: "ada", Action: "template deleted" });
    }
    /// <summary>
    /// The editor opened from Settings › Templates opens over Settings - its menu beside it, the section behind it - and
    /// Cancel comes back to the section. At the library's address the settings' menu went away under the editor and the
    /// person was left on the Templates screen when it closed.
    /// </summary>
    [Fact]
    public void A_template_edited_from_settings_is_edited_over_settings_and_cancel_comes_back_there()
    {
        var (_, id) = WorkspaceWithDigest();

        var screen = SettingsAt("ada", Enactive.Server.Components.Links.SettingsTemplate(id, "digest"), "digest");

        screen.WaitForAssertion(() => Assert.NotNull(screen.Find("[role='dialog'] .template-editor")));
        Assert.Equal("digest", screen.Find("[role='dialog'] #t-id").GetAttribute("value"));
        // Behind it, the settings: the menu on Templates, and the section's list.
        Assert.Equal("Templates", screen.Find(".settings-nav-item.on").TextContent.Trim());
        Assert.NotNull(Card(screen, "Digest"));

        screen.InvokeAsync(() => screen.Find("[role='dialog'] .template-editor-actions").QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "Cancel").Click()).GetAwaiter().GetResult();

        screen.WaitForAssertion(() => Assert.EndsWith("/settings/ai-templates", Navigation.Uri));
        // The router draws the page again at that address - the same page, so the settings being edited are kept.
        screen.Render(p => p.Add(s => s.Section, "ai-templates").Add(s => s.Edit, null));
        Assert.Empty(screen.FindAll(".template-editor"));
        Assert.Equal("Templates", screen.Find(".settings-nav-item.on").TextContent.Trim());
        Assert.NotNull(Card(screen, "Digest"));
    }

    /// <summary>
    /// Saved from the editor over Settings, the template is in the section's list - read again from the files - and the
    /// section says what the save did; the person is on Settings › Templates still.
    /// </summary>
    [Fact]
    public void A_template_saved_from_settings_is_listed_there_and_said()
    {
        var (_, id) = WorkspaceWithDigest();

        var screen = SettingsAt("ada", Enactive.Server.Components.Links.SettingsTemplate(id, "digest"), "digest");
        screen.WaitForAssertion(() => Assert.NotNull(screen.Find("[role='dialog'] #t-name")));
        screen.InvokeAsync(() => screen.Find("[role='dialog'] #t-name").Input("Weekly digest")).GetAwaiter().GetResult();
        screen.InvokeAsync(() => screen.Find("[role='dialog'] .template-editor-actions").QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "Save").Click()).GetAwaiter().GetResult();

        screen.WaitForAssertion(() => Assert.EndsWith("/settings/ai-templates", Navigation.Uri));
        screen.Render(p => p.Add(s => s.Section, "ai-templates").Add(s => s.Edit, null));
        Assert.Empty(screen.FindAll(".template-editor"));
        screen.WaitForAssertion(() => Assert.NotNull(Card(screen, "Weekly digest")));
        Assert.StartsWith("Saved 'Weekly digest'", screen.Find(".settings-templates-said").TextContent.Trim());
        Assert.Equal("Templates", screen.Find(".settings-nav-item.on").TextContent.Trim());
    }

    private NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    /// <summary>A workspace of the service with a template of its own, "digest", saved as alice; its folder and id.</summary>
    private (string Folder, Guid Id) WorkspaceWithDigest()
    {
        var workspace = Path.Combine(_roots, "project");
        Directory.CreateDirectory(workspace);
        Engine.Workspaces.Touch(workspace);
        var (saved, said) = Services.GetRequiredService<ServerLibrary>().SaveTemplate(
            Enactive.Server.Accounts.ServerAuth.PrincipalOf(Users.Find("alice")!), workspace,
            """{ "id": "digest", "name": "Digest", "goal": "Write the digest." }""", Enactive.Workspace.TemplateScope.Workspace);
        Assert.True(saved, said);
        return (workspace, Services.GetRequiredService<WorkspaceIds>().TryIdOf(workspace)!.Value);
    }

    /// <summary>Settings › Templates at the editor's address, drawn as the router draws it: the section and the template.</summary>
    private IRenderedComponent<SettingsScreen> SettingsAt(string who, string address, string edit)
    {
        SignIn(who);
        Navigation.NavigateTo("/" + address);
        var screen = Render<SettingsScreen>(p => p.Add(s => s.Section, "ai-templates").Add(s => s.Edit, edit));
        screen.WaitForAssertion(() => Assert.True(screen.FindAll(".settings-pane, .said").Count > 0));
        return screen;
    }
}
