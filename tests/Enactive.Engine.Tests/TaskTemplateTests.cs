namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Enactive.Core.Context;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Workspace;
using Enactive.Tools;
using Xunit;

/// <summary>
/// M0: a template is data. It is written, validated and stored, and nothing about it executes.
///
/// <para>The checks that matter here are the ones about the ID, because the id becomes a file name.
/// Everything else in this file guards ordinary correctness; those two guard the template folder.</para>
/// </summary>
public sealed class TaskTemplateTests : IDisposable
{
    private readonly string _root;
    private readonly string _global;

    public TaskTemplateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
        _global = Path.Combine(_root, "global-templates");
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Never the real %APPDATA%: a test that read the person's own template folder would pass or
    /// fail depending on what they happen to have saved.
    /// </summary>
    private TemplateStore Store() => new(_root, _global);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>
    /// A throwaway template. The id deliberately matches NO built-in: "fix-bug" used to be the
    /// default here and became a shipped template, so a test asserting "this id is not in the
    /// library" started failing over a name collision rather than over what it was testing.
    /// </summary>
    private static TaskTemplate Simple(string id = "sample-task")
        => new(id, "Sample Task", "Do the sample thing.");

    // ── the id is a path component ──────────────────────────────────────────

    [Theory]
    [InlineData("../../settings")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("c:template")]
    [InlineData("Fix-Bug")]     // uppercase: two ids that differ only by case are one file on Windows
    [InlineData("9lives")]      // must start with a letter, so {0} is never parameter-shaped
    [InlineData("")]
    public void An_id_that_is_not_a_safe_file_name_is_refused(string id)
    {
        Assert.False(TemplateValidator.IsSafeId(id));
        Assert.Contains(TemplateValidator.Validate(Simple(id)), p => p.Field == "Id");
    }

