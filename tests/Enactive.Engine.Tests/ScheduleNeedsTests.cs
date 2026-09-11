namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Schedules;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// FIX_PLAN §9an, M2: a schedule that cannot work is refused when it is written, not at 00:14.
///
/// <para>Both halves of what the person was shown were TRUE. The window said "this runs Code
/// Review". It said "Refused, not asked about: run_command, run_powershell, git, docker — nobody is
/// watching a scheduled run". Nobody put the two together, and the run found out for them, six
/// refusals at a time.</para>
///
/// <para>These tests hold the join, and — as importantly — hold it to refusing only what is
/// actually broken. A form that refuses schedules which would have worked is the same failure
/// wearing better manners.</para>
/// </summary>
public sealed class ScheduleNeedsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "enactive-needs", Guid.NewGuid().ToString("N"));

    private readonly WorkspaceInfo _workspace;

    public ScheduleNeedsTests()
    {
        Directory.CreateDirectory(_root);
        _workspace = WorkspaceInfo.For(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>Slider positions, by name. AutonomyTiers holds the mapping; these are the indexes.</summary>
    private const int Execute = 2;

    private const int Autonomous = 3;

    private static TaskTemplate Review => BuiltinTemplates.All.Single(t => t.Id == "code-review");

    private static TaskTemplate? Find(string id)
        => BuiltinTemplates.All.FirstOrDefault(
            t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    private ScheduleDraftResult Check(int tier, string templateId = "code-review")
        => ScheduleDrafts.Check(
            new ScheduleDraft(
                "nightly", _root,
                ScheduledWork.FromTemplate(templateId),
                ScheduleTiming.Daily(new TimeOnly(3, 0), "UTC"),
                AutonomyTiers.PolicyFor(tier)),
            _workspace, DateTimeOffset.Now, Find);

    // ── the case that was found ─────────────────────────────────────────────

    /// <summary>
    /// Code Review on the Execute tier. Its scope defaults to a diff; Execute asks before git and
    /// before both shells; a schedule has nobody to ask. That combination could be saved, and the
    /// only thing that ever objected was the run itself.
    /// </summary>
    [Fact]
    public void Code_review_on_a_tier_that_cannot_reach_a_diff_is_refused_when_it_is_written()
    {
        var result = Check(Execute);

        Assert.Null(result.Schedule);

        var problem = Assert.Single(result.Problems);

        // The capability, not the tool list: a person reads "cannot see what has changed" and knows
        // what is broken without knowing which tool produces a diff.
        Assert.Contains("cannot see what has changed", problem, StringComparison.Ordinal);

        // Every alternative is named, because "it needs a tool" invites the wrong fix.
        Assert.Contains("git", problem, StringComparison.Ordinal);
        Assert.Contains("run_command", problem, StringComparison.Ordinal);

        // And the reason is the unattended one, not a policy denial - they need different fixes.
        Assert.Contains(ToolOffers.Unanswerable, problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both ways out, in the sentence. One of them is the one the check cannot see for itself:
    /// Code Review needs a diff only because its scope parameter defaults to one.
    /// </summary>
    [Fact]
    public void The_refusal_says_what_to_do_about_it()
    {
        var problem = Assert.Single(Check(Execute).Problems);

        Assert.Contains("choose a tier that allows these outright", problem, StringComparison.Ordinal);
        Assert.Contains("What to review", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tier §9an recommends actually works. Without this the test above passes over a check
    /// that refuses Code Review on every tier, which is a working feature in the sense that a
    /// disconnected wire is safe.
    /// </summary>
    [Fact]
    public void The_same_schedule_at_a_tier_that_can_reach_a_diff_is_saved()
    {
        var result = Check(Autonomous);

        Assert.Empty(result.Problems);
        Assert.NotNull(result.Schedule);
    }

    // ── refusing only what is actually broken ───────────────────────────────

    /// <summary>
    /// ANY of the alternatives is enough. A run with a shell can produce a diff whether or not the
    /// git tool is available, and a check that demanded all three would refuse a schedule that
    /// works.
    /// </summary>
    [Fact]
    public void One_way_to_do_the_job_is_enough()
    {
        var withoutGit = AutonomyTiers.PolicyFor(Autonomous) with
        {
            Deny = new[] { "git" }
        };

        Assert.Empty(TemplateNeeds.Unmet(Review, withoutGit, approvalIsPossible: false));
    }

    /// <summary>
    /// A template that declares nothing cannot start refusing schedules. Most of them declare
    /// nothing, and a default that objected would make this change a bug in every one of them.
    /// </summary>
    [Fact]
    public void A_template_that_declares_no_needs_is_never_refused_for_them()
    {
        var plain = new TaskTemplate(Id: "tidy", Name: "Tidy up", Goal: "tidy the folder");

        Assert.Empty(plain.Needs);
        Assert.Empty(TemplateNeeds.Unmet(
            plain, new PermissionPolicy(PermissionLevel.Observe, Array.Empty<string>(), Array.Empty<string>()),
            approvalIsPossible: false));
    }

    /// <summary>
    /// Someone IS there: the same template and the same tier, checked for a watched run, is fine —
    /// because the shells would be asked about and the person can say yes. The unattended answer
    /// must not leak into the interactive one.
    /// </summary>
    [Fact]
    public void A_watched_run_can_do_what_a_schedule_cannot()
    {
        var execute = AutonomyTiers.PolicyFor(Execute);

        Assert.NotEmpty(TemplateNeeds.Unmet(Review, execute, approvalIsPossible: false));
        Assert.Empty(TemplateNeeds.Unmet(Review, execute, approvalIsPossible: true));
    }

    /// <summary>
    /// The template's OWN ceiling counts, not just the tier. A template that denies every tool its
    /// job needs cannot work at any tier, and the policy checked has to be the narrowed one the run
    /// will actually use — checking the workspace's would pass it.
    /// </summary>
    [Fact]
    public void A_template_that_denies_what_it_needs_is_refused_at_every_tier()
    {
        var contradictory = Review with
        {
            Id = "contradictory",
            Permissions = new PermissionCeiling(
                Deny: new[] { "git", "run_command", "run_powershell" })
        };

        var problem = Assert.Single(TemplateNeeds.Unmet(
            contradictory,
            TemplateResolution.Narrow(
                AutonomyTiers.PolicyFor(Autonomous), contradictory.Ceiling),
            approvalIsPossible: false));

        // A denial, not an unanswered question - so the advice must not be "raise the tier".
        Assert.Contains(ToolOffers.Blocked, problem, StringComparison.Ordinal);
        Assert.DoesNotContain("choose a tier", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The over-refusal this check must not commit. Code Review needs a diff only because its scope
    /// DEFAULTS to one; somebody who has already pointed it at a folder needs nothing of the sort,
    /// and refusing them would be the same failure as accepting a schedule that cannot work, only
    /// more irritating.
    /// </summary>
    [Fact]
    public void A_schedule_that_changed_the_parameter_the_need_depends_on_is_not_refused()
    {
        var draft = new ScheduleDraft(
            "nightly", _root,
            ScheduledWork.FromTemplate("code-review", new Dictionary<string, string>
            {
                ["scope"] = "the Widgets folder"
            }),
            ScheduleTiming.Daily(new TimeOnly(3, 0), "UTC"),
            AutonomyTiers.PolicyFor(Execute));

        var result = ScheduleDrafts.Check(draft, _workspace, DateTimeOffset.Now, Find);

        Assert.Empty(result.Problems);
        Assert.NotNull(result.Schedule);
    }

    /// <summary>
    /// And leaving the parameter alone is still the case that IS refused. Without this the test
    /// above passes over a check that has been switched off.
    /// </summary>
    [Fact]
    public void Leaving_the_parameter_at_its_default_is_still_refused()
    {
        var draft = new ScheduleDraft(
            "nightly", _root,
            ScheduledWork.FromTemplate("code-review", new Dictionary<string, string>
            {
                ["scope"] = Review.ParameterList.Single(p => p.Id == "scope").Default!
            }),
            ScheduleTiming.Daily(new TimeOnly(3, 0), "UTC"),
            AutonomyTiers.PolicyFor(Execute));

        Assert.NotEmpty(ScheduleDrafts.Check(draft, _workspace, DateTimeOffset.Now, Find).Problems);
    }

    // ── what is already stored, not only what is being typed ────────────────

    /// <summary>
    /// The half the first version of this left out. A schedule saved before the check existed keeps
    /// firing every night; the window's refusal only ever reached schedules somebody happened to
    /// open again, and a schedule nobody will open again is exactly the kind this product is for.
    /// </summary>
    [Fact]
    public void A_stored_schedule_that_cannot_work_is_not_run()
    {
        var stored = new Schedule(
            Guid.NewGuid(), _root, "nightly review",
            ScheduledWork.FromTemplate("code-review"),
            ScheduleTiming.Daily(new TimeOnly(3, 0), "UTC"),
            AutonomyTiers.PolicyFor(Execute),
            CreatedAt: DateTimeOffset.Now.AddDays(-7));

        // Long past due, so without the needs check this would be a Run.
        var at = DateTimeOffset.Now;

        var decision = ScheduleTick.Decide(stored, at, isRunning: null, findTemplate: Find);

        Assert.Equal(DueVerdict.Unschedulable, decision.Verdict);
        Assert.False(decision.ShouldRun);

        // Told once, rather than found as a failed run each morning.
        Assert.True(decision.WorthReporting);
        Assert.Contains("cannot see what has changed", decision.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same schedule at a tier that can do the job: it runs. The check must stop what cannot work
    /// and nothing else — a tick that refused everything would be a scheduler that never fires,
    /// which is the quietest way for this to go wrong.
    /// </summary>
    [Fact]
    public void A_stored_schedule_that_can_work_still_runs()
    {
        var stored = new Schedule(
            Guid.NewGuid(), _root, "nightly review",
            ScheduledWork.FromTemplate("code-review"),
            ScheduleTiming.Daily(new TimeOnly(3, 0), "UTC"),
            AutonomyTiers.PolicyFor(Autonomous),
            CreatedAt: DateTimeOffset.Now.AddDays(-7));

        var decision = ScheduleTick.Decide(stored, DateTimeOffset.Now, isRunning: null, findTemplate: Find);

        Assert.NotEqual(DueVerdict.Unschedulable, decision.Verdict);
    }

    /// <summary>
    /// A caller with no way to look a template up gets exactly the behaviour it had before. The
    /// parameter is optional so that adding this could not change what any existing tick decided.
    /// </summary>
    [Fact]
    public void A_tick_that_cannot_look_templates_up_decides_as_it_always_did()
    {
        var stored = new Schedule(
            Guid.NewGuid(), _root, "nightly review",
            ScheduledWork.FromTemplate("code-review"),
            ScheduleTiming.Daily(new TimeOnly(3, 0), "UTC"),
            AutonomyTiers.PolicyFor(Execute),
            CreatedAt: DateTimeOffset.Now.AddDays(-7));

        Assert.NotEqual(
            DueVerdict.Unschedulable,
            ScheduleTick.Decide(stored, DateTimeOffset.Now).Verdict);
    }

    // ── the assumption this rests on, guarded ───────────────────────────────

    /// <summary>
    /// The schedules window carries no tool registry, so <see cref="TemplateNeeds.Unmet"/> falls
    /// back to <see cref="PermissionLevel.Execute"/> for every tool — which is what
    /// <c>ToolRegistry</c> itself answers for a name it does not know.
    ///
    /// <para>That fallback is right for every tool the built-ins name, and wrong for the read-only
    /// ones, which are Observe. So the day somebody declares a need on <c>read_file</c> this fails
    /// rather than quietly refusing schedules at the Observe tier that would have run perfectly
    /// well. An assumption with a test on it is a decision; without one it is a guess that used to
    /// be true.</para>
    /// </summary>
    [Fact]
    public void Every_tool_a_shipped_template_names_really_does_require_execute()
    {
        var registry = new ToolRegistry(EngineFixture.ShippedTools());

        foreach (var template in BuiltinTemplates.All)
        foreach (var need in template.Needs)
        foreach (var tool in need.AnyOf)
        {
            Assert.Equal(PermissionLevel.Execute, registry.RequiredLevelOf(tool));
        }
    }

    /// <summary>
    /// And every tool named is one that exists. A need naming a typo would silently never be met,
    /// which turns this whole check into a template nobody can schedule.
    /// </summary>
    [Fact]
    public void Every_tool_a_shipped_template_names_is_a_tool_that_exists()
    {
        var registered = new ToolRegistry(EngineFixture.ShippedTools())
            .Definitions.Select(d => d.Name).ToArray();

        foreach (var template in BuiltinTemplates.All)
        foreach (var need in template.Needs)
        foreach (var tool in need.AnyOf)
        {
            Assert.Contains(tool, registered);
        }
    }
}
