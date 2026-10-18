namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Core.Context;
using Enactive.Server.Accounts;
using Enactive.Server.Components.Pages;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Templates as the window's: the library (TemplatesWindow.axaml) - the list, 300 px, "What it will be asked to do", the
/// form a template asks for, Run - and the editor (TemplateEditWindow.axaml) - its Form / JSON switch over one template,
/// Save. Both bind the window's own view models (Enactive.Application); every action is ServerLibrary's to decide: a Viewer
/// reads, an Operator edits, deletes and runs. Only a workspace's own library is written here: the global one is the real
/// %APPDATA% of whoever runs the tests.
/// </summary>
public sealed class TemplatesTests : ComponentHost
{
    private const string Digest = """
        { "id": "digest", "name": "Digest", "description": "The week, in a page.", "goal": "Write the digest of {topic}.",
          "parameters": [ { "id": "topic", "name": "Topic", "type": "Text", "required": true } ] }
        """;

    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-templates", Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public TemplatesTests()
    {
        // A run from the library starts only under the service's roots.
        Services.AddSingleton(new WorkspaceRoots([_folders]));
        _root = Path.Combine(_folders, "project");
        Directory.CreateDirectory(_root);
        Services.GetRequiredService<ServerEngine>().Workspaces.Touch(_root);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Enactive.Tests.SqlitePools.ClearUnder(_folders);
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
        }
    }

    private Guid WorkspaceId => WorkspaceInfo.For(_root).Id;

    private NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    private void Saved(string json = Digest)
    {
        var (saved, said) = Services.GetRequiredService<ServerLibrary>()
            .SaveTemplate(ServerAuth.PrincipalOf(Users.Find("alice")!), _root, json, TemplateScope.Workspace);
        Assert.True(saved, said);
    }

    private string? FileOf(string id)
    {
        var path = Path.Combine(_root, ".enactive", "templates", id + ".json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private IRenderedComponent<TemplatesScreen> Library(string who, string? template = null)
    {
        SignIn(who);
        Navigation.NavigateTo($"/templates?ws={WorkspaceId}" + (template is null ? "" : "&t=" + template));
        var screen = Render<TemplatesScreen>();
        screen.WaitForAssertion(() => Assert.NotEmpty(screen.FindAll(".templates-list .template-card")));
        return screen;
    }

    private IRenderedComponent<TemplateEditor> Editor(string who, string id)
    {
        SignIn(who);
        Navigation.NavigateTo($"/templates/{id}?ws={WorkspaceId}");
        var editor = Render<TemplateEditor>(p => p.Add(e => e.Id, id));
        editor.WaitForAssertion(() => Assert.NotEmpty(editor.FindAll(".template-editor, .said")));
        return editor;
    }

    private static string[] Offered(IRenderedComponent<TemplatesScreen> screen)
        => screen.FindAll("button, a").Select(b => (b.GetAttribute("aria-label") ?? b.TextContent).Trim()).ToArray();

    private static void Choose(IRenderedComponent<TemplatesScreen> screen, string name)
        => screen.FindAll(".templates-list .template-card").First(c => c.QuerySelector(".card-title")!.TextContent.Trim() == name).Click();

    // ── the editor ──────────────────────────────────────────────────────────

    /// <summary>
    /// The editor opens in a window over the library, as the window's opens over its Settings: the library stays on the
    /// template being edited behind it - a page of its own took the library away while a template was being written.
    /// </summary>
    [Fact]
    public void The_editor_opens_in_a_window_over_the_library_on_the_template_it_edits()
    {
        Saved();
        SignIn("alice");
        Navigation.NavigateTo($"/templates/digest?ws={WorkspaceId}");
        var screen = Render<TemplatesScreen>(p => p.Add(s => s.Edit, "digest"));

        screen.WaitForAssertion(() => Assert.NotNull(screen.Find("[role='dialog'] .template-editor")));
        Assert.Equal("digest", screen.Find("[role='dialog'] #t-id").GetAttribute("value"));
        // Behind it, the library - on the template being edited.
        Assert.NotEmpty(screen.FindAll(".templates-list .template-card"));
        Assert.Contains("Write the digest of", screen.Find(".template-goal").TextContent);
    }

    /// <summary>
    /// Opened over the library, the editor's Cancel comes back to the library on the template it was opened on - not to
    /// Settings, which opens the same editor over itself (TemplatesSection).
    /// </summary>
    [Fact]
    public void Cancel_in_the_editor_over_the_library_comes_back_to_the_library()
    {
        Saved();
        SignIn("alice");
        Navigation.NavigateTo($"/templates/digest?ws={WorkspaceId}");
        var screen = Render<TemplatesScreen>(p => p.Add(s => s.Edit, "digest"));
        screen.WaitForAssertion(() => Assert.NotNull(screen.Find("[role='dialog'] .template-editor")));

        screen.InvokeAsync(() => screen.Find("[role='dialog'] .template-editor-actions").QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "Cancel").Click()).GetAwaiter().GetResult();

        screen.WaitForAssertion(() => Assert.EndsWith($"/templates?ws={WorkspaceId}&t=digest", Navigation.Uri));
    }

    /// <summary>A Viewer at an editor's address reads the template in the library; no editor is offered.</summary>
    [Fact]
    public void A_viewer_at_an_editor_s_address_reads_the_template_and_is_offered_no_editor()
    {
        Saved();
        SignIn("vera");
        Navigation.NavigateTo($"/templates/digest?ws={WorkspaceId}");
        var screen = Render<TemplatesScreen>(p => p.Add(s => s.Edit, "digest"));

        screen.WaitForAssertion(() => Assert.NotEmpty(screen.FindAll(".templates-list .template-card")));
        Assert.Empty(screen.FindAll("[role='dialog']"));
        Assert.Empty(screen.FindAll(".template-editor"));
    }


    /// <summary>
    /// The form and the JSON are one template (TemplateEditViewModel): written on the form, read on the JSON tab, saved -
    /// and what is in the workspace's library is what the JSON tab showed, in the store's own format. Saving goes back to
    /// the library on the saved template, with what the service said.
    /// </summary>
    [Fact]
    public void The_form_saves_what_the_json_tab_shows()
    {
        var editor = Editor("alice", "New");

        editor.Find("#t-id").Input("digest");
        editor.Find("#t-name").Input("Digest");
        editor.Find("#t-goal").Input("Write the digest of {topic}.");
        editor.FindAll("button").Single(b => b.TextContent.Trim() == "Add parameter").Click();
        editor.Find(".param-row input[aria-label='id']").Input("topic");
        editor.Find(".param-row input[aria-label='Label shown to you']").Input("Topic");
        editor.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "JSON").Click();
        var shown = editor.Find("textarea.template-json").GetAttribute("value") ?? editor.Find("textarea.template-json").TextContent;

        // Into the workspace's library, chosen: a new template is offered the global one first, as the window offers it, and that is
        // the real %APPDATA% of whoever runs the tests.
        editor.Find("select[aria-label='Kept in']").Change("Workspace");
        editor.FindAll("button").Single(b => b.TextContent.Trim() == "Save").Click();

        editor.WaitForAssertion(() => Assert.NotNull(FileOf("digest")));
        Assert.Equal(TemplateStore.ToJson(TemplateStore.FromJson(shown, out _)!), FileOf("digest"));
        Assert.Contains("\"topic\"", FileOf("digest"));
        // What the save said goes to the library with the navigation, not in its address (Links.Saying).
        Assert.EndsWith($"/templates?ws={WorkspaceId}&t=digest", Navigation.Uri);
        Assert.Equal("Saved 'Digest' to the workspace's library.", Navigation.HistoryEntryState);
        var library = Render<TemplatesScreen>();
        library.WaitForAssertion(() => Assert.Equal("Saved 'Digest' to the workspace's library.", library.Find("p.said").TextContent.Trim()));
    }

    /// <summary>
    /// What the library says comes only from the service's own pages, never from its address: "?said=" put any words a link
    /// liked in the service's voice, and they outlived signing in, carried in the return address.
    /// </summary>
    [Fact]
    public void An_address_cannot_make_the_library_say_anything()
    {
        Saved();
        SignIn("alice");
        Navigation.NavigateTo($"/templates?ws={WorkspaceId}&said={Uri.EscapeDataString("Your session has ended. Sign in again at evil.example.")}");
        var library = Render<TemplatesScreen>();

        library.WaitForAssertion(() => Assert.Contains("Digest", library.Markup));
        Assert.DoesNotContain("evil.example", library.Markup);
    }

    /// <summary>
    /// The validator the store uses says what is wrong, by field, before anything is written - and the page stays where it
    /// is, with what was typed, so it can be put right.
    /// </summary>
    [Fact]
    public void A_template_that_does_not_validate_is_not_saved_and_says_why()
    {
        var editor = Editor("alice", "New");
        var at = Navigation.Uri;

        editor.Find("#t-id").Input("Release Check");
        editor.Find("#t-name").Input("Release Check");
        // Into the workspace's library, chosen: a new template is offered the global one first, as the window offers it, and that is
        // the real %APPDATA% of whoever runs the tests.
        editor.Find("select[aria-label='Kept in']").Change("Workspace");
        editor.FindAll("button").Single(b => b.TextContent.Trim() == "Save").Click();

        var status = editor.Find(".template-status");
        Assert.Equal("alert", status.GetAttribute("role"));
        Assert.Contains("must be a slug", status.TextContent);
        Assert.Contains("Goal", status.TextContent);
        Assert.False(Directory.Exists(Path.Combine(_root, ".enactive", "templates")));
        Assert.Equal(at, Navigation.Uri);
        Assert.Equal("Release Check", editor.Find("#t-id").GetAttribute("value"));
    }

    /// <summary>JSON that will not parse keeps its tab and changes nothing, in the window's words.</summary>
    [Fact]
    public void Json_that_does_not_parse_keeps_its_tab()
    {
        Saved();
        var editor = Editor("alice", "digest");

        editor.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "JSON").Click();
        editor.Find("textarea.template-json").Input("{ not json");
        editor.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Form").Click();

        Assert.Equal("true", editor.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "JSON").GetAttribute("aria-selected"));
        Assert.StartsWith("That JSON could not be read", editor.Find(".template-status").TextContent.Trim());
    }

    /// <summary>An existing template keeps its id (the window's rule) and is offered its own library first.</summary>
    [Fact]
    public void An_existing_template_keeps_its_id_and_its_library()
    {
        Saved();
        var editor = Editor("alice", "digest");

        Assert.True(editor.Find("#t-id").HasAttribute("disabled"));
        Assert.Equal("Digest", editor.Find("#t-name").GetAttribute("value"));
        Assert.Equal("Workspace", editor.Find("select[aria-label='Kept in']").GetAttribute("value"));
    }

    /// <summary>The editor is an Operator's: a Viewer at its address is told why and offered no Save.</summary>
    [Fact]
    public void A_viewer_at_the_editor_s_address_is_offered_no_save()
    {
        Saved();
        var editor = Editor("vera", "digest");

        Assert.Contains("role does not include", editor.Find(".said").TextContent);
        Assert.DoesNotContain(editor.FindAll("button"), b => b.TextContent.Trim() == "Save");
    }

    // ── the library ─────────────────────────────────────────────────────────

    /// <summary>
    /// A Viewer reads the library - the template, where it lives, what it will be asked to do and what it asks for - and is
    /// offered nothing that changes or runs: no New template, Edit, Duplicate, Delete or Run.
    /// </summary>
    [Fact]
    public void A_viewer_reads_templates_and_is_offered_no_edit_or_run()
    {
        Saved();
        var screen = Library("vera", "digest");

        var detail = screen.Find(".templates-detail");
        Assert.Contains("What it will be asked to do", detail.TextContent);
        Assert.Equal("Write the digest of {topic}.", detail.QuerySelector(".template-goal")!.TextContent.Trim());
        Assert.StartsWith("runs at Execute", detail.QuerySelector(".template-summary")!.TextContent.Trim());
        Assert.Contains("Topic", detail.QuerySelector(".template-inputs")!.TextContent);

        var offered = Offered(screen);
        foreach (var action in new[] { "New template", "Edit", "Copy and edit", "Duplicate", "Delete", "Reset", "Run" })
            Assert.DoesNotContain(action, offered);
    }

    /// <summary>The card's edge says where a template lives, and amber says which one is chosen (Palette.Template).</summary>
    [Fact]
    public void A_card_s_edge_says_where_it_lives_and_which_is_chosen()
    {
        Saved();
        Saved(Digest.Replace("\"digest\"", "\"weekly\"").Replace("\"Digest\"", "\"Weekly\""));
        var screen = Library("vera", "digest");

        string Edge(string name) => screen.FindAll(".templates-list .template-card")
            .First(c => c.QuerySelector(".card-title")!.TextContent.Trim() == name).ClassList.Single(c => c.StartsWith("edge-"));

        Assert.Equal("edge-selected", Edge("Digest"));
        Assert.Equal("edge-workspace", Edge("Weekly"));
        Choose(screen, "Weekly");
        Assert.Equal("edge-workspace", Edge("Digest"));
        Assert.Equal("edge-selected", Edge("Weekly"));
    }

    /// <summary>
    /// Run as the window's library runs one: greyed, saying why, until what the template asks for is filled in; then the
    /// template resolved with the answers, started as that saved task, and opened where the Runs screen shows a run.
    /// </summary>
    [Fact]
    public void An_operator_fills_in_what_it_asks_for_and_runs_it()
    {
        Saved();
        var screen = Library("alice", "digest");
        var run = screen.FindAll("button").Single(b => b.TextContent.Trim() == "Run");
        Assert.True(run.HasAttribute("disabled"));
        Assert.Contains("Topic", screen.Find(".template-problems").TextContent);

        screen.Find(".template-inputs input").Input("the inbox");
        screen.FindAll("button").Single(b => b.TextContent.Trim() == "Run").Click();

        screen.WaitForAssertion(() => Assert.Matches(@"/runs/[0-9a-f-]{36}$", Navigation.Uri));
        var started = Services.GetRequiredService<IRunControl>()
            .Find(ServerAuth.PrincipalOf(Users.Find("alice")!), Guid.Parse(Navigation.Uri[^36..]))!;
        Assert.Equal("Write the digest of the inbox.", started.Prompt);
        Assert.Equal("digest", started.Spec!.TemplateId);
    }

    /// <summary>
    /// An Operator is offered the window's Settings actions on a template of the workspace's - Edit, Duplicate, Delete - and
    /// Delete asks the window's question first, then deletes the file and says so.
    /// </summary>
    [Fact]
    public void An_operator_deletes_a_template_after_the_window_s_question()
    {
        Saved();
        var screen = Library("alice", "digest");
        Assert.Contains("Edit", Offered(screen));
        Assert.Contains("Duplicate", Offered(screen));
        Assert.Equal($"templates/digest?ws={WorkspaceId}",
            screen.FindAll("a").Single(a => a.TextContent.Trim() == "Edit").GetAttribute("href"));

        screen.FindAll("button").Single(b => b.TextContent.Trim() == "Delete").Click();
        var dialog = screen.Find("[role=dialog]");
        Assert.Equal("Delete “Digest”?", dialog.QuerySelector(".dialog-title")!.TextContent.Trim());
        Assert.Equal("The file is deleted from this workspace. This happens now.", dialog.QuerySelector(".body")!.TextContent.Trim());
        dialog.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Remove").Click();

        screen.WaitForAssertion(() => Assert.Null(FileOf("digest")));
        screen.WaitForAssertion(() => Assert.Contains("Deleted 'digest'", screen.Find(".said").TextContent));
        Assert.DoesNotContain(screen.FindAll(".templates-list .card-title"), t => t.TextContent.Trim() == "Digest");
    }
}
