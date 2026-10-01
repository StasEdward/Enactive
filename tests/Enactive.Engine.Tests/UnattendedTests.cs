namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Xunit;

/// <summary>
/// M5 of the task-templates plan: a run with nobody watching.
///
/// <para>The rule the whole design rests on is <b>unattended + Ask = Deny</b>. A run at three in the
/// morning has nobody to approve anything, so a policy that says "ask before running commands"
/// means, for that run, "do not run commands" — and the consequence is visible rather than
/// swallowed: the refusal is recorded, and the run comes out Incomplete instead of finishing green
/// over work it never did.</para>
/// </summary>
public sealed class UnattendedTests
{
    private static DecisionRequest Approval(params string[] optionIds)
        => new(Guid.NewGuid(), "Run tool 'run_command'?", "Arguments: rm -rf /",
               optionIds.Select(id => new DecisionOption(id, id)).ToArray(),
               RecommendedOptionId: "allow");

    // ── the rule ────────────────────────────────────────────────────────────

    /// <summary>
    /// The decisive one, and the hole it closes. The console host answered an unanswered prompt with
    /// the RECOMMENDED option, which for a tool approval is "allow": a scheduled run would have
    /// approved every command it was asked about, silently, on the grounds that nobody objected.
    /// Nobody was there to object.
    /// </summary>
    [Fact]
    public async Task An_approval_nobody_can_give_is_refused_not_assumed()
    {
        var handler = new UnattendedDecisionHandler();

        var outcome = await handler.RequestAsync(Approval("allow", "deny"), CancellationToken.None);

        Assert.Equal("deny", outcome.OptionId);
        Assert.NotEqual("allow", outcome.OptionId);
    }

    /// <summary>
    /// A run nobody watched has to be able to say what it refused. Otherwise "the checks did not
    /// run" looks identical to "there were no checks", and a person reads a green-ish report about
    /// work that never happened.
    /// </summary>
    [Fact]
    public async Task Every_refusal_is_kept_for_the_report()
    {
        var handler = new UnattendedDecisionHandler();

        await handler.RequestAsync(Approval("allow", "deny"), CancellationToken.None);
        await handler.RequestAsync(Approval("allow", "deny"), CancellationToken.None);

        Assert.Equal(2, handler.Refusals.Count);
        Assert.All(handler.Refusals, r => Assert.Contains("unattended", r));
    }

    /// <summary>
    /// A fork whose options are named something else still gets a conservative answer: the last
    /// option is the cautious one by construction, so falling back to it cannot accidentally pick
    /// the permissive one.
    /// </summary>
    [Fact]
    public async Task A_fork_with_no_deny_option_still_gets_the_cautious_answer()
    {
        var handler = new UnattendedDecisionHandler();

        var outcome = await handler.RequestAsync(Approval("apply", "skip"), CancellationToken.None);

        Assert.Equal("skip", outcome.OptionId);
    }

    // ── the exit code a scheduler reads ─────────────────────────────────────

    /// <summary>
    /// Failed and Incomplete get DIFFERENT codes, because the distinction is real at three in the
    /// morning: failed means the work is wrong, incomplete means we could not finish or could not
    /// check. A pipeline may well stop for one and only warn on the other, and collapsing both to 1
    /// takes that choice away.
    /// </summary>
    [Theory]
    [InlineData(RunOutcomeKind.Completed, 0)]
    [InlineData(RunOutcomeKind.Failed, 1)]
    [InlineData(RunOutcomeKind.Incomplete, 2)]
    [InlineData(RunOutcomeKind.Cancelled, 130)]
    public void The_exit_code_says_which_kind_of_not_done_it_was(RunOutcomeKind outcome, int expected)
        => Assert.Equal(expected, RunReport.ExitCodeFor(outcome));

