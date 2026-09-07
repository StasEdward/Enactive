namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// M1: a template, a workspace and the answers become one frozen specification.
///
/// <para>One property in this file matters more than the rest: <b>a template can never grant more
/// than the workspace allows.</b> Everything else here is convenience.</para>
/// </summary>
public sealed class TemplateResolutionTests
{
    private static readonly WorkspaceInfo Workspace = WorkspaceInfo.For(Path.GetTempPath());

    private static PermissionPolicy Policy(
        PermissionLevel level, string[]? ask = null, string[]? deny = null)
        => new(level, new[] { "*" }, ask ?? Array.Empty<string>())
        {
            Deny = deny ?? Array.Empty<string>()
        };

    private static TaskTemplate Template(PermissionCeiling? ceiling = null)
        => new("release-check", "Release Check", "Build and report.", Permissions: ceiling);

    // ── the property that matters ───────────────────────────────────────────

    /// <summary>
    /// The decisive test. A saved task that could raise its own autonomy would make the workspace
    /// slider decorative: anyone who can drop a JSON file into .enactive/templates could grant
    /// themselves Autonomous in a repository the user deliberately keeps at Execute.
    ///
    /// <para>Note what this does NOT rely on: <see cref="PermissionCeiling"/> has no Allow list and
    /// no way to raise a level, so widening is not something the resolver has to refuse - it is
    /// something the type cannot express. This test guards that shape.</para>
    /// </summary>
    [Fact]
    public void A_template_cannot_raise_the_autonomy_the_workspace_granted()
    {
        var workspace = Policy(PermissionLevel.Execute);
        var greedy = new PermissionCeiling(MaxLevel: PermissionLevel.Autonomous);

        var effective = TemplateResolution.Narrow(workspace, greedy);

        Assert.Equal(PermissionLevel.Execute, effective.Level);
    }

    [Fact]
    public void A_template_can_lower_it()
    {
        var workspace = Policy(PermissionLevel.Autonomous);
        var careful = new PermissionCeiling(MaxLevel: PermissionLevel.Suggest);

        Assert.Equal(PermissionLevel.Suggest, TemplateResolution.Narrow(workspace, careful).Level);
    }

    [Fact]
    public void Ask_and_deny_lists_are_the_union_of_both_levels()
    {
        var workspace = Policy(PermissionLevel.Execute,
            ask: new[] { "run_command" }, deny: new[] { "docker" });
        var ceiling = new PermissionCeiling(
            AskBefore: new[] { "run_command", "write_file" }, Deny: new[] { "git_push" });

        var effective = TemplateResolution.Narrow(workspace, ceiling);

        Assert.Equal(new[] { "run_command", "write_file" }, effective.AskBefore);
        Assert.Equal(new[] { "docker", "git_push" }, effective.Deny);
        Assert.Equal(new[] { "*" }, effective.Allow);   // the ceiling has no way to touch this
    }

    // ── the engine has to honour Deny ───────────────────────────────────────

    /// <summary>
    /// Before this, <c>Evaluate</c> could only answer Allow or Ask: everything a policy did not
    /// permit became a question. Fine while someone is there to answer; useless for a saved task
    /// that says "never push" and fatal for one running at 03:00, which would sit on an approval
    /// nobody is awake to give.
    /// </summary>
    [Fact]
    public void A_denied_tool_is_denied_rather_than_asked_about()
    {
        var engine = new PermissionEngine();
        var policy = Policy(PermissionLevel.Autonomous, deny: new[] { "git_push" });

        Assert.Equal(PermissionDecision.Deny,
            engine.Evaluate(policy, "git_push", PermissionLevel.Execute));
        Assert.Equal(PermissionDecision.Allow,
            engine.Evaluate(policy, "write_file", PermissionLevel.Execute));
    }

    [Fact]
    public void Deny_wins_over_ask_when_a_tool_is_in_both_lists()
    {
        var engine = new PermissionEngine();
        var policy = Policy(PermissionLevel.Execute,
            ask: new[] { "git_push" }, deny: new[] { "git_push" });

        Assert.Equal(PermissionDecision.Deny,
            engine.Evaluate(policy, "git_push", PermissionLevel.Execute));
    }

    // ── parameters ──────────────────────────────────────────────────────────

    [Fact]
    public void A_missing_required_parameter_stops_resolution_and_names_itself()
    {
        var template = Template() with
        {
            Goal = "Fix {issue}.",
            Parameters = new[] { new TemplateParameter("issue", "Issue", TemplateParameterType.Text) }
        };

        var result = TemplateResolution.Resolve(template, Workspace, Policy(PermissionLevel.Execute));

        Assert.False(result.Ok);
        Assert.Null(result.Spec);
        Assert.Contains(result.Problems, p => p.Field.Contains("issue"));
    }

