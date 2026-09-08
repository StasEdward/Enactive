namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// A failed check gets the agent a chance to fix it, and then the check decides again.
///
/// <para>Until 2026-09-08 the criteria ran once and that was the end of the run.
/// <c>TASK_TEMPLATES_PLAN.md</c> listed the verification LOOP as "not in M2" and the reason given
/// was honest — re-entering the plan is a real change to the shape of a run — but the gap showed up
/// in practice on 2026-09-07 at 23:20: a run left a test file un-compilable, and the <c>Builds</c>
/// criterion would have caught it with nothing behind it to act on. Being told the build is broken
/// is not what "done" means to anybody.</para>
///
/// <para>What ships is deliberately narrower than "re-run the plan": ONE tool loop, in its own
/// artifact scope, told exactly what failed and given the check's own output. It does not get to
/// declare victory — the criteria are re-run afterwards and they alone decide.</para>
/// </summary>
public sealed class VerificationLoopTests
{
    private static WorkEvent Terminal(List<WorkEvent> events)
        => events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed);

    /// <summary>
    /// A check that passes only once the named file is there. No nested "cmd /c": run_command
    /// already runs through the shell, and a second level of it re-quotes anything with parentheses
    /// into something that always fails - which is a good imitation of a check that cannot be
    /// satisfied, and cost half an hour to tell apart from a broken repair loop.
    /// </summary>
    private static SuccessCriterionDefinition ChecksFor(string path, string name = "Builds")
        => new(
            name,
            OperatingSystem.IsWindows() ? $"dir /b {path}" : $"test -f {path}",
            0,
            Required: true);

    private static SuccessCriterionDefinition Always(int exitCode, string name = "Builds", bool required = true)
        => new(name, OperatingSystem.IsWindows() ? $"cmd /c exit {exitCode}" : $"exit {exitCode}", 0, required);

    private static int Checks(List<WorkEvent> events)
        => events.Count(e => e.Kind == EventKind.CriterionEvaluated);

    // ── the point of it ─────────────────────────────────────────────────────

    /// <summary>
    /// The one that matters: the run finishes with the check failing, the agent is given the failure
    /// and fixes it, and the re-check passes — so the run is Completed on the strength of a check,
    /// not of anybody's claim.
    /// </summary>
    [Fact]
    public async Task A_run_whose_check_failed_is_given_a_chance_to_fix_it()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Calls1("write_file", """{"path":"other.txt","content":"not the one"}"""),
            Turn.Says("All done."),
            // The repair attempt: told what failed, it writes the file the check looks for.
            Turn.Calls1("write_file", """{"path":"built.txt","content":"now it builds"}"""),
            Turn.Says("Fixed the cause."));

        var events = await fx.RunAsync(
            fx.Build(provider, successCriteria: new[] { ChecksFor("built.txt") }, successRetries: 1),
            "do the thing");

        Assert.Equal(RunOutcomeKind.Completed, Terminal(events).Outcome());
        Assert.True(fx.Exists("built.txt"));

        // Checked twice: once to find the failure, once to confirm the fix. Both are in the record,
        // because a report that shows only the final state cannot answer what was repaired.
        Assert.Equal(2, Checks(events));
    }

    /// <summary>
    /// And the repair does not get to declare victory. The agent says it fixed the problem, the
    /// check says otherwise, and the check wins — which is the whole reason criteria exist.
    /// </summary>
    [Fact]
    public async Task A_repair_that_did_not_work_still_fails_the_run()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Says("All done."),
            Turn.Says("I have fixed it, everything passes now."))
        {
            WhenExhausted = Turn.Says("Nothing more to do.")
        };

        var events = await fx.RunAsync(
            fx.Build(provider, successCriteria: new[] { Always(1) }, successRetries: 1),
            "do the thing");

        Assert.Equal(RunOutcomeKind.Failed, Terminal(events).Outcome());
        Assert.Contains("Builds", Terminal(events).OutcomeReason());
        Assert.Contains(events, e => (e.Summary ?? "").Contains("I have fixed it"));
    }

    /// <summary>The repair is told the check's own output, not merely that something failed.</summary>
    [Fact]
    public async Task The_repair_is_told_which_check_failed_and_what_it_ran()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Says("All done."))
        {
            WhenExhausted = Turn.Says("I cannot fix that.")
        };

        await fx.RunAsync(
            fx.Build(provider, successCriteria: new[] { Always(1, "Solution builds") }, successRetries: 1),
            "do the thing");

        // The last request is the repair's; it must name the check and carry its command.
        var repair = string.Join("\n", provider.Requests[^1].Messages.Select(m => m.Content ?? ""));

        Assert.Contains("Solution builds", repair);
        Assert.Contains("did NOT pass", repair);
        Assert.Contains("exit 1", repair);

        // And it is told not to make the check pass by changing the check.
        Assert.Contains("Do not change the check itself", repair);
    }

    // ── the bounds ──────────────────────────────────────────────────────────

    /// <summary>Zero retries is the behaviour this had before: check once, and stop.</summary>
    [Fact]
    public async Task At_zero_retries_a_failed_check_ends_the_run_as_it_always_did()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Says("All done."))
        {
            WhenExhausted = Turn.Says("still here")
        };

        var events = await fx.RunAsync(
            fx.Build(provider, successCriteria: new[] { Always(1) }, successRetries: 0),
            "do the thing");

        Assert.Equal(RunOutcomeKind.Failed, Terminal(events).Outcome());
        Assert.Equal(1, Checks(events));
    }

    /// <summary>A run whose checks pass is not given a repair it does not need.</summary>
    [Fact]
    public async Task A_passing_check_does_not_start_a_repair()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Says("All done."))
        {
            WhenExhausted = Turn.Says("nothing to do")
        };

        var events = await fx.RunAsync(
            fx.Build(provider, successCriteria: new[] { Always(0) }, successRetries: 3),
            "do the thing");

        Assert.Equal(RunOutcomeKind.Completed, Terminal(events).Outcome());
        Assert.Equal(1, Checks(events));
    }

    /// <summary>
    /// An OPTIONAL check never holds a run back, so it never buys a repair either — otherwise every
    /// check somebody adds out of curiosity becomes a way to spend their budget.
    /// </summary>
    [Fact]
    public async Task An_optional_check_that_failed_does_not_start_a_repair()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Says("All done."))
        {
            WhenExhausted = Turn.Says("nothing to do")
        };

        var events = await fx.RunAsync(
            fx.Build(
                provider,
                successCriteria: new[] { Always(1, "Nice to have", required: false) },
                successRetries: 3),
            "do the thing");

        Assert.Equal(RunOutcomeKind.Completed, Terminal(events).Outcome());
        Assert.Equal(1, Checks(events));
    }

    /// <summary>
    /// A check that could not be EVALUATED is not something the agent can fix by working harder —
    /// the command does not exist, or policy refused it — so it does not buy a repair. Retrying it
    /// would spend a whole tool loop learning the same thing.
    /// </summary>
    [Fact]
    public async Task A_check_that_could_not_be_evaluated_does_not_start_a_repair()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Says("All done."))
        {
            WhenExhausted = Turn.Says("nothing to do")
        };

        // Denied by policy: run_command is not on the allow list, so the check is Unknown.
        var policy = new PermissionPolicy(
            PermissionLevel.Execute,
            Allow: new[] { "write_file" },
            AskBefore: Array.Empty<string>())
        {
            Deny = new[] { "run_command" }
        };

        var events = await fx.RunAsync(
            fx.Build(provider, policy: policy, successCriteria: new[] { Always(0) }, successRetries: 3),
            "do the thing");

        Assert.NotEqual(RunOutcomeKind.Completed, Terminal(events).Outcome());
        Assert.Equal(1, Checks(events));
    }

    /// <summary>
    /// Three attempts means three, and the last word is still the check's. A repair loop that could
    /// not stop would be a worse defect than the one it fixes.
    /// </summary>
    [Fact]
    public async Task The_repair_attempts_are_bounded_by_the_setting()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Says("All done."))
        {
            WhenExhausted = Turn.Says("I tried.")
        };

        var events = await fx.RunAsync(
            fx.Build(provider, successCriteria: new[] { Always(1) }, successRetries: 3),
            "do the thing");

        Assert.Equal(RunOutcomeKind.Failed, Terminal(events).Outcome());

        // One evaluation, then one per attempt.
        Assert.Equal(4, Checks(events));
    }
}