    /// <summary>
    /// The decisive one. A template id becomes &lt;folder&gt;/&lt;id&gt;.json, so an id of
    /// "../../settings" would have the store write outside the template folder entirely.
    /// </summary>
    [Fact]
    public void The_store_refuses_to_turn_an_unsafe_id_into_a_path()
    {
        var store = Store();

        Assert.Throws<ArgumentException>(() => store.PathFor("../../settings", TemplateScope.Workspace));
        Assert.Throws<ArgumentException>(() => store.PathFor("a/b", TemplateScope.Global));

        var safe = store.PathFor("fix-bug", TemplateScope.Workspace);
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(safe), StringComparison.OrdinalIgnoreCase);
    }

    // ── placeholders ────────────────────────────────────────────────────────

    [Fact]
    public void A_placeholder_that_names_no_parameter_is_a_typo_and_is_reported()
    {
        var template = Simple() with
        {
            Goal = "Fix {isue} in the parser.",
            Parameters = new[] { new TemplateParameter("issue", "Issue", TemplateParameterType.Text) }
        };

        var problems = TemplateValidator.Validate(template);
        Assert.Contains(problems, p => p.Message.Contains("{isue}"));
    }

    /// <summary>
    /// A goal is prose, and prose contains braces. Only a token shaped like a parameter id - a
    /// letter first - is treated as a reference, so a JSON body or a format string in the task text
    /// neither fails validation nor gets mangled at substitution.
    /// </summary>
    [Fact]
    public void Braces_that_are_not_parameter_shaped_are_left_alone()
    {
        var template = Simple() with
        {
            Goal = "Given {} and {0}, make Dictionary<string, int> {} parse. Fix {issue}.",
            Parameters = new[] { new TemplateParameter("issue", "Issue", TemplateParameterType.Text) }
        };

        Assert.Empty(TemplateValidator.Validate(template));

        var filled = TemplateResolution.Substitute(
            template.Goal, new Dictionary<string, string> { ["issue"] = "the reconnect" });

        Assert.Contains("{}", filled);
        Assert.Contains("{0}", filled);
        Assert.Contains("Fix the reconnect.", filled);
    }

    // ── parameters ──────────────────────────────────────────────────────────

    [Fact]
    public void A_choice_with_one_option_is_not_a_choice()
    {
        var template = Simple() with
        {
            Parameters = new[]
            {
                new TemplateParameter("mode", "Mode", TemplateParameterType.Choice,
                    Choices: new[] { "only-one" })
            }
        };

        Assert.Contains(TemplateValidator.Validate(template), p => p.Message.Contains("two choices"));
    }

    [Fact]
    public void A_default_that_its_own_type_would_reject_is_reported()
    {
        var template = Simple() with
        {
            Parameters = new[]
            {
                new TemplateParameter("retries", "Retries", TemplateParameterType.Integer, Default: "many")
            }
        };

        Assert.Contains(TemplateValidator.Validate(template), p => p.Message.Contains("not a valid Integer"));
    }

    [Fact]
    public void A_limit_of_zero_would_mean_the_run_may_do_nothing()
    {
        var template = Simple() with { Limits = new ExecutionLimits(MaxSteps: 0) };
        Assert.Contains(TemplateValidator.Validate(template), p => p.Field == "Limits");

        // Null is the way to say "no limit of its own", and stays legal.
        Assert.Empty(TemplateValidator.Validate(Simple() with { Limits = new ExecutionLimits() }));
    }

    // ── storage ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_stored_template_comes_back_as_it_went_in()
    {
        var store = Store();
        var template = new TaskTemplate(
            "release-check", "Release Check", "Build {solution} and report.",
            Version: 3,
            Description: "Build, test, report.",
            Category: "DevOps",
            Parameters: new[]
            {
                new TemplateParameter("solution", "Solution", TemplateParameterType.Path, Default: "Enactive.sln"),
                new TemplateParameter("mode", "Mode", TemplateParameterType.Choice,
                    Choices: new[] { "quick", "full" }, Default: "full")
            },
            Permissions: new PermissionCeiling(PermissionLevel.Execute, new[] { "run_command" }, new[] { "git_push" }),
            SuccessCriteria: new[] { new SuccessCriterionDefinition("Builds", "dotnet build", 0) },
            Limits: new ExecutionLimits(MaxSteps: 12, MaxTokens: 90_000),
            WorkerId: "developer",
            ReviewRequired: false);

        store.Save(template, TemplateScope.Workspace);
        var back = store.Find("release-check");

        Assert.NotNull(back);

        // Field by field, not Assert.Equal on the records: a record's generated equality compares
        // each member with EqualityComparer<T>.Default, and for the arrays here that is REFERENCE
        // equality - two identical templates loaded from disk would never be equal, so the
        // comparison would fail for a reason that has nothing to do with serialization.
        Assert.Equal(template.Name, back!.Name);
        Assert.Equal(template.Goal, back.Goal);
        Assert.Equal(template.Version, back.Version);
        Assert.Equal(template.Description, back.Description);
        Assert.Equal(template.Category, back.Category);
        Assert.Equal(template.WorkerId, back.WorkerId);
        Assert.Equal(template.ReviewRequired, back.ReviewRequired);

        Assert.Equal(new[] { "solution", "mode" }, back.ParameterList.Select(p => p.Id));
        Assert.Equal(TemplateParameterType.Path, back.ParameterList[0].Type);
        Assert.Equal("Enactive.sln", back.ParameterList[0].Default);
        Assert.Equal(new[] { "quick", "full" }, back.ParameterList[1].Choices);

        Assert.Equal(PermissionLevel.Execute, back.Ceiling.MaxLevel);
        Assert.Equal(new[] { "run_command" }, back.Ceiling.AskBefore);
        Assert.Equal(new[] { "git_push" }, back.Ceiling.Deny);

        Assert.Equal("Builds", Assert.Single(back.CriteriaList).Name);
        Assert.Equal(12, back.LimitsOrNone.MaxSteps);
        Assert.Equal(90_000, back.LimitsOrNone.MaxTokens);
        Assert.Null(back.LimitsOrNone.MaxDurationSeconds);
    }

    /// <summary>
    /// The mechanism behind "generic template, project-specific command": the project keeps its own
    /// copy next to the code instead of editing the shared one.
    /// </summary>
    [Fact]
    public void A_workspace_template_shadows_a_global_one_with_the_same_id()
    {
        var store = Store();
        var globalPath = store.PathFor("shadow-me", TemplateScope.Global);
        var localPath = store.PathFor("shadow-me", TemplateScope.Workspace);

        Directory.CreateDirectory(Path.GetDirectoryName(globalPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllText(globalPath, """{"id":"shadow-me","name":"Global","goal":"generic"}""");
        File.WriteAllText(localPath, """{"id":"shadow-me","name":"Local","goal":"specific"}""");

        var found = store.Find("shadow-me");

        Assert.NotNull(found);
        Assert.Equal("Local", found!.Name);
        Assert.Single(store.Load(), t => t.Id == "shadow-me");
    }

    /// <summary>
    /// The library is never empty, so the feature can be tried the moment it is opened - and every
    /// shipped template has to survive the same validator a hand-written one does. A built-in that
    /// does not validate could not be saved by anyone who duplicated it.
    /// </summary>
    [Fact]
    public void Every_built_in_template_is_valid_read_only_and_uniquely_named()
    {
        Assert.NotEmpty(BuiltinTemplates.All);

        foreach (var template in BuiltinTemplates.All)
        {
            Assert.True(template.Builtin, $"'{template.Id}' is shipped but not marked built-in.");
            Assert.Empty(TemplateValidator.Validate(template));
        }

        Assert.Equal(
            BuiltinTemplates.All.Count,
            BuiltinTemplates.All.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// The customisation path for someone who wants THIS project's Release Check rather than a copy
    /// under a new name: a file of the same id replaces the shipped one.
    /// </summary>
    [Fact]
    public void A_file_of_the_same_id_replaces_a_built_in()
    {
        var store = Store();
        var builtin = BuiltinTemplates.All[0];

        var mine = builtin.Duplicate(builtin.Id, "My " + builtin.Name) with { Goal = "do it my way" };
        store.Save(mine, TemplateScope.Workspace);

        var found = store.Find(builtin.Id);

        Assert.NotNull(found);
        Assert.Equal("do it my way", found!.Goal);
        Assert.False(found.Builtin);
        Assert.Single(store.Load(), t => t.Id == builtin.Id);
    }

    /// <summary>
    /// One unreadable file must not empty the library. The user would see nothing and have no way
    /// to tell which file to fix - a worse failure than the one being reported.
    /// </summary>
    [Fact]
    public void A_broken_file_is_skipped_rather_than_hiding_the_others()
    {
        var store = Store();
        store.Save(Simple("good-one"), TemplateScope.Workspace);

        File.WriteAllText(Path.Combine(store.WorkspaceFolder!, "broken.json"), "{ this is not json");
        File.WriteAllText(Path.Combine(store.WorkspaceFolder!, "nameless.json"),
            """{"id":"../escape","name":"Escapee","goal":"x"}""");

        var loaded = store.Load();

        Assert.Single(loaded, t => t.Id == "good-one");
        Assert.DoesNotContain(loaded, t => t.Id.Contains("escape", StringComparison.Ordinal));
        Assert.DoesNotContain(loaded, t => t.Id == "broken");
    }

    [Fact]
    public void A_builtin_template_is_read_only_and_duplicating_it_produces_an_editable_copy()
    {
        var store = Store();
        var builtin = Simple() with { Version = 7, Builtin = true };

        Assert.Throws<InvalidOperationException>(() => store.Save(builtin, TemplateScope.Workspace));

        var copy = builtin.Duplicate("fix-bug-enactive", "Fix Bug — Enactive");

        Assert.False(copy.Builtin);
        Assert.Equal(1, copy.Version);
        Assert.Equal("fix-bug-enactive", copy.Id);
        Assert.Equal(builtin.Goal, copy.Goal);

        store.Save(copy, TemplateScope.Workspace);
        Assert.NotNull(store.Find("fix-bug-enactive"));
    }

    [Fact]
    public void An_invalid_template_is_never_written()
    {
        var store = Store();

        Assert.Throws<ArgumentException>(() => store.Save(Simple() with { Goal = "" }, TemplateScope.Workspace));

        // The built-ins are always there, so "nothing was written" is about this id, not about the
        // library being empty.
        Assert.DoesNotContain(store.Load(), t => t.Id == "sample-task");
    }

    // ── the built-in library ────────────────────────────────────────────────

    /// <summary>
    /// Every tool a built-in names must EXIST.
    ///
    /// <para>Shipped 2026-09-07 and found an hour later: all three built-ins denied
    /// <c>create_dir</c>, and the tool is called <c>create_directory</c>. The permission engine
    /// matches names exactly, so that entry forbade nothing at all — a restriction written down,
    /// displayed in the template, and connected to nothing. The same class as <c>edit_file</c>
    /// shipping in no worker's allowlist, twice in one day.</para>
    ///
    /// <para>The tool set comes from the assembly rather than a list typed here, because a
    /// hand-copied list is the same bug wearing a different hat.</para>
    /// </summary>
    [Fact]
    public void Every_tool_a_built_in_names_is_a_real_tool()
    {
        var real = ShippedToolNames();
        Assert.Contains("create_directory", real);

        foreach (var template in BuiltinTemplates.All)
        foreach (var named in template.Ceiling.DenyList.Concat(template.Ceiling.AskBeforeList))
            Assert.True(
                real.Contains(named),
                $"'{template.Id}' names the tool '{named}', which does not exist. "
                + "The permission engine matches names exactly, so this restricts nothing. "
                + "Real tools: " + string.Join(", ", real.OrderBy(n => n)));
    }

    /// <summary>
    /// Every ITool this build ships, by the name the permission engine matches on. A tool built from what the host knows
    /// (run_tests, from the ecosystems) has no constructor to call bare, so the host's own list adds it.
    /// </summary>
    private static HashSet<string> ShippedToolNames()
        => typeof(WriteFileTool).Assembly
            .GetTypes()
            .Where(t => typeof(ITool).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => ((ITool)Activator.CreateInstance(t)!).Definition.Name)
            .Concat(EngineFixture.ShippedTools().Select(t => t.Definition.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A built-in either runs with nothing supplied, or names exactly which parameter stops it.
    /// That is what makes the console's refusal actionable instead of "it did not work".
    /// </summary>
    [Fact]
    public void Every_built_in_either_resolves_unattended_or_says_what_is_missing()
    {
        var workspace = WorkspaceInfo.For(_root);
        var policy = new PermissionPolicy(PermissionLevel.Execute, new[] { "*" }, Array.Empty<string>());

        foreach (var template in BuiltinTemplates.All)
        {
            var result = TemplateResolution.Resolve(template, workspace, policy);

            if (result.Ok)
                continue;

            Assert.All(result.Problems, p =>
                Assert.True(
                    p.Field.StartsWith("Parameter '", StringComparison.Ordinal),
                    $"'{template.Id}' cannot run unattended for a reason nobody can act on: {p}"));
        }
    }

    /// <summary>
    /// A criteria list made only of optional checks is a gate that gates nothing - it reports and
    /// changes no outcome. Either a template means its checks or it should not carry any.
    /// </summary>
    [Fact]
    public void A_built_in_that_carries_checks_means_at_least_one_of_them()
    {
        foreach (var template in BuiltinTemplates.All.Where(t => t.CriteriaList.Count > 0))
            Assert.True(
                template.CriteriaList.Any(c => c.Required),
                $"'{template.Id}' has only optional checks, so nothing it checks can affect its outcome.");
    }

    /// <summary>
    /// The release check reports on the workspace and may not change it, so a failing test is its finding, not a
    /// failed run: its test check passes on the runner's "some tests failed" (1) as well as on 0, and on nothing
    /// else. And it runs the commands the person gave - with "npm test" given, it ran "dotnet test" regardless.
    /// On 2026-10-05 a check demanding exit 0 failed the run whose report was right, then sent a repair to make the
    /// tests pass in a run denied every file tool.
    /// </summary>
    [Fact]
    public void The_release_check_runs_the_given_commands_and_reports_failing_tests_rather_than_failing()
    {
        var template = BuiltinTemplates.All.Single(t => t.Id == "release-check");
        var spec = TemplateResolution.Resolve(
            template, WorkspaceInfo.For(_root),
            new PermissionPolicy(PermissionLevel.Execute, new[] { "*" }, Array.Empty<string>()),
            new Dictionary<string, string> { ["build_command"] = "npm run build", ["test_command"] = "npm test" }).Spec;

        Assert.NotNull(spec);
        var build = spec!.SuccessCriteria.Single(c => c.Name == "Builds");
        var tests = spec.SuccessCriteria.Single(c => c.Name == "Tests ran");

        Assert.Equal("npm run build", build.Command);
        Assert.Equal("npm test", tests.Command);

        Assert.True(build.PassesOn(0));
        Assert.False(build.PassesOn(1));

        Assert.True(tests.PassesOn(0));
        Assert.True(tests.PassesOn(1));
        Assert.False(tests.PassesOn(2));
        Assert.True(tests.Required);
    }

    /// <summary>
    /// An optional parameter nobody filled in leaves NOTHING behind, not its own name. Skipping it
    /// left the literal text "{area}" in the prompt: a token the model has no way to read as "the
    /// author left this blank", and every chance of treating as something to interpret.
    /// </summary>
    [Fact]
    public void An_unfilled_optional_parameter_disappears_from_the_goal()
    {
        var template = Simple() with
        {
            Goal = "Cover {area} in the tests.",
            Parameters = new[]
            {
                new TemplateParameter("area", "Area", TemplateParameterType.Text, Required: false)
            }
        };

        var spec = TemplateResolution.Resolve(
            template, WorkspaceInfo.For(_root),
            new PermissionPolicy(PermissionLevel.Execute, new[] { "*" }, Array.Empty<string>())).Spec;

        Assert.NotNull(spec);
        Assert.DoesNotContain("{area}", spec!.Goal);
        Assert.Equal("Cover  in the tests.", spec.Goal);
    }

    /// <summary>
    /// The coverage template declares no criteria of its own. It declared "Builds: dotnet build" - .NET's command in a
    /// template for any project - and a template's criteria lock the review of the final checks, while its goal names a
    /// test command the review kept adding: on 2026-10-08 that asked three times and once ended a run before it began.
    /// The engine checks for new build errors itself, for every kind of project it knows.
    /// </summary>
    [Fact]
    public void The_coverage_template_locks_no_criteria()
    {
        var template = Assert.Single(BuiltinTemplates.All, t => t.Id == "improve-tests");

        Assert.Empty(template.SuccessCriteria);
        Assert.Equal(3, template.Version);
    }
}
