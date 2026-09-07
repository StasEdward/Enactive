namespace Enactive.Engine.Tests;

using Enactive.Core.Templates;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Where a template came from, and what it replaced.
///
/// <para><c>Load</c> answers "what can I run" and deliberately loses this: it merges three sources
/// into one list by id. That is right for running and wrong for managing — shadowing is the whole
/// point of the scopes, and in a merged list it is completely invisible. Somebody edits the global
/// copy of a template their workspace overrides, watches the change do nothing, and has no way to
/// find out why. The Templates section in Settings shows it, so the store has to know it.</para>
/// </summary>
public sealed class TemplateInventoryTests : IDisposable
{
    private readonly string _root;
    private readonly string _global;

    public TemplateInventoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
        _global = Path.Combine(_root, "global-templates");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>Never the real %APPDATA%: see TaskTemplateTests.</summary>
    private TemplateStore Store() => new(_root, _global);

    private static TaskTemplate Sample(string id, string name = "Sample")
        => new(Id: id, Name: name, Goal: "Do the thing.");

    private StoredTemplate Entry(string id)
        => Store().Inventory().Single(e => string.Equals(e.Template.Id, id, StringComparison.OrdinalIgnoreCase));

    // ── provenance ──────────────────────────────────────────────────────────

    [Fact]
    public void A_shipped_template_says_it_is_built_in()
    {
        var entry = Entry("code-review");

        Assert.Equal(TemplateOrigin.Builtin, entry.Origin);
        Assert.True(entry.IsBuiltin);
        Assert.Null(entry.Scope);        // a built-in lives in neither folder
        Assert.Null(entry.Shadows);
    }

    [Fact]
    public void A_template_saved_globally_says_so()
    {
        Store().Save(Sample("my-check"), TemplateScope.Global);

        var entry = Entry("my-check");

        Assert.Equal(TemplateOrigin.Global, entry.Origin);
        Assert.Equal(TemplateScope.Global, entry.Scope);
        Assert.Null(entry.Shadows);
    }

    [Fact]
    public void A_template_saved_in_the_workspace_says_so()
    {
        Store().Save(Sample("my-check"), TemplateScope.Workspace);

        Assert.Equal(TemplateOrigin.Workspace, Entry("my-check").Origin);
        Assert.Equal(TemplateScope.Workspace, Entry("my-check").Scope);
    }

    // ── shadowing, which is the reason this exists ──────────────────────────

    /// <summary>
    /// The customisation path: a file under a built-in's id replaces it. The list has to say that,
    /// or "Code Review" behaving differently from the documented one is unexplainable.
    /// </summary>
    [Fact]
    public void A_file_that_replaces_a_built_in_says_which_one_it_replaced()
    {
        Store().Save(Sample("code-review", "Code Review (ours)"), TemplateScope.Global);

        var entry = Entry("code-review");

        Assert.Equal(TemplateOrigin.Global, entry.Origin);
        Assert.Equal(TemplateOrigin.Builtin, entry.Shadows);
        Assert.Equal("Code Review (ours)", entry.Template.Name);
    }

    /// <summary>
    /// And a project's own copy replaces the global one — "generic template, project-specific build
    /// command", which is what the two scopes were built for.
    /// </summary>
    [Fact]
    public void A_workspace_copy_replaces_the_global_one_and_names_it()
    {
        var store = Store();
        store.Save(Sample("release-check", "Ours everywhere"), TemplateScope.Global);
        store.Save(Sample("release-check", "Ours here"), TemplateScope.Workspace);

        var entry = Entry("release-check");

        Assert.Equal(TemplateOrigin.Workspace, entry.Origin);
        Assert.Equal(TemplateOrigin.Global, entry.Shadows);
        Assert.Equal("Ours here", entry.Template.Name);
    }

    /// <summary>Only one row per id, whatever the depth of the stack. The library is what runs.</summary>
    [Fact]
    public void A_template_appears_once_however_many_copies_exist()
    {
        var store = Store();
        store.Save(Sample("code-review", "global"), TemplateScope.Global);
        store.Save(Sample("code-review", "workspace"), TemplateScope.Workspace);

        Assert.Single(store.Inventory(), e => e.Template.Id == "code-review");
    }

    /// <summary>
    /// Deleting the file brings the built-in back. That is what makes customising one safe, and it
    /// is the behaviour the Reset action in Settings depends on.
    /// </summary>
    [Fact]
    public void Deleting_a_replacement_brings_the_built_in_back()
    {
        var store = Store();
        store.Save(Sample("code-review", "Ours"), TemplateScope.Global);
        Assert.Equal("Ours", Entry("code-review").Template.Name);

        store.Delete("code-review", TemplateScope.Global);

        var entry = Entry("code-review");
        Assert.True(entry.IsBuiltin);
        Assert.Equal("Code Review", entry.Template.Name);
    }

    // ── the two views agree ─────────────────────────────────────────────────

    /// <summary>
    /// Load is defined in terms of Inventory, so they cannot disagree about what the library holds -
    /// two independent merges of the same three folders is exactly the sort of pair that drifts.
    /// </summary>
    [Fact]
    public void What_you_can_run_is_what_the_inventory_lists()
    {
        var store = Store();
        store.Save(Sample("mine"), TemplateScope.Global);
        store.Save(Sample("code-review", "Ours"), TemplateScope.Workspace);

        Assert.Equal(
            store.Load().Select(t => t.Id),
            store.Inventory().Select(e => e.Template.Id));
    }

    [Fact]
    public void A_store_with_no_workspace_still_lists_the_global_ones()
    {
        var store = new TemplateStore(workspaceRoot: null, globalFolder: _global);
        store.Save(Sample("mine"), TemplateScope.Global);

        Assert.Contains(store.Inventory(), e => e.Template.Id == "mine" && e.Origin == TemplateOrigin.Global);
        Assert.All(store.Inventory(), e => Assert.NotEqual(TemplateOrigin.Workspace, e.Origin));
    }
}
