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

    private ChecksViewModel List(CheckScope scope) => DesktopChecks.For(Workspace, scope, Global);

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

    /// <summary>
    /// A run check runs at the run's end whatever changed: its row does not say "on &lt;globs&gt;" as if they narrowed it,
    /// and says the store's note that they are not used.
    /// </summary>
    [Fact]
    public void A_run_check_with_matches_is_not_shown_as_on_them()
    {
        new CheckStore(Workspace, Global).Save(new CheckDefinition("audit", null, "npm audit", ["src/**"], CheckFire.Run,
            CheckSeverity.Advisory, 60, null), CheckScope.Workspace);

        var row = Assert.Single(List(CheckScope.Workspace).Checks);

        Assert.Equal("at the run's end · advisory · 60 s", row.Meta);
        Assert.Contains("its matches are not used", row.Notes);
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
    /// While a run holds the workspace (WorkspaceLease - every run on every host takes it, the window's background runs, a
    /// schedule's and another program's too), its checks are not changed: the run puts them back at its end as they were
    /// when it started, and the change would be undone without a word. The list says why; a global check is the next run's
    /// and may be changed.
    /// </summary>
    [Fact]
    public void Nothing_is_changed_in_a_workspace_while_a_run_holds_it()
    {
        var lease = Enactive.Core.Storage.WorkspaceLease.Take(Workspace);
        var list = List(CheckScope.Workspace);
        Assert.Equal(CheckChangeWords.RunActive, list.NotNow);

        list.AddCommand.Execute(null);
        Fill(list.Draft!, "lint", "npm run lint", "src/**");
        list.Draft!.SaveCommand.Execute(null);

        Assert.Equal(CheckChangeWords.RunActive, list.Draft!.Problems);
        Assert.False(File.Exists(Path.Combine(WorkspaceChecks, "lint.json")));

        var global = List(CheckScope.Global);
        Assert.False(global.HasNotNow);

        lease.Dispose();
        list.Draft!.SaveCommand.Execute(null);
        Assert.True(File.Exists(Path.Combine(WorkspaceChecks, "lint.json")));
    }

    /// <summary>
    /// A workspace check that writes out the same command as the global one is a check of its own: edited, it keeps its command,
    /// and goes on running when the global one is deleted. The list once took it for a raise, saved it without its command, and
    /// it stopped running with the global check.
    /// </summary>
    [Fact]
    public void A_workspace_check_that_says_the_global_command_keeps_it()
    {
        const string lint = """{"id":"lint","command":"npm run lint","matches":["src/**"]}""";
        File.WriteAllText(Path.Combine(Global, "lint.json"), lint);
        Directory.CreateDirectory(WorkspaceChecks);
        File.WriteAllText(Path.Combine(WorkspaceChecks, "lint.json"), lint);
        var list = List(CheckScope.Workspace);
        var row = list.Checks.Single();
        Assert.Equal("replaces the global one", row.Shadows);

        row.EditCommand.Execute(null);
        Assert.True(list.Draft!.HasCommand);
        list.Draft.Severity = "Blocking";
        list.Draft.SaveCommand.Execute(null);

        File.Delete(Path.Combine(Global, "lint.json"));
        var kept = new CheckStore(Workspace, Global).Inventory().Checks.Single();
        Assert.Equal(("npm run lint", CheckSeverity.Blocking), (kept.Definition.Command, kept.Definition.Severity));
    }

    /// <summary>
    /// A global check turned off from the workspace's list says that, and nothing else: it still follows the global check when
    /// that one changes. Saved whole, it was a copy that stayed behind.
    /// </summary>
    [Fact]
    public void A_check_from_below_turned_off_here_says_only_that()
    {
        File.WriteAllText(Path.Combine(Global, "audit.json"), """{"id":"audit","command":"npm audit","fireOn":"Run"}""");
        var list = List(CheckScope.Workspace);

        list.Checks.Single().ToggleCommand.Execute(null);

        Assert.Equal("""{"id":"audit","enabled":false}""",
            System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(Path.Combine(WorkspaceChecks, "audit.json")), @"\s", ""));
        File.WriteAllText(Path.Combine(Global, "audit.json"), """{"id":"audit","command":"npm audit --omit=dev","fireOn":"Run"}""");
        list.Refresh();
        Assert.Equal("npm audit --omit=dev", list.Checks.Single().Command);
        Assert.Equal("raises the global one", list.Checks.Single().Shadows);
    }

    /// <summary>
    /// Only what the person changed is saved: a check whose manifest said no timeout, moved to fire on write, gets the write
    /// default - the form once carried its two-minute step timeout over, which is past what a write check may take.
    /// </summary>
    [Fact]
    public void A_timeout_left_alone_stays_unsaid_and_follows_when_the_check_fires()
    {
        Directory.CreateDirectory(WorkspaceChecks);
        File.WriteAllText(Path.Combine(WorkspaceChecks, "lint.json"), """{"id":"lint","command":"npm run lint","matches":["src/**"]}""");
        var list = List(CheckScope.Workspace);
        Assert.Contains("120 s", list.Checks.Single().Meta);

        list.Checks.Single().EditCommand.Execute(null);
        Assert.Equal("", list.Draft!.Timeout);
        list.Draft.FireOn = "Write";
        list.Draft.SaveCommand.Execute(null);

        var written = File.ReadAllText(Path.Combine(WorkspaceChecks, "lint.json"));
        Assert.DoesNotContain("timeoutSeconds", written);
        Assert.DoesNotContain("severity", written);
        Assert.Equal(CheckLimits.WriteTimeoutLimitSeconds, new CheckStore(Workspace, Global).Inventory().Checks.Single().Definition.TimeoutSeconds);
    }

    /// <summary>The question before a reset says Reset on its button - what pressing it does - not Delete.</summary>
    [Fact]
    public void A_reset_is_asked_with_reset()
    {
        File.WriteAllText(Path.Combine(Global, "audit.json"), """{"id":"audit","command":"npm audit","fireOn":"Run"}""");
        Directory.CreateDirectory(WorkspaceChecks);
        File.WriteAllText(Path.Combine(WorkspaceChecks, "audit.json"), """{"severity":"Blocking"}""");
        File.WriteAllText(Path.Combine(WorkspaceChecks, "lint.json"), """{"id":"lint","command":"npm run lint","fireOn":"Run"}""");
        var list = List(CheckScope.Workspace);
        var asked = new List<string>();
        list.ConfirmRequested += (_, _, confirm) =>
        {
            asked.Add(confirm);
            return Task.FromResult(false);
        };

        list.Checks.Single(r => r.Id == "audit").RemoveCommand.Execute(null);
        list.Checks.Single(r => r.Id == "lint").RemoveCommand.Execute(null);

        Assert.Equal(["Reset", "Delete"], asked);
    }

    /// <summary>Settings' global list is replaced when the window's workspace changes, and the window is told to draw the new one.</summary>
    [Fact]
    public void Settings_draws_the_checks_list_it_is_given_again()
    {
        var settings = new SettingsViewModel(new Enactive.Settings.AppSettings(), _ => { }, new NoStartupEntry(), editsFilesHere: false);
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.Checks = List(CheckScope.Global);
        settings.Checks = DesktopChecks.For(null, CheckScope.Global, Global);

        Assert.Equal(2, changed.Count(p => p == nameof(SettingsViewModel.Checks)));
    }
}
