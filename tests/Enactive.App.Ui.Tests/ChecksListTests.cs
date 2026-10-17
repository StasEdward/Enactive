namespace Enactive.App.Ui.Tests;

using Enactive.Application;
using Enactive.Core.Checks;
using Enactive.Workspace;

/// <summary>
/// The window's Checks lists (Plan Phase 3.5): the global one in Settings and a workspace's from its card - the merged checks
/// with where each comes from, Add, Edit, Delete, Turn on/off and Raise to blocking straight to the files, the store's
/// problems beside their files - and nothing changed in a workspace while the window's run works in it, because that run
/// puts the checks back as they were when it started.
/// </summary>
public sealed class ChecksListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-checks-list", Guid.NewGuid().ToString("N"));
    private readonly ForegroundRunSlot _foreground = new();

    private string Workspace => Path.Combine(_root, "project");
    private string Global => Path.Combine(_root, "global");
    private string WorkspaceChecks => Path.Combine(Workspace, ".enactive", "checks");

    public ChecksListTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Global);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private ChecksViewModel List(CheckScope scope) => DesktopChecks.For(Workspace, scope, _foreground, Global);

    private static void Fill(CheckDraft draft, string id, string command, string matches, string fireOn = "Step", string severity = "Advisory")
    {
        draft.Id = id;
        draft.Command = command;
        draft.Matches = matches;
        draft.FireOn = fireOn;
        draft.Severity = severity;
    }

    [Fact]
    public void A_check_added_is_written_to_the_workspace_and_listed_as_its_own()
    {
        var list = List(CheckScope.Workspace);

        list.AddCommand.Execute(null);
        Fill(list.Draft!, "lint", "npm run lint", "src/**/*.ts");
        list.Draft!.SaveCommand.Execute(null);

        Assert.False(list.IsEditing);
        Assert.True(File.Exists(Path.Combine(WorkspaceChecks, "lint.json")));
        var row = Assert.Single(list.Checks);
        Assert.Equal(("lint", "This workspace"), (row.Id, row.Origin));
        Assert.Equal("at each step's end · advisory · on src/**/*.ts · 120 s", row.Meta);
        Assert.True(row.CanEdit && row.CanDelete && row.CanToggle);
    }

    [Fact]
    public void What_the_store_refuses_is_said_on_the_form_and_nothing_is_written()
    {
        var list = List(CheckScope.Workspace);

        list.AddCommand.Execute(null);
        // A step check with a command must say which paths it is about.
        Fill(list.Draft!, "lint", "npm run lint", "");
        list.Draft!.SaveCommand.Execute(null);

        Assert.True(list.IsEditing);
        Assert.Contains("must say which paths it is about", list.Draft!.Problems);
        Assert.False(Directory.Exists(WorkspaceChecks) && Directory.EnumerateFiles(WorkspaceChecks).Any());
    }

    [Fact]
    public void A_check_is_edited_turned_off_and_deleted_through_its_file()
    {
        var store = new CheckStore(Workspace, Global);
        store.Save(new CheckDefinition("lint", null, "npm run lint", ["src/**"], CheckFire.Step, CheckSeverity.Advisory, 60, null),
            CheckScope.Workspace);
        var list = List(CheckScope.Workspace);

        list.Checks.Single().EditCommand.Execute(null);
        Assert.Equal("npm run lint", list.Draft!.Command);
        list.Draft.Severity = "Blocking";
        list.Draft.SaveCommand.Execute(null);
        Assert.Equal(CheckSeverity.Blocking, store.Inventory().Checks.Single().Definition.Severity);

        list.Checks.Single().ToggleCommand.Execute(null);
        Assert.False(store.Inventory().Checks.Single().Definition.Enabled);
        Assert.Contains("off", list.Checks.Single().Meta);
        Assert.Equal("Turn on", list.Checks.Single().ToggleText);

        list.Checks.Single().RemoveCommand.Execute(null);
        Assert.Empty(store.Inventory().Checks);
        Assert.Empty(list.Checks);
        Assert.False(File.Exists(Path.Combine(WorkspaceChecks, "lint.json")));
    }

    [Fact]
    public void A_skipped_manifest_is_shown_beside_its_file()
    {
        Directory.CreateDirectory(WorkspaceChecks);
        File.WriteAllText(Path.Combine(WorkspaceChecks, "lint.json"), "{ not json");

        var list = List(CheckScope.Workspace);

        var note = Assert.Single(list.Files);
        Assert.True(note.IsProblem);
        Assert.StartsWith("lint.json (this workspace): skipped - It is not a check manifest as JSON", note.Text);
        Assert.Empty(list.Checks);
    }

    /// <summary>Where no ecosystem applies there are no implicit build and test rows - the store's reason is said instead.</summary>
    [Fact]
    public void Where_nothing_builds_the_reason_is_said_instead_of_empty_rows()
    {
        var list = List(CheckScope.Workspace);

        Assert.Empty(list.Checks);
        Assert.True(list.HasNoImplicit);
        Assert.Contains("not one the engine can build or test itself", list.NoImplicit);
    }

    /// <summary>
    /// The implicit build is offered "Raise to blocking", not a command: saved, it is a manifest of the same id without one,
    /// which changes how the engine's own build decides and keeps its command.
    /// </summary>
    [Fact]
    public void The_implicit_build_is_raised_to_blocking_without_a_command()
    {
        File.WriteAllText(Path.Combine(Workspace, "app.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var list = List(CheckScope.Workspace);
        var build = list.Checks.Single(r => r.Id == "build");
        Assert.Equal(("Implicit", "Raise to blocking"), (build.Origin, build.EditText));
        Assert.False(build.CanDelete);
        // It is the engine's own build check, which runs whatever a manifest says: there is nothing to turn off.
        Assert.False(build.CanToggle);

        build.EditCommand.Execute(null);
        Assert.False(list.Draft!.HasCommand);
        Assert.Equal("Blocking", list.Draft.Severity);
        list.Draft.SaveCommand.Execute(null);

        var raised = list.Checks.Single(r => r.Id == "build");
        Assert.Equal(("This workspace", "raises the implicit one", "Reset"), (raised.Origin, raised.Shadows, raised.RemoveText));
        Assert.DoesNotContain("\"command\"", File.ReadAllText(Path.Combine(WorkspaceChecks, "build.json")));
        Assert.Equal(CheckSeverity.Blocking, new CheckStore(Workspace, Global, KnownEcosystems.All).Inventory().Checks
            .Single(c => c.Id == "build").Definition.Severity);
    }

    [Fact]
    public void The_global_list_shows_the_workspace_s_checks_but_changes_only_its_own()
    {
        new CheckStore(Workspace, Global).Save(
            new CheckDefinition("audit", null, "npm audit", [], CheckFire.Run, CheckSeverity.Advisory, 60, null), CheckScope.Workspace);
        var list = List(CheckScope.Global);

        var audit = list.Checks.Single();
        Assert.Equal("This workspace", audit.Origin);
        Assert.False(audit.CanEdit || audit.CanDelete || audit.CanToggle);

        list.AddCommand.Execute(null);
        Fill(list.Draft!, "spell", "cspell .", "", fireOn: "Run");
        list.Draft!.SaveCommand.Execute(null);
        Assert.True(File.Exists(Path.Combine(Global, "spell.json")));
        Assert.Equal("Global", list.Checks.Single(r => r.Id == "spell").Origin);
    }

    /// <summary>
    /// While the window's run works in the workspace, its checks are not changed: the run puts them back at its end as they
    /// were when it started, and the change would be undone without a word. The list says why; a global check is the next
    /// run's and may be changed.
    /// </summary>
    [Fact]
    public void Nothing_is_changed_in_a_workspace_while_its_run_works()
    {
        var running = _foreground.TryStart(Workspace)!;
        var list = List(CheckScope.Workspace);
        Assert.Equal(CheckChangeWords.RunActive, list.NotNow);

        list.AddCommand.Execute(null);
        Fill(list.Draft!, "lint", "npm run lint", "src/**");
        list.Draft!.SaveCommand.Execute(null);

        Assert.Equal(CheckChangeWords.RunActive, list.Draft!.Problems);
        Assert.False(File.Exists(Path.Combine(WorkspaceChecks, "lint.json")));

        var global = List(CheckScope.Global);
        Assert.False(global.HasNotNow);

        _foreground.Finish(running);
        list.Draft!.SaveCommand.Execute(null);
        Assert.True(File.Exists(Path.Combine(WorkspaceChecks, "lint.json")));
    }
}
