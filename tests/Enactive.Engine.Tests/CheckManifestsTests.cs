namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Checks;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Plan Phase 3.1: declarative checks as files - one per check, in the workspace and next to settings.json - read with
/// a problem list instead of an exception, layered over the engine's own build and test of a .NET workspace.
/// </summary>
public sealed class CheckManifestsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
    private readonly string _global;

    public CheckManifestsTests()
    {
        Directory.CreateDirectory(_root);
        _global = _root + "-global-checks";
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_global, recursive: true); } catch { }
    }

    private string Local(string name) => Path.Combine(_root, ".enactive", "checks", name);

    private void WriteLocal(string name, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Local(name))!);
        File.WriteAllText(Local(name), json);
    }

    private void WriteGlobal(string name, string json)
    {
        Directory.CreateDirectory(_global);
        File.WriteAllText(Path.Combine(_global, name), json);
    }

    private CheckStore Store() => new(_root, _global, KnownEcosystems.All);

    /// <summary>A .NET workspace with a test project: what the engine's own build and test check recognise.</summary>
    private void MakeDotnet()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "App"));
        File.WriteAllText(Path.Combine(_root, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Directory.CreateDirectory(Path.Combine(_root, "tests", "App.Tests"));
        File.WriteAllText(Path.Combine(_root, "tests", "App.Tests", "App.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.0.0\" /></ItemGroup></Project>");
    }

    private const string Lint = """{"id":"lint","command":"npm run lint","matches":["src/**/*.ts"],"fireOn":"Step"}""";

    // ── validation ──────────────────────────────────────────────────────────

    [Fact]
    public void Two_files_of_one_scope_with_one_id_are_both_reported_and_neither_is_used()
    {
        WriteLocal("lint.json", Lint);
        WriteLocal("lint-again.json", """{"id":"lint","command":"eslint .","matches":["**/*.js"]}""");

        var inventory = Store().Inventory();

        Assert.DoesNotContain(inventory.Checks, c => c.Id == "lint");
        Assert.Equal(2, inventory.Problems.Count(p => p.Message.Contains("unique", StringComparison.Ordinal)));
        Assert.Contains(inventory.Problems, p => p.File == Local("lint.json") && p.Message.Contains("lint-again.json", StringComparison.Ordinal));
        Assert.All(inventory.Problems, p => Assert.Equal(CheckScope.Workspace, p.Scope));
    }

    [Theory]
    [InlineData("""{"id":"lint","command":"","fireOn":"Run"}""")]
    [InlineData("""{"id":"lint","fireOn":"Run"}""")]
    [InlineData("""{"id":"lint","command":"   ","fireOn":"Run"}""")]
    public void A_check_without_a_command_and_nothing_to_raise_is_reported_and_skipped(string json)
    {
        WriteLocal("lint.json", json);

        var inventory = Store().Inventory();

        Assert.Empty(inventory.Checks);
        var problem = Assert.Single(inventory.Problems);
        Assert.Equal(Local("lint.json"), problem.File);
        Assert.Contains("no command", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Step")]
    public void A_write_or_step_check_must_say_which_paths_it_is_about(string fire)
    {
        WriteLocal("lint.json", $$"""{"id":"lint","command":"npm run lint","fireOn":"{{fire}}","matches":[]}""");
        WriteLocal("spell.json", $$"""{"id":"spell","command":"cspell .","fireOn":"{{fire}}"}""");

        var inventory = Store().Inventory();

        Assert.Empty(inventory.Checks);
        Assert.Equal(2, inventory.Problems.Count);
        Assert.All(inventory.Problems, p => Assert.Contains("matches", p.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_check_may_cover_no_paths()
    {
        WriteLocal("audit.json", """{"id":"audit","command":"npm audit","fireOn":"Run"}""");

        var check = Assert.Single(Store().Inventory().Checks).Definition;

        Assert.Equal(CheckFire.Run, check.FireOn);
        Assert.Empty(check.Matches);
    }

    [Theory]
    [InlineData("src/[abc")]
    [InlineData("src/{a,b")]
    [InlineData("src/a]")]
    [InlineData("/etc/passwd")]
    [InlineData("../outside/**")]
    [InlineData("")]
    public void A_bad_glob_is_reported_and_the_check_skipped(string glob)
    {
        WriteLocal("lint.json", JsonSerializer.Serialize(new { id = "lint", command = "lint", matches = new[] { glob } }));
        WriteLocal("guard.json", JsonSerializer.Serialize(new { id = "guard", command = "true", fireOn = "Run", protects = new[] { glob } }));

        var inventory = Store().Inventory();

        Assert.Empty(inventory.Checks);
        Assert.Equal(2, inventory.Problems.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(CheckLimits.MaxTimeoutSeconds + 1)]
    [InlineData(120000)]
    public void A_timeout_out_of_range_is_reported_and_the_check_skipped(int timeout)
    {
        WriteLocal("lint.json", $$"""{"id":"lint","command":"lint","matches":["**"],"timeoutSeconds":{{timeout}}}""");

        var inventory = Store().Inventory();

        Assert.Empty(inventory.Checks);
        Assert.Contains("timeoutSeconds", Assert.Single(inventory.Problems).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("""{"id":"Has Spaces","command":"x","fireOn":"Run"}""")]
    [InlineData("""{"id":"lint","command":"x","fireOn":"Sometimes"}""")]
    [InlineData("""{"id":"lint","command":"x","fireOn":"Run","severity":7}""")]
    public void An_unreadable_manifest_is_reported_and_the_others_still_load(string json)
    {
        WriteLocal("broken.json", json);
        WriteLocal("lint.json", Lint);

        var inventory = Store().Inventory();

        Assert.Equal("lint", Assert.Single(inventory.Checks).Id);
        Assert.Equal(Local("broken.json"), Assert.Single(inventory.Problems).File);
    }

    [Fact]
    public void A_folder_that_cannot_be_listed_throws_nothing()
    {
        // The global "folder" is a file: listing it fails, and the store reports rather than throws.
        File.WriteAllText(_global, "not a folder");
        try
        {
            var inventory = new CheckStore(_root, _global, KnownEcosystems.All).Inventory();
            Assert.Empty(inventory.Checks);
        }
        finally { File.Delete(_global); }
    }

    [Fact]
    public void The_enums_are_stored_by_name_and_read_back()
    {
        var check = new CheckDefinition("lint", "Lints", "npm run lint", ["src/**"], CheckFire.Write, CheckSeverity.Blocking, 30, ["tests/**"])
            { Enabled = false };

        // By name in the store's own file, and in plain System.Text.Json too - a checkpoint and a run record are written
        // with options that know nothing of these enums.
        var file = CheckStore.ToJson(check);
        var plain = JsonSerializer.Serialize(check);
        foreach (var json in new[] { file, plain })
        {
            Assert.Contains("\"Write\"", json, StringComparison.Ordinal);
            Assert.Contains("\"Blocking\"", json, StringComparison.Ordinal);
        }
        Assert.Equal(check, JsonSerializer.Deserialize<CheckDefinition>(plain));

        var resolved = new ResolvedCheck(check, CheckOrigin.Workspace, CheckOrigin.Implicit) { Ecosystems = ["dotnet"] };
        var again = JsonSerializer.Deserialize<ResolvedCheck>(JsonSerializer.Serialize(resolved));
        Assert.Equal(resolved, again);
        Assert.Contains("\"Implicit\"", JsonSerializer.Serialize(resolved), StringComparison.Ordinal);
    }

    // ── the store ───────────────────────────────────────────────────────────

    /// <summary>
    /// A write check that says no timeout gets the write-time limit, not the general two minutes - given those it was
    /// over the limit and never ran at write time. A timeout a manifest SAYS - here or in the check it raises - is kept,
    /// and a check raised to fire at another time without one gets that time's default.
    /// </summary>
    [Fact]
    public void A_write_check_that_says_no_timeout_gets_the_write_time_limit_and_one_that_says_one_keeps_it()
    {
        MakeDotnet();
        WriteLocal("spell.json", """{"id":"spell","command":"cspell","matches":["**/*.md"],"fireOn":"Write"}""");
        WriteLocal("slow.json", """{"id":"slow","command":"slow","matches":["**/*.md"],"fireOn":"Write","timeoutSeconds":90}""");
        WriteGlobal("lint.json", """{"id":"lint","command":"eslint .","matches":["**/*.js"]}""");
        WriteLocal("lint.json", """{"id":"lint","fireOn":"Write"}""");
        WriteGlobal("fmt.json", """{"id":"fmt","command":"fmt","matches":["**/*.js"],"timeoutSeconds":90}""");
        WriteLocal("fmt.json", """{"id":"fmt","fireOn":"Write"}""");
        WriteLocal("build.json", """{"id":"build","fireOn":"Write"}""");
        WriteGlobal("audit.json", """{"id":"audit","command":"npm audit","matches":["**"],"fireOn":"Write"}""");
        WriteLocal("audit.json", """{"id":"audit","fireOn":"Step"}""");

        var checks = Store().Inventory().Checks.ToDictionary(c => c.Id, c => c.Definition.TimeoutSeconds);

        Assert.Equal(CheckLimits.WriteTimeoutLimitSeconds, checks["spell"]);
        Assert.Equal(90, checks["slow"]);
        Assert.Equal(CheckLimits.WriteTimeoutLimitSeconds, checks["lint"]);
        Assert.Equal(90, checks["fmt"]);
        Assert.Equal(CheckLimits.WriteTimeoutLimitSeconds, checks["build"]);
        Assert.Equal(CheckLimits.DefaultTimeoutSeconds, checks["audit"]);
        Assert.Equal(CheckLimits.DefaultTimeoutSeconds, checks["test"]);
    }

    [Fact]
    public void A_workspace_check_shadows_a_global_one_of_the_same_id_and_says_so()
    {
        WriteGlobal("lint.json", """{"id":"lint","command":"eslint .","matches":["**/*.js"]}""");
        WriteGlobal("spell.json", """{"id":"spell","command":"cspell .","fireOn":"Run"}""");
        WriteLocal("lint.json", Lint);

        var checks = Store().Inventory().Checks;

        var lint = Assert.Single(checks, c => c.Id == "lint");
        Assert.Equal("npm run lint", lint.Definition.Command);
        Assert.Equal(CheckOrigin.Workspace, lint.Origin);
        Assert.Equal(CheckOrigin.Global, lint.Shadows);
        var spell = Assert.Single(checks, c => c.Id == "spell");
        Assert.Equal(CheckOrigin.Global, spell.Origin);
        Assert.Null(spell.Shadows);
    }

    [Fact]
    public void Save_and_delete_work_per_scope_one_file_per_check()
    {
        var store = Store();
        var lint = new CheckDefinition("lint", "Lints", "npm run lint", ["src/**/*.ts"], CheckFire.Step, CheckSeverity.Advisory, 60, null);

        store.Save(lint, CheckScope.Global);
        store.Save(lint with { Command = "npm run lint -- --strict" }, CheckScope.Workspace);
        store.Save(lint with { Id = "format", Command = "prettier --check ." }, CheckScope.Workspace);

        Assert.Equal(["lint.json"], Directory.GetFiles(_global).Select(Path.GetFileName));
        Assert.Equal(["format.json", "lint.json"], Directory.GetFiles(Path.GetDirectoryName(Local("x"))!).Select(Path.GetFileName).Order());
        Assert.Equal("npm run lint -- --strict", store.Inventory().Checks.Single(c => c.Id == "lint").Definition.Command);

        store.Delete("lint", CheckScope.Workspace);

        var back = store.Inventory().Checks.Single(c => c.Id == "lint");
        Assert.Equal(CheckOrigin.Global, back.Origin);
        Assert.Null(back.Shadows);
        Assert.Equal(lint, back.Definition);
        Assert.True(File.Exists(Path.Combine(_global, "lint.json")));

        store.Delete("lint", CheckScope.Global);
        Assert.DoesNotContain(store.Inventory().Checks, c => c.Id == "lint");
        store.Delete("lint", CheckScope.Global);                 // not there: the goal, not an error
    }

    [Fact]
    public void Delete_removes_a_hand_named_file_that_declares_the_id()
    {
        WriteLocal("my-linter.json", Lint);

        Store().Delete("lint", CheckScope.Workspace);

        Assert.False(File.Exists(Local("my-linter.json")));
    }

    [Fact]
    public void Save_refuses_what_a_listing_would_skip()
    {
        var store = Store();
        Assert.Throws<ArgumentException>(() =>
            store.Save(new CheckDefinition("lint", null, "lint", [], CheckFire.Write, CheckSeverity.Advisory, 30, null), CheckScope.Workspace));
        Assert.Throws<ArgumentException>(() =>
            store.Save(new CheckDefinition("../escape", null, "x", ["**"], CheckFire.Step, CheckSeverity.Advisory, 30, null), CheckScope.Workspace));
        Assert.Throws<ArgumentException>(() =>
            store.Save(new CheckDefinition("slow", null, "x", ["**"], CheckFire.Step, CheckSeverity.Advisory, 0, null), CheckScope.Global));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Local("x"))));
    }

    [Fact]
    public void A_manifest_may_be_turned_off_and_is_still_listed()
    {
        WriteLocal("lint.json", """{"id":"lint","command":"lint","matches":["**"],"enabled":false}""");

        var check = Assert.Single(Store().Inventory().Checks);

        Assert.False(check.Definition.Enabled);
        Assert.True(new CheckDefinition("x", null, "x", [], CheckFire.Run, CheckSeverity.Advisory, 1, null).Enabled);
    }

    // ── implicit build and test ─────────────────────────────────────────────

    [Fact]
    public void A_dotnet_workspace_lists_build_and_test_as_implicit_advisory_run_checks()
    {
        MakeDotnet();

        var inventory = Store().Inventory();

        Assert.Null(inventory.NoImplicitChecks);
        Assert.Equal(["build", "test"], inventory.Checks.Select(c => c.Id));
        Assert.All(inventory.Checks, c =>
        {
            Assert.Equal(CheckOrigin.Implicit, c.Origin);
            Assert.Equal(CheckSeverity.Advisory, c.Definition.Severity);
            Assert.Equal(CheckFire.Run, c.Definition.FireOn);
            Assert.Null(c.Definition.Command);
            Assert.Equal(["dotnet"], c.Ecosystems);
            Assert.True(c.RunsThroughEcosystem);
        });
    }

    [Fact]
    public void A_workspace_manifest_raises_build_to_blocking_per_step_without_a_command()
    {
        MakeDotnet();
        WriteLocal("build.json", """{"severity":"Blocking","fireOn":"Step"}""");

        var inventory = Store().Inventory();

        Assert.Empty(inventory.Problems);
        var build = Assert.Single(inventory.Checks, c => c.Id == "build");
        Assert.Equal(CheckSeverity.Blocking, build.Definition.Severity);
        Assert.Equal(CheckFire.Step, build.Definition.FireOn);
        Assert.Null(build.Definition.Command);
        Assert.True(build.RunsThroughEcosystem);
        Assert.Equal(CheckOrigin.Workspace, build.Origin);
        Assert.Equal(CheckOrigin.Implicit, build.Shadows);
        Assert.Equal(Local("build.json"), build.File);
        // What it did not say stays the implicit check's.
        var test = Assert.Single(inventory.Checks, c => c.Id == "test");
        Assert.Equal(CheckSeverity.Advisory, test.Definition.Severity);
        Assert.Empty(Store().Validate(build.Definition, CheckScope.Workspace));
    }

    [Fact]
    public void A_workspace_raise_of_a_global_check_keeps_the_global_command()
    {
        WriteGlobal("lint.json", """{"id":"lint","command":"eslint .","matches":["**/*.js"]}""");
        WriteLocal("lint.json", """{"severity":"Blocking"}""");

        var lint = Assert.Single(Store().Inventory().Checks);

        Assert.Equal("eslint .", lint.Definition.Command);
        Assert.Equal(CheckSeverity.Blocking, lint.Definition.Severity);
        Assert.Equal(["**/*.js"], lint.Definition.Matches);
        Assert.Equal(CheckOrigin.Global, lint.Shadows);
    }

    [Fact]
    public void A_workspace_no_ecosystem_applies_to_has_no_implicit_checks_and_says_why()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");
        WriteLocal("build.json", """{"severity":"Blocking","fireOn":"Step"}""");

        var inventory = Store().Inventory();

        Assert.Empty(inventory.Checks);
        Assert.False(string.IsNullOrWhiteSpace(inventory.NoImplicitChecks));
        Assert.Contains("dotnet", inventory.NoImplicitChecks, StringComparison.Ordinal);
        // The raise has nothing to raise, and the problem says why instead of naming an empty id.
        var problem = Assert.Single(inventory.Problems);
        Assert.Contains(inventory.NoImplicitChecks!, problem.Message, StringComparison.Ordinal);
    }

    // ── globs ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("src/**/*.cs", "src/a/b/C.cs", true)]
    [InlineData("src/**/*.cs", "src/C.cs", true)]
    [InlineData("src/**/*.cs", "tests/C.cs", false)]
    [InlineData("*.md", "docs/guide/readme.md", true)]
    [InlineData("docs/*.md", "docs/guide/readme.md", false)]
    [InlineData("tests/**", "tests/a/b.txt", true)]
    [InlineData("src/{a,b}/*.ts", "src/b/x.ts", true)]
    [InlineData("src/{a,b}/*.ts", "src/c/x.ts", false)]
    [InlineData("data/file[0-9].csv", "data/file7.csv", true)]
    [InlineData("data/file[!0-9].csv", "data/file7.csv", false)]
    [InlineData("src\\**\\*.cs", "src/x/y.cs", true)]
    [InlineData("src/{a,[b,c]}/x", "src/c/x", true)]
    [InlineData("src/{a,[b,c]}/x", "src/,/x", true)]
    [InlineData("src/{a,[b,c]}/x", "src/d/x", false)]
    [InlineData("{a,[}]}", "}", true)]
    [InlineData("[]x]", "]", true)]
    [InlineData("[!]x]", "y", true)]
    [InlineData("[!]x]", "]", false)]
    [InlineData("**/**/**/*.cs", "a/b/c.cs", true)]
    [InlineData("**/**/**/*.cs", "c.cs", true)]
    public void A_glob_names_the_paths_it_says(string glob, string path, bool matches)
        => Assert.Equal(matches, CheckGlob.IsMatch(glob, path));

    /// <summary>
    /// The one parser behind both questions: whatever Problem passes, IsMatch can match - it threw on a comma in a set in
    /// alternatives - and whatever IsMatch could not translate, Problem names.
    /// </summary>
    [Theory]
    [InlineData("src/{a,[b,c]}")]
    [InlineData("{a,[}]}")]
    [InlineData("{[,]}")]
    [InlineData("[{]")]
    [InlineData("a,b")]
    [InlineData("{a,b}{c,d}")]
    [InlineData("data/[z-a].csv")]
    [InlineData("{a,{b,c}}")]
    [InlineData("{a,[b}")]
    [InlineData("[!")]
    [InlineData("{")]
    [InlineData("a}")]
    public void A_pattern_with_no_problem_never_throws_and_one_that_cannot_be_matched_has_one(string glob)
    {
        var problem = CheckGlob.Problem(glob);
        var matched = Record.Exception(() => CheckGlob.IsMatch(glob, "src/a/b/c.txt"));

        Assert.Null(matched);
        if (problem is not null)
            Assert.False(CheckGlob.IsMatch(glob, glob));
    }

    [Theory]
    [InlineData("data/[z-a].csv")]
    [InlineData("{a,{b,c}}")]
    [InlineData("{a,[b}")]
    public void A_glob_a_regex_would_refuse_is_a_problem(string glob) => Assert.NotNull(CheckGlob.Problem(glob));

    /// <summary>
    /// A manifest's glob is matched against every path a step changed, inside the step: a pattern a backtracking engine
    /// takes exponential time over must still answer at once.
    /// </summary>
    [Fact]
    public void A_pathological_glob_on_a_deep_path_answers_at_once()
    {
        var deep = string.Join('/', Enumerable.Repeat("a", 400)) + "/" + new string('a', 2000);
        var patterns = new[]
        {
            string.Concat(Enumerable.Repeat("**/", 40)) + "b",
            string.Concat(Enumerable.Repeat("*a", 40)) + "*b",
            "**/" + string.Concat(Enumerable.Repeat("*a", 30)) + "b/**"
        };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var pattern in patterns)
            Assert.False(CheckGlob.IsMatch(pattern, deep));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    // ── a file named otherwise than its id ──────────────────────────────────

    [Fact]
    public void Save_writes_to_the_hand_named_file_that_declares_the_id()
    {
        WriteLocal("my-linter.json", Lint);
        var store = Store();

        store.Save(new CheckDefinition("lint", null, "eslint .", ["**/*.js"], CheckFire.Step, CheckSeverity.Advisory, 30, null), CheckScope.Workspace);

        Assert.Equal(["my-linter.json"], Directory.GetFiles(Path.GetDirectoryName(Local("x"))!).Select(Path.GetFileName));
        var inventory = store.Inventory();
        Assert.Empty(inventory.Problems);
        Assert.Equal("eslint .", Assert.Single(inventory.Checks).Definition.Command);
    }

    [Fact]
    public void Save_settles_two_files_of_one_id_into_one()
    {
        WriteLocal("my-linter.json", Lint);
        WriteLocal("other-linter.json", Lint);
        var store = Store();

        store.Save(new CheckDefinition("lint", null, "eslint .", ["**/*.js"], CheckFire.Step, CheckSeverity.Advisory, 30, null), CheckScope.Workspace);

        Assert.Equal(["lint.json"], Directory.GetFiles(Path.GetDirectoryName(Local("x"))!).Select(Path.GetFileName));
        Assert.Equal("eslint .", Assert.Single(store.Inventory().Checks).Definition.Command);
    }

    [Fact]
    public void Save_never_overwrites_a_file_named_for_the_id_that_holds_another_check()
    {
        WriteLocal("lint.json", """{"id":"format","command":"prettier --check .","fireOn":"Run"}""");
        var store = Store();
        var lint = new CheckDefinition("lint", null, "eslint .", ["**/*.js"], CheckFire.Step, CheckSeverity.Advisory, 30, null);

        Assert.NotEmpty(store.Validate(lint, CheckScope.Workspace));
        Assert.Throws<ArgumentException>(() => store.Save(lint, CheckScope.Workspace));
        Assert.Contains("format", File.ReadAllText(Local("lint.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_never_removes_a_file_that_declares_another_id()
    {
        WriteLocal("lint.json", """{"id":"format","command":"prettier --check .","fireOn":"Run"}""");
        WriteLocal("my-linter.json", Lint);

        Store().Delete("lint", CheckScope.Workspace);

        Assert.True(File.Exists(Local("lint.json")));
        Assert.False(File.Exists(Local("my-linter.json")));
        Assert.Equal("format", Assert.Single(Store().Inventory().Checks).Id);
    }

    // ── notes, not problems ─────────────────────────────────────────────────

    [Fact]
    public void A_write_check_asked_to_block_is_read_as_advisory_and_noted()
    {
        WriteLocal("lint.json", """{"id":"lint","command":"lint","matches":["**"],"fireOn":"Write","severity":"Blocking"}""");

        var inventory = Store().Inventory();

        var lint = Assert.Single(inventory.Checks);
        Assert.Equal(CheckSeverity.Advisory, lint.Definition.Severity);
        Assert.Equal(CheckFire.Write, lint.Definition.FireOn);
        Assert.Empty(inventory.Problems);
        var note = Assert.Single(inventory.Notes);
        Assert.Equal(Local("lint.json"), note.File);
        Assert.Contains("advisory", note.Message, StringComparison.Ordinal);
        Assert.Single(Store().Snapshot().Notes);
    }

    [Fact]
    public void A_global_raise_of_build_where_no_ecosystem_applies_is_a_note_not_a_problem()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");
        WriteGlobal("build.json", """{"severity":"Blocking","fireOn":"Step"}""");

        var inventory = Store().Inventory();

        Assert.Empty(inventory.Checks);
        Assert.Empty(inventory.Problems);
        var note = Assert.Single(inventory.Notes);
        Assert.Equal(CheckScope.Global, note.Scope);
        Assert.Contains("does not apply here", note.Message, StringComparison.Ordinal);

        // In a .NET workspace the same file raises build.
        MakeDotnet();
        Assert.Equal(CheckSeverity.Blocking, Store().Inventory().Checks.Single(c => c.Id == "build").Definition.Severity);
    }

    /// <summary>
    /// Each check says what its own manifest said, apart from what it took from below (Plan Phase 3.5): a workspace manifest
    /// that writes out the global check's command is a check of its own, and one that says only a severity is a raise - an
    /// editor told them apart by comparing commands, and saved the first as a raise.
    /// </summary>
    [Fact]
    public void The_inventory_says_what_each_manifest_said_itself()
    {
        WriteGlobal("lint.json", Lint);
        WriteGlobal("audit.json", """{"id":"audit","command":"npm audit","fireOn":"Run"}""");
        WriteLocal("lint.json", Lint);
        WriteLocal("audit.json", """{"severity":"Blocking"}""");

        var checks = Store().Inventory().Checks;
        var lint = checks.Single(c => c.Id == "lint");
        var audit = checks.Single(c => c.Id == "audit");

        Assert.True(lint.CommandSaid);
        Assert.Equal("npm run lint", lint.Said!.Command);
        Assert.False(audit.CommandSaid);
        Assert.Equal(new CheckManifestSaid(Severity: CheckSeverity.Blocking), audit.Said);
        Assert.Equal("npm audit", audit.Definition.Command);
    }

    /// <summary>
    /// Saved as said, a manifest holds what it says and nothing it took from below: a check turned off from a workspace says
    /// that alone, and follows the global check when that one changes - a full copy stayed behind, frozen.
    /// </summary>
    [Fact]
    public void A_manifest_saved_as_said_writes_only_what_it_says()
    {
        WriteGlobal("audit.json", """{"id":"audit","command":"npm audit","fireOn":"Run"}""");
        var store = Store();

        store.Save("audit", new CheckManifestSaid(Enabled: false), CheckScope.Workspace);

        using var file = JsonDocument.Parse(File.ReadAllText(Local("audit.json")));
        Assert.Equal(["id", "enabled"], file.RootElement.EnumerateObject().Select(p => p.Name));
        WriteGlobal("audit.json", """{"id":"audit","command":"npm audit --omit=dev","fireOn":"Run"}""");
        var audit = Store().Inventory().Checks.Single();
        Assert.Equal(("npm audit --omit=dev", false), (audit.Definition.Command, audit.Definition.Enabled));
    }
}