    [Fact]
    public void A_default_stands_in_for_an_answer_that_was_not_given()
    {
        var template = Template() with
        {
            Goal = "Build {solution}.",
            Parameters = new[]
            {
                new TemplateParameter("solution", "Solution", TemplateParameterType.Path,
                    Default: "Enactive.sln")
            }
        };

        var result = TemplateResolution.Resolve(template, Workspace, Policy(PermissionLevel.Execute));

        Assert.True(result.Ok);
        Assert.Equal("Build Enactive.sln.", result.Spec!.Goal);
    }

    [Fact]
    public void A_supplied_value_that_its_type_rejects_stops_resolution()
    {
        var template = Template() with
        {
            Goal = "Retry {retries} times.",
            Parameters = new[]
            {
                new TemplateParameter("retries", "Retries", TemplateParameterType.Integer)
            }
        };

        var result = TemplateResolution.Resolve(
            template, Workspace, Policy(PermissionLevel.Execute),
            new Dictionary<string, string> { ["retries"] = "lots" });

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Message.Contains("not a valid Integer"));
    }

    [Fact]
    public void An_invalid_template_never_resolves_at_all()
    {
        var broken = Template() with { Goal = "" };

        var result = TemplateResolution.Resolve(broken, Workspace, Policy(PermissionLevel.Execute));

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Field == "Goal");
    }

    // ── the snapshot ────────────────────────────────────────────────────────

    [Fact]
    public void The_resolved_specification_carries_the_template_version_it_came_from()
    {
        var template = Template() with { Version = 4 };

        var spec = TemplateResolution
            .Resolve(template, Workspace, Policy(PermissionLevel.Execute)).Spec;

        Assert.NotNull(spec);
        Assert.Equal(4, spec!.TemplateVersion);
        Assert.Equal("release-check", spec.TemplateId);
        Assert.Equal(Workspace.Id, spec.WorkspaceId);
    }

    /// <summary>
    /// Two specifications with the same content must produce the same bytes, or the snapshot cannot
    /// be used to tell whether anything changed between two runs - which is most of what it is for.
    ///
    /// <para>The dictionaries are built here rather than through <c>Resolve</c> on purpose. Resolve
    /// fills its dictionary by walking the template's PARAMETER LIST, so whatever order the answers
    /// arrived in, its output is already in declaration order - a test that went through Resolve
    /// would pass with the ordering removed, and would be testing nothing. A ResolvedTaskSpec is a
    /// public record that anything may construct, so the canonical form has to hold regardless of
    /// who built the dictionary.</para>
    /// </summary>
    [Fact]
    public void The_snapshot_does_not_depend_on_the_order_the_parameters_were_added_in()
    {
        var forwards = new Dictionary<string, string> { ["solution"] = "a.sln", ["mode"] = "full" };
        var backwards = new Dictionary<string, string> { ["mode"] = "full", ["solution"] = "a.sln" };

        Assert.NotEqual(
            string.Join(",", forwards.Keys),
            string.Join(",", backwards.Keys));

        Assert.Equal(Spec(forwards).Snapshot(), Spec(backwards).Snapshot());
    }

    private static ResolvedTaskSpec Spec(IReadOnlyDictionary<string, string> parameters)
        => new(
            TemplateId: "release-check",
            TemplateVersion: 1,
            TemplateName: "Release Check",
            WorkspaceId: Workspace.Id,
            WorkspaceName: Workspace.Name,
            WorkspaceRoot: Workspace.RootPath,
            Goal: "Build a.sln in full.",
            Parameters: parameters,
            Permissions: Policy(PermissionLevel.Execute),
            SuccessCriteria: Array.Empty<SuccessCriterionDefinition>(),
            Limits: ExecutionLimits.None,
            WorkerId: null,
            ReviewRequired: true);

    /// <summary>
    /// The snapshot stores the autonomy tier by NAME. Reading a two-year-old run must not depend on
    /// nobody having inserted a value into the middle of the enum since.
    /// </summary>
    [Fact]
    public void The_snapshot_names_the_autonomy_tier_rather_than_numbering_it()
    {
        var spec = TemplateResolution
            .Resolve(Template(), Workspace, Policy(PermissionLevel.Suggest)).Spec;

        Assert.Contains("Suggest", spec!.Snapshot());
    }
}
