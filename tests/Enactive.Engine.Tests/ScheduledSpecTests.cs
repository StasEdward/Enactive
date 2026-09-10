namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Schedules;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Turning a schedule into the thing a run starts from — and, mostly, saying why it cannot.
///
/// <para>Nobody is at the machine at three in the morning. A schedule that fails to produce a task
/// and leaves nothing behind is indistinguishable from a schedule that never fired, and the person
/// finds out weeks later by noticing the work was not done.</para>
/// </summary>
public sealed class ScheduledSpecTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "enactive-schedspec", Guid.NewGuid().ToString("N"));

    private readonly WorkspaceInfo _workspace;

    public ScheduledSpecTests()
    {
        Directory.CreateDirectory(_root);
        _workspace = WorkspaceInfo.For(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private Schedule With(ScheduledWork work)
        => new(Guid.NewGuid(), _root, "nightly", work,
               ScheduleTiming.Daily(new TimeOnly(3, 0), "UTC"),
               PermissionPolicy.PermissiveDefault, DateTimeOffset.UtcNow);

    private static TaskTemplate Template(params TemplateParameter[] parameters)
        => new(Id: "tidy", Name: "Tidy up", Goal: "tidy {scope}", Parameters: parameters);

    private static TemplateParameter Parameter(string id, string? @default)
        => new(Id: id, Name: id, Type: TemplateParameterType.Text, Required: true, Default: @default);

    // ── a template, resolved as it stands today ─────────────────────────────

    [Fact]
    public void A_template_schedule_resolves_against_the_template()
    {
        var schedule = With(ScheduledWork.FromTemplate(
            "tidy", new Dictionary<string, string> { ["scope"] = "the logs" }));

        var result = ScheduledSpec.For(schedule, _workspace, _ => Template(Parameter("scope", null)));

        Assert.NotNull(result.Spec);
        Assert.Equal("tidy the logs", result.Spec!.Goal);
        Assert.Equal("", result.Why);
    }

    /// <summary>
    /// The price of resolving late: a template edited into needing something this schedule does not
    /// supply. Reported, with the template's name and the missing field — not skipped, and not run
    /// half-filled.
    /// </summary>
    [Fact]
    public void A_template_that_grew_a_parameter_says_so_instead_of_running()
    {
        var schedule = With(ScheduledWork.FromTemplate("tidy"));

        var result = ScheduledSpec.For(
            schedule, _workspace, _ => Template(Parameter("scope", null), Parameter("depth", null)));

        Assert.Null(result.Spec);
        Assert.Contains("Tidy up", result.Why, StringComparison.Ordinal);
        Assert.Contains("depth", result.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_that_is_gone_says_so()
    {
        var result = ScheduledSpec.For(With(ScheduledWork.FromTemplate("tidy")), _workspace, _ => null);

        Assert.Null(result.Spec);
        Assert.Contains("tidy", result.Why, StringComparison.Ordinal);
    }

    // ── a past run, frozen ──────────────────────────────────────────────────

    /// <summary>
    /// "Run that again" means the thing that ran. The snapshot is used as it stands, and no template
    /// is consulted — asserted by handing it a lookup that would throw if it were.
    /// </summary>
    [Fact]
    public void A_past_run_is_used_exactly_as_it_ran()
    {
        var original = new ResolvedTaskSpec(
            "tidy", 3, "Tidy up", _workspace.Id, "ws", _root, "tidy the logs",
            new Dictionary<string, string> { ["scope"] = "the logs" },
            PermissionPolicy.PermissiveDefault, Array.Empty<SuccessCriterionDefinition>(),
            ExecutionLimits.None, null, false);

        var result = ScheduledSpec.For(
            With(ScheduledWork.FromPastRun(original.Snapshot())), _workspace,
            _ => throw new InvalidOperationException("a past-run schedule must not consult a template"));

        Assert.NotNull(result.Spec);
        Assert.Equal("tidy the logs", result.Spec!.Goal);
        Assert.Equal(3, result.Spec.TemplateVersion);
    }

    [Fact]
    public void A_recorded_run_that_cannot_be_read_back_says_so()
    {
        var result = ScheduledSpec.For(
            With(ScheduledWork.FromPastRun("{ this is not a spec")), _workspace, _ => null);

        Assert.Null(result.Spec);
        Assert.NotEqual("", result.Why);
    }

    [Fact]
    public void Work_that_names_nothing_says_so()
    {
        var result = ScheduledSpec.For(
            With(new ScheduledWork(null, new Dictionary<string, string>(), null)), _workspace, _ => null);

        Assert.Null(result.Spec);
        Assert.NotEqual("", result.Why);
    }
}
