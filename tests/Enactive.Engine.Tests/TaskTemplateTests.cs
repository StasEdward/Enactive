namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Workspace;
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

    private static TaskTemplate Simple(string id = "fix-bug")
        => new(id, "Fix Bug", "Fix the reported problem.");

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
        Assert.Single(store.Load());
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

        Assert.Single(loaded);
        Assert.Equal("good-one", loaded[0].Id);
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
        Assert.Empty(store.Load());
    }
}
