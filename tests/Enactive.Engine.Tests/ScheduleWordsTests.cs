namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Enactive.Core.Schedules;
using Xunit;

/// <summary>
/// What a person is shown before they agree to a schedule.
///
/// <para>A schedule is an approval given in advance for every future run: they will not be asked
/// again, they will not be there, and it repeats. So the test of these strings is not whether they
/// read nicely — it is whether somebody who acted only on them would be surprised later.</para>
/// </summary>
public sealed class ScheduleWordsTests
{
    private static Schedule With(
        ScheduleTiming timing, PermissionPolicy? policy = null,
        MissedRun missed = MissedRun.Skip, bool enabled = true)
        => new(Guid.NewGuid(), @"C:\work", "nightly",
               ScheduledWork.FromTemplate("tidy"), timing,
               policy ?? PermissionPolicy.PermissiveDefault,
               DateTimeOffset.Now.AddDays(-1), missed, enabled);

    // ── when ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_daily_schedule_says_the_time_and_the_zone()
    {
        var words = ScheduleWords.When(ScheduleTiming.Daily(new TimeOnly(3, 0), "UTC"));

        Assert.Contains("Every day at 03:00", words);
        Assert.Contains("UTC", words);
    }

    [Fact]
    public void A_weekly_schedule_says_which_day()
    {
        var words = ScheduleWords.When(ScheduleTiming.Weekly(DayOfWeek.Tuesday, new TimeOnly(9, 0), "UTC"));

        Assert.Contains("Every Tuesday at 09:00", words);
    }

    /// <summary>
    /// The zone travels with the schedule, so a machine that has never heard of it says so instead
    /// of quietly showing a time in some other zone. A wrong time shown confidently is worse than
    /// an admission.
    /// </summary>
    [Fact]
    public void An_unknown_zone_is_admitted_rather_than_shown_as_a_time()
    {
        var words = ScheduleWords.When(
            ScheduleTiming.Daily(new TimeOnly(3, 0), "Mars Standard Time"));

        Assert.Contains("unknown on this machine", words);
    }

    /// <summary>
    /// A one-off with no instant cannot fire. Saying "Once" and stopping would read as a schedule
    /// that is set, and the person would find out by the thing never happening.
    /// </summary>
    [Fact]
    public void A_one_off_with_no_date_says_it_will_never_run()
    {
        var timing = new ScheduleTiming(ScheduleRepeat.Once, new TimeOnly(9, 0), "UTC");

        Assert.Contains("never run", ScheduleWords.When(timing));
    }

    /// <summary>Same for a weekly schedule with no day: it is not a weekly schedule yet.</summary>
    [Fact]
    public void A_weekly_schedule_with_no_day_says_so()
    {
        var timing = new ScheduleTiming(ScheduleRepeat.Weekly, new TimeOnly(9, 0), "UTC");

        Assert.Contains("no day was chosen", ScheduleWords.When(timing));
    }

    // ── what it may do ──────────────────────────────────────────────────────

    [Fact]
    public void The_tier_is_a_sentence_not_a_number()
    {
        var lines = ScheduleWords.MayDo(PermissionPolicy.PermissiveDefault);

        Assert.Contains(lines, l => l.Contains("Edit files") && l.Contains("run commands"));
        Assert.DoesNotContain(lines, l => l.Contains("Execute"));
    }

    [Fact]
    public void A_read_only_schedule_says_it_cannot_change_anything()
    {
        var policy = new PermissionPolicy(PermissionLevel.Observe, new[] { "*" }, Array.Empty<string>());

        Assert.Contains(ScheduleWords.MayDo(policy), l => l.Contains("cannot change anything"));
    }

    /// <summary>
    /// THE one a person would not guess. Unattended plus "ask first" equals DENY — a scheduled run
    /// has nobody to ask. Showing those tools as "you will be asked" would say the opposite of what
    /// happens, and the person would agree to a schedule believing they would get a say.
    /// </summary>
    [Fact]
    public void Ask_before_tools_are_shown_as_refused_not_as_questions()
    {
        var policy = new PermissionPolicy(
            PermissionLevel.Execute, new[] { "*" }, new[] { "run_command" });

        var line = Assert.Single(ScheduleWords.MayDo(policy), l => l.Contains("run_command"));

        Assert.Contains("Refused", line);
        Assert.Contains("nobody is watching", line);
    }

