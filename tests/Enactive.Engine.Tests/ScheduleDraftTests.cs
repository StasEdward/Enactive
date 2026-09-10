namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Schedules;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Whether what somebody typed can be saved as a schedule.
///
/// <para>The failure this exists to prevent is not a bad error message. It is a form that says yes
/// to something the runner will say no to at three in the morning: the person is told it is set, and
/// finds out days later that it never ran.</para>
/// </summary>
public sealed class ScheduleDraftTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "enactive-draft", Guid.NewGuid().ToString("N"));

    private readonly WorkspaceInfo _workspace;

    public ScheduleDraftTests()
    {
        Directory.CreateDirectory(_root);
        _workspace = WorkspaceInfo.For(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private static readonly TaskTemplate Tidy =
        new(Id: "tidy", Name: "Tidy up", Goal: "tidy the folder", Parameters: Array.Empty<TemplateParameter>());

    private static TaskTemplate? Find(string id)
        => string.Equals(id, "tidy", StringComparison.OrdinalIgnoreCase) ? Tidy : null;

    private ScheduleDraft Draft(
        string name = "nightly",
        ScheduledWork? work = null,
        ScheduleTiming? timing = null,
        Guid? id = null)
        => new(name, _root,
               work ?? ScheduledWork.FromTemplate("tidy"),
               timing ?? ScheduleTiming.Daily(new TimeOnly(3, 0), "UTC"),
               PermissionPolicy.PermissiveDefault, Id: id);

    private ScheduleDraftResult Check(ScheduleDraft draft, DateTimeOffset? now = null)
        => ScheduleDrafts.Check(draft, _workspace, now ?? DateTimeOffset.Now, Find);

    // ── the ordinary case ───────────────────────────────────────────────────

    [Fact]
    public void A_complete_draft_becomes_a_schedule()
    {
        var result = Check(Draft());

        Assert.Empty(result.Problems);
        Assert.NotNull(result.Schedule);
        Assert.Equal("nightly", result.Schedule!.Name);
        Assert.Equal(_root, result.Schedule.WorkspaceRoot);
    }

    /// <summary>
    /// Editing keeps the ID. The outcomes already filed against it are its history, and a new id
    /// would leave them behind while looking like the same schedule in the list.
    /// </summary>
    [Fact]
    public void Editing_keeps_the_schedules_identity()
    {
        var existing = Guid.NewGuid();

        var result = Check(Draft(name: "renamed", id: existing));

        Assert.Equal(existing, result.Schedule!.Id);
        Assert.Equal("renamed", result.Schedule.Name);
    }

    // ── what is missing ─────────────────────────────────────────────────────

    [Fact]
    public void A_schedule_with_no_name_is_refused()
    {
        var result = Check(Draft(name: "   "));

        Assert.Null(result.Schedule);
        Assert.Contains(result.Problems, p => p.Contains("name"));
    }

    [Fact]
    public void A_draft_that_names_nothing_to_run_is_refused()
    {
        var result = Check(Draft(work: new ScheduledWork(null, new Dictionary<string, string>(), null)));

        Assert.Null(result.Schedule);
        Assert.Contains(result.Problems, p => p.Contains("Choose what to run"));
    }

    /// <summary>Both at once is a different mistake and gets a different sentence.</summary>
    [Fact]
    public void A_draft_that_names_both_is_told_which_mistake_it_made()
    {
        var result = Check(Draft(work: new ScheduledWork("tidy", new Dictionary<string, string>(), "{}")));

        Assert.Contains(result.Problems, p => p.Contains("not both"));
    }

    // ── timings that would never fire ───────────────────────────────────────

    /// <summary>
    /// The quietest failure of all: saved, listed, looks set, never runs. The runner cannot report
    /// it either — an unfireable timing produces no occurrence, so there is nothing for it to
    /// complain about.
    /// </summary>
    [Fact]
    public void A_one_off_in_the_past_is_refused_rather_than_saved_and_forgotten()
    {
        var result = Check(Draft(timing: ScheduleTiming.Once(DateTimeOffset.Now.AddHours(-1))));

        Assert.Null(result.Schedule);
        Assert.Contains(result.Problems, p => p.Contains("already passed"));
    }

    [Fact]
    public void A_weekly_schedule_with_no_day_is_refused()
    {
        var timing = new ScheduleTiming(ScheduleRepeat.Weekly, new TimeOnly(9, 0), "UTC");

        Assert.Contains(Check(Draft(timing: timing)).Problems, p => p.Contains("which day"));
    }

    [Fact]
    public void A_one_off_with_no_moment_is_refused()
    {
        var timing = new ScheduleTiming(ScheduleRepeat.Once, new TimeOnly(9, 0), "UTC");

        Assert.Contains(Check(Draft(timing: timing)).Problems, p => p.Contains("date and time"));
    }

    [Fact]
    public void An_unknown_time_zone_is_refused()
    {
        var timing = ScheduleTiming.Daily(new TimeOnly(3, 0), "Mars Standard Time");

        Assert.Contains(Check(Draft(timing: timing)).Problems, p => p.Contains("does not know the time zone"));
    }

    // ── agreeing with the runner ────────────────────────────────────────────

    /// <summary>
    /// The template half is not a second opinion. It runs the resolution the RUNNER runs, so a draft
    /// the form accepts is one the runner can start — which is the whole reason this check exists
    /// rather than a few null tests on the form.
    /// </summary>
    [Fact]
    public void A_template_that_is_not_there_is_refused_now_rather_than_at_three_in_the_morning()
    {
        var result = Check(Draft(work: ScheduledWork.FromTemplate("no-such-template")));

        Assert.Null(result.Schedule);
        Assert.Contains(result.Problems, p => p.Contains("no template 'no-such-template'"));
    }

    /// <summary>
    /// A template needing a parameter this schedule does not supply cannot run unattended. The
    /// runner says so at fire time; the form says the same thing, from the same code, before it is
    /// saved.
    /// </summary>
    [Fact]
    public void A_template_needing_a_parameter_the_schedule_does_not_supply_is_refused()
    {
        var needy = new TaskTemplate(
            Id: "needy", Name: "Needs a scope", Goal: "tidy {scope}",
            Parameters: new[]
            {
                new TemplateParameter(
                    Id: "scope", Name: "scope", Type: TemplateParameterType.Text,
                    Required: true, Default: null)
            });

        var result = ScheduleDrafts.Check(
            Draft(work: ScheduledWork.FromTemplate("needy")), _workspace, DateTimeOffset.Now,
            id => id == "needy" ? needy : null);

        Assert.Null(result.Schedule);
        Assert.Contains(result.Problems, p => p.Contains("cannot run unattended"));
    }

    /// <summary>
    /// Supplying it makes the same draft savable. Without this the previous test would also pass on
    /// a check that refused everything.
    /// </summary>
    [Fact]
    public void Supplying_the_parameter_makes_it_savable()
    {
        var needy = new TaskTemplate(
            Id: "needy", Name: "Needs a scope", Goal: "tidy {scope}",
            Parameters: new[]
            {
                new TemplateParameter(
                    Id: "scope", Name: "scope", Type: TemplateParameterType.Text,
                    Required: true, Default: null)
            });

        var work = ScheduledWork.FromTemplate("needy", new Dictionary<string, string> { ["scope"] = "logs" });

        var result = ScheduleDrafts.Check(
            Draft(work: work), _workspace, DateTimeOffset.Now, id => id == "needy" ? needy : null);

        Assert.Empty(result.Problems);
        Assert.NotNull(result.Schedule);
    }

    // ── all of them, not the first ──────────────────────────────────────────

    /// <summary>
    /// Every objection at once. Revealing them one at a time makes a person submit four times to
    /// learn four things, and each round trip is a chance to give up.
    /// </summary>
    [Fact]
    public void Every_reason_is_given_at_once()
    {
        var draft = new ScheduleDraft(
            "  ", _root,
            new ScheduledWork(null, new Dictionary<string, string>(), null),
            new ScheduleTiming(ScheduleRepeat.Weekly, new TimeOnly(9, 0), "Mars Standard Time"),
            PermissionPolicy.PermissiveDefault);

        var problems = Check(draft).Problems;

        Assert.Contains(problems, p => p.Contains("name"));
        Assert.Contains(problems, p => p.Contains("Choose what to run"));
        Assert.Contains(problems, p => p.Contains("which day"));
        Assert.Contains(problems, p => p.Contains("time zone"));
    }
}