    [Fact]
    public void Only_a_completed_run_exits_zero()
    {
        foreach (var outcome in Enum.GetValues<RunOutcomeKind>())
            if (outcome != RunOutcomeKind.Completed)
                Assert.NotEqual(0, RunReport.ExitCodeFor(outcome));
    }

    // ── the report ──────────────────────────────────────────────────────────

    private static RunRecord Record(
        RunOutcomeKind outcome, string? reason = null,
        IEnumerable<RunEventRecord>? extra = null)
    {
        var at = DateTimeOffset.Now;
        var events = new List<RunEventRecord>
        {
            new(at, nameof(EventKind.IntentReceived), "Intent: check the release",
                null, WorkEventPayload.RequestPayload("check the release")),
            new(at.AddSeconds(30),
                outcome == RunOutcomeKind.Completed
                    ? nameof(EventKind.TaskCompleted)
                    : nameof(EventKind.TaskFailed),
                outcome.ToString(),
                null, WorkEventPayload.OutcomePayload(outcome, reason))
        };
        if (extra is not null)
            events.InsertRange(1, extra);

        return new RunRecord(
            Guid.NewGuid(), Guid.NewGuid(), "Release Check", "gemma4-12b",
            at, at.AddSeconds(30), outcome.ToString(),
            events, new[] { "web-site/index.html" }, Array.Empty<string>(),
            Usage: new RunUsage(400, 120));
    }

    /// <summary>
    /// The outcome comes from the terminal event's PAYLOAD, not from its wording. A report that read
    /// the summary would say something different the moment that sentence is reworded, which is the
    /// habit the typed payloads exist to end.
    /// </summary>
    [Fact]
    public void The_report_reads_the_outcome_as_a_value()
    {
        var record = Record(RunOutcomeKind.Failed, "1 success criterion(s) failed: Tests pass");

        Assert.Equal(RunOutcomeKind.Failed, RunReport.OutcomeOf(record));

        var report = RunReport.Render(record, @"c:\repos\Enactive");
        Assert.Contains("Failed", report);
        Assert.Contains("Tests pass", report);
        Assert.Contains("web-site/index.html", report);
        Assert.Contains("520", report);   // the tokens, summed
    }

    /// <summary>
    /// "No checks ran" must never look like "everything checked out". It is the single most
    /// misleading thing a report of an unwatched run could imply.
    /// </summary>
    [Fact]
    public void A_run_that_verified_nothing_says_so_in_as_many_words()
    {
        var report = RunReport.Render(Record(RunOutcomeKind.Completed), @"c:\repos");

        Assert.Contains("none", report);
        Assert.Contains("nothing verified this run", report);
    }

    [Fact]
    public void The_checks_that_did_run_are_listed()
    {
        var at = DateTimeOffset.Now;
        var record = Record(RunOutcomeKind.Completed, extra: new[]
        {
            new RunEventRecord(at, nameof(EventKind.CriterionEvaluated),
                "PASS (exit 0) — Builds: dotnet build", null,
                WorkEventPayload.CriterionPayload("Builds", "Passed", true, 0))
        });

        var report = RunReport.Render(record, @"c:\repos");

        Assert.Contains("CHECKS", report);
        Assert.Contains("Builds", report);
        Assert.DoesNotContain("nothing verified this run", report);
    }

    /// <summary>A record from before the payloads still renders - history opens, or it is useless.</summary>
    [Fact]
    public void A_record_with_no_payloads_still_produces_a_report()
    {
        var at = DateTimeOffset.Now;
        var bare = new RunRecord(
            Guid.NewGuid(), Guid.NewGuid(), "an older run", null, at, at, "Completed",
            Array.Empty<RunEventRecord>(), Array.Empty<string>(), Array.Empty<string>());

        var report = RunReport.Render(bare, @"c:\repos");

        Assert.Contains("an older run", report);
        Assert.Contains("Completed", report);
        Assert.Equal(RunOutcomeKind.Completed, RunReport.OutcomeOf(bare));
    }
}
