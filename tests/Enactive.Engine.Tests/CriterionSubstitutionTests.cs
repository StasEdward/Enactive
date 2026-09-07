namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Noticed 2026-09-07 while writing a success criterion for a GLOBAL template.
///
/// <para>Parameters exist so that one template can serve every workspace: the goal says
/// <c>{test_command}</c> and each run fills it in. The criteria were the single place they did not
/// reach — <c>Resolve</c> substituted the goal and passed <c>template.CriteriaList</c> through
/// verbatim — so a criterion could only ever name a command literally, and a template that needed a
/// per-workspace one had to stop being global.</para>
///
/// <para>Worse, it failed SILENTLY: writing <c>{test_command}</c> in a criterion sent those eight
/// characters to the shell, which fails for a reason that has nothing to do with the run. That is
/// what makes this worth fixing rather than documenting.</para>
/// </summary>
public sealed class CriterionSubstitutionTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceInfo _workspace;

    public CriterionSubstitutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = WorkspaceInfo.For(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private static TaskTemplate Template(params SuccessCriterionDefinition[] criteria)
        => new(
            Id: "improve-tests",
            Name: "Improve Test Coverage",
            Goal: "Add tests for {area}.\n\nTest command: {test_command}",
            Parameters: new[]
            {
                new TemplateParameter("area", "What to cover", TemplateParameterType.Text,
                                      Required: true, Default: "the least covered behaviour"),
                new TemplateParameter("test_command", "Test command", TemplateParameterType.Text,
                                      Required: true, Default: "dotnet test")
            },
            SuccessCriteria: criteria);

    private ResolvedTaskSpec Resolve(TaskTemplate template, params (string Key, string Value)[] values)
    {
        var result = TemplateResolution.Resolve(
            template, _workspace, PermissionPolicy.PermissiveDefault,
            values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase));

        Assert.True(result.Ok, string.Join("; ", result.Problems.Select(p => p.Field + ": " + p.Message)));
        return result.Spec!;
    }

    // ── the gap ─────────────────────────────────────────────────────────────

    /// <summary>The one that matters: a criterion may name its command through a parameter.</summary>
    [Fact]
    public void A_criterion_command_is_filled_in_like_the_goal()
    {
        var spec = Resolve(
            Template(new SuccessCriterionDefinition("Tests run", "{test_command}")),
            ("test_command", "dotnet run --project tests/Smoke/Smoke.csproj"));

        Assert.Equal("dotnet run --project tests/Smoke/Smoke.csproj",
                     Assert.Single(spec.SuccessCriteria).Command);
    }

    /// <summary>A placeholder inside a longer command, which is the ordinary case.</summary>
    [Fact]
    public void A_placeholder_inside_a_command_is_filled_in_too()
    {
        var spec = Resolve(
            Template(new SuccessCriterionDefinition("Builds", "dotnet build {project}")),
            ("test_command", "dotnet test"));

        // {project} is not a declared parameter, so it is left exactly as written - the same rule
        // the goal follows, and the reason substitution is safe to run over a shell command at all.
        Assert.Equal("dotnet build {project}", Assert.Single(spec.SuccessCriteria).Command);
    }

    /// <summary>The default is used when nothing was supplied, exactly as for the goal.</summary>
    [Fact]
    public void A_criterion_gets_the_parameters_default()
    {
        var spec = Resolve(Template(new SuccessCriterionDefinition("Tests run", "{test_command}")));

        Assert.Equal("dotnet test", Assert.Single(spec.SuccessCriteria).Command);
    }

    /// <summary>Every criterion, not just the first.</summary>
    [Fact]
    public void All_of_them_are_filled_in()
    {
        var spec = Resolve(
            Template(
                new SuccessCriterionDefinition("Builds", "dotnet build"),
                new SuccessCriterionDefinition("Tests run", "{test_command}"),
                new SuccessCriterionDefinition("Tests run again", "{test_command} --no-build")),
            ("test_command", "dotnet run --project x"));

        Assert.Equal(
            new[] { "dotnet build", "dotnet run --project x", "dotnet run --project x --no-build" },
            spec.SuccessCriteria.Select(c => c.Command));
    }

    // ── what must not change ────────────────────────────────────────────────

    /// <summary>
    /// The NAME is left alone. It is what a person reads in the run's outcome — "Tests run: FAILED" —
    /// and a name that changes with its parameters stops being a name.
    /// </summary>
    [Fact]
    public void The_name_is_not_substituted()
    {
        var spec = Resolve(
            Template(new SuccessCriterionDefinition("{test_command} passes", "{test_command}")),
            ("test_command", "dotnet test"));

        var criterion = Assert.Single(spec.SuccessCriteria);
        Assert.Equal("{test_command} passes", criterion.Name);
        Assert.Equal("dotnet test", criterion.Command);
    }

    /// <summary>A command with no placeholders comes through untouched, braces and all.</summary>
    [Theory]
    [InlineData("dotnet build")]
    [InlineData("bash -c 'for f in *.cs; do echo ${f}; done'")]
    [InlineData("jq '{name: .name}' package.json")]
    public void A_command_with_nothing_to_fill_in_is_left_exactly_alone(string command)
    {
        var spec = Resolve(Template(new SuccessCriterionDefinition("Check", command)),
                           ("test_command", "dotnet test"));

        Assert.Equal(command, Assert.Single(spec.SuccessCriteria).Command);
    }

    /// <summary>The rest of the criterion is carried through as written.</summary>
    [Fact]
    public void The_exit_code_and_the_requirement_survive()
    {
        var spec = Resolve(
            Template(new SuccessCriterionDefinition("Diff is empty", "git diff --exit-code {area}",
                                                    ExpectedExitCode: 0, Required: false)),
            ("area", "src/"), ("test_command", "dotnet test"));

        var criterion = Assert.Single(spec.SuccessCriteria);
        Assert.Equal("git diff --exit-code src/", criterion.Command);
        Assert.Equal(0, criterion.ExpectedExitCode);
        Assert.False(criterion.Required);
    }

    /// <summary>A template with no criteria still resolves.</summary>
    [Fact]
    public void No_criteria_is_not_a_problem()
        => Assert.Empty(Resolve(Template()).SuccessCriteria);

    /// <summary>And the goal is still filled in — this was not a swap.</summary>
    [Fact]
    public void The_goal_is_still_filled_in()
    {
        var spec = Resolve(
            Template(new SuccessCriterionDefinition("Builds", "dotnet build")),
            ("area", "the parser"), ("test_command", "dotnet test"));

        Assert.Contains("Add tests for the parser.", spec.Goal, StringComparison.Ordinal);
        Assert.Contains("Test command: dotnet test", spec.Goal, StringComparison.Ordinal);
    }
}