    [Fact]
    public void A_denied_tool_is_shown_as_never()
    {
        var policy = PermissionPolicy.PermissiveDefault with { Deny = new[] { "git" } };

        Assert.Contains(ScheduleWords.MayDo(policy), l => l.Contains("Never") && l.Contains("git"));
    }

    /// <summary>A narrowed tool list is part of what is being approved, so it is shown.</summary>
    [Fact]
    public void A_narrowed_tool_list_is_named()
    {
        var policy = new PermissionPolicy(
            PermissionLevel.Execute, new[] { "read_file", "write_file" }, Array.Empty<string>());

        Assert.Contains(ScheduleWords.MayDo(policy), l => l.Contains("read_file, write_file"));
    }

    // ── the machine was off ─────────────────────────────────────────────────

    [Fact]
    public void Both_answers_to_a_missed_run_are_stated()
    {
        Assert.Contains("skipped", ScheduleWords.IfItWasMissed(MissedRun.Skip));
        Assert.Contains("as soon as the machine is back", ScheduleWords.IfItWasMissed(MissedRun.RunLate));
    }

    // ── the next run ────────────────────────────────────────────────────────

    [Fact]
    public void A_disabled_schedule_says_it_will_not_run()
    {
        var schedule = With(ScheduleTiming.Daily(new TimeOnly(3, 0), "UTC"), enabled: false);

        Assert.Contains("Disabled", ScheduleWords.NextRun(schedule, DateTimeOffset.Now));
    }

    [Fact]
    public void A_schedule_in_an_unknown_zone_says_nothing_can_be_timed()
    {
        var schedule = With(ScheduleTiming.Daily(new TimeOnly(3, 0), "Mars Standard Time"));

        Assert.Contains("does not know the time zone", ScheduleWords.NextRun(schedule, DateTimeOffset.Now));
    }

    [Fact]
    public void A_one_off_whose_time_has_passed_says_there_is_no_next_run()
    {
        var schedule = With(ScheduleTiming.Once(DateTimeOffset.Now.AddDays(-3)));

        Assert.Contains("no next run", ScheduleWords.NextRun(schedule, DateTimeOffset.Now));
    }

    [Fact]
    public void An_ordinary_schedule_names_its_next_run()
    {
        var schedule = With(ScheduleTiming.Daily(new TimeOnly(3, 0), "UTC"));

        Assert.StartsWith("Next run:", ScheduleWords.NextRun(schedule, DateTimeOffset.Now));
    }

    // ── the whole of it ─────────────────────────────────────────────────────

    /// <summary>
    /// The approval carries all four things. A window free to show three of them would show three of
    /// them, and the one left out would be whichever was least convenient to lay out.
    /// </summary>
    [Fact]
    public void The_approval_says_when_next_what_if_missed_and_what_it_may_do()
    {
        var policy = new PermissionPolicy(PermissionLevel.Execute, new[] { "*" }, new[] { "git" })
        {
            Deny = new[] { "docker" }
        };
        var schedule = With(ScheduleTiming.Daily(new TimeOnly(3, 0), "UTC"), policy, MissedRun.RunLate);

        var lines = ScheduleWords.Approval(schedule, DateTimeOffset.Now);

        Assert.Contains(lines, l => l.StartsWith("Every day at 03:00"));
        Assert.Contains(lines, l => l.StartsWith("Next run:"));
        Assert.Contains(lines, l => l.Contains("as soon as the machine is back"));
        Assert.Contains(lines, l => l.Contains("Edit files"));
        Assert.Contains(lines, l => l.Contains("Refused") && l.Contains("git"));
        Assert.Contains(lines, l => l.Contains("Never") && l.Contains("docker"));
    }
}
