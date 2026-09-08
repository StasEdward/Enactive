namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Xunit;

/// <summary>
/// The parallel dispatcher — a channel fan-in, two locks, a semaphore and a keep-N-in-flight loop,
/// which shipped covered by NOTHING.
///
/// <para>Recorded in <c>FIX_PLAN.md</c> §9d as the most dangerous code in the engine, and the reason
/// it stayed uncovered was not that anybody decided to skip it: <see cref="EngineFixture.Build"/>
/// had no way to set <c>MaxParallelSteps</c>, so all 762 tests ran the <c>== 1</c> branch because
/// that was the only branch the harness could reach. A setting no test can reach is a setting no
/// test covers, however many tests there are.</para>
///
/// <para>It matters more after 2026-09-08 than before it: N1 of that day's review — a revert
/// reaching past a sibling's accepted write — is only reachable with two steps in flight, so the fix
/// for it had been proved at the store's level and never once through the engine in the
/// configuration it exists for. The last test here is that case, end to end.</para>
///
/// <para>These use <see cref="ByStepChatProvider"/>, not <see cref="FakeChatProvider"/>: a scripted
/// queue answers by POSITION, which means nothing once two steps interleave.</para>
/// </summary>
public sealed class ParallelStepTests
{
    // Two independent steps and one that waits for both — the smallest plan with a branch, a join
    // and something for the dispatcher to actually schedule.
    private const string Diamond = """
        {"disposition":"task","title":"Two branches",
         "steps":[{"title":"Left","dependsOn":[]},
                  {"title":"Right","dependsOn":[]},
                  {"title":"Join","dependsOn":[0,1]}]}
        """;

    private const string TwoBranches = """
        {"disposition":"task","title":"Two branches",
         "steps":[{"title":"Left","dependsOn":[]},
                  {"title":"Right","dependsOn":[]}]}
        """;

    private static Turn Writes(string path, string content)
        => Turn.Calls1("write_file", $$"""{"path":"{{path}}","content":"{{content}}"}""", "w-" + path);

    private static IEnumerable<WorkEvent> StepsDone(List<WorkEvent> events)
        => events.Where(e => e.Kind == EventKind.StepCompleted);

    // ── that it runs at all ─────────────────────────────────────────────────

    /// <summary>
    /// The smoke test the branch never had: three steps, two of them concurrent, all finishing, in
    /// one ordered event stream. If the fan-in deadlocks or drops the channel this hangs or comes
    /// back short, and until now nothing would have said so.
    /// </summary>
    [Fact]
    public async Task A_plan_with_a_branch_and_a_join_completes_with_two_in_flight()
    {
        using var fixture = new EngineFixture();

        var provider = new ByStepChatProvider(Diamond)
            .Step("Left", Writes("left.txt", "from the left"), Turn.Says("Left done."))
            .Step("Right", Writes("right.txt", "from the right"), Turn.Says("Right done."))
            .Step("Join", Writes("join.txt", "both"), Turn.Says("Joined."));

        var events = await fixture.RunAsync(
            fixture.Build(provider, maxParallelSteps: 2), "do both branches");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(3, StepsDone(events).Count());
        Assert.All(StepsDone(events), e => Assert.Equal(StepOutcomeKind.Succeeded, e.StepOutcome()));

        Assert.True(fixture.Exists("left.txt"));
        Assert.True(fixture.Exists("right.txt"));
        Assert.True(fixture.Exists("join.txt"));

        // And they really did overlap — otherwise this whole file is testing the serial path twice.
        Assert.True(provider.PeakConcurrency > 1,
            "the two independent steps never overlapped; nothing here is exercising the dispatcher");
    }

    /// <summary>
    /// At the shipping default of 1 nothing overlaps, and the same plan produces the same result.
    /// The guard on the guard: a dispatcher that ignored the setting would pass every other test in
    /// this file.
    /// </summary>
    [Fact]
    public async Task At_the_default_of_one_nothing_overlaps()
    {
        using var fixture = new EngineFixture();

        var provider = new ByStepChatProvider(Diamond)
            .Step("Left", Writes("left.txt", "from the left"), Turn.Says("Left done."))
            .Step("Right", Writes("right.txt", "from the right"), Turn.Says("Right done."))
            .Step("Join", Writes("join.txt", "both"), Turn.Says("Joined."));

        var events = await fixture.RunAsync(fixture.Build(provider), "do both branches");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(1, provider.PeakConcurrency);
    }

    // ── whose conversation is whose ─────────────────────────────────────────

    /// <summary>
    /// Two steps cannot append to one message list, so above degree 1 each gets its own fork. What
    /// that has to mean, and what this checks, is that a step never sees the other's transcript:
    /// a fork that accidentally shared the list would still "work" and would quietly double every
    /// prompt while anchoring each branch on its sibling's tool calls.
    /// </summary>
    [Fact]
    public async Task A_parallel_step_does_not_see_its_siblings_transcript()
    {
        using var fixture = new EngineFixture();

        var provider = new ByStepChatProvider(TwoBranches)
            .Step("Left", Writes("left.txt", "LEFT-MARKER"), Turn.Says("Left done."))
            .Step("Right", Writes("right.txt", "RIGHT-MARKER"), Turn.Says("Right done."));

        await fixture.RunAsync(fixture.Build(provider, maxParallelSteps: 2), "do both branches");

        foreach (var request in provider.RequestsFor("Right"))
        {
            var whole = string.Join("\n", request.Messages.Select(m => m.Content ?? ""));
            Assert.DoesNotContain("LEFT-MARKER", whole);
            Assert.DoesNotContain("left.txt", whole);
        }
    }

    /// <summary>
    /// And what a later step DOES get: one line per finished sibling, not their transcripts. The
    /// digest is the whole point of forking rather than sharing — a branch has to know what its
    /// siblings concluded without paying for how they got there.
    /// </summary>
    [Fact]
    public async Task A_later_step_is_told_what_its_siblings_concluded_and_not_how()
    {
        using var fixture = new EngineFixture();

        var provider = new ByStepChatProvider(Diamond)
            .Step("Left", Writes("left.txt", "x"), Turn.Says("The parser now handles empty input."))
            .Step("Right", Writes("right.txt", "y"), Turn.Says("The formatter was left alone."))
            .Step("Join", Turn.Says("Joined."));

        await fixture.RunAsync(fixture.Build(provider, maxParallelSteps: 2), "do both branches");

        var join = provider.RequestsFor("Join").First();
        var whole = string.Join("\n", join.Messages.Select(m => m.Content ?? ""));

        Assert.Contains("Earlier steps of this plan are already finished", whole);
        Assert.Contains("The parser now handles empty input", whole);
        Assert.Contains("The formatter was left alone", whole);

        // The conclusions, not the work: no tool call of theirs is in here.
        Assert.DoesNotContain("write_file", whole);
    }

    // ── the shared state ────────────────────────────────────────────────────

    /// <summary>
    /// The artifact list is one list written by every step in flight. Twelve writes across two
    /// concurrent branches must all arrive: a torn <c>List.Add</c> loses entries silently, and what
    /// it loses is the record a revert and a review are built from.
    /// </summary>
    [Fact]
    public async Task Concurrent_steps_do_not_lose_each_others_artifacts()
    {
        using var fixture = new EngineFixture();

        Turn[] SixWrites(string side) =>
            Enumerable.Range(1, 6).Select(i => Writes($"{side}-{i}.txt", $"{side} {i}")).ToArray();

        var provider = new ByStepChatProvider(TwoBranches)
            .Step("Left", SixWrites("left").Append(Turn.Says("Left done.")).ToArray())
            .Step("Right", SixWrites("right").Append(Turn.Says("Right done.")).ToArray());

        var events = await fixture.RunAsync(
            fixture.Build(provider, maxParallelSteps: 2), "write a lot from both branches");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());

        for (var i = 1; i <= 6; i++)
        {
            Assert.True(fixture.Exists($"left-{i}.txt"), $"left-{i}.txt is missing");
            Assert.True(fixture.Exists($"right-{i}.txt"), $"right-{i}.txt is missing");
        }

        Assert.Equal(12, events.Count(e => e.Kind == EventKind.ArtifactProduced));
    }

    /// <summary>
    /// Approval cards are serialised behind a semaphore, because a person cannot answer two
    /// questions at the same moment. Checked from inside the handler: the gate is the thing callers
    /// wait on, so only the thing being called can say whether two were ever inside it together.
    /// </summary>
    [Fact]
    public async Task Two_branches_never_put_two_approval_cards_up_at_once()
    {
        using var fixture = new EngineFixture();

        var asks = new PermissionPolicy(
            PermissionLevel.Execute,
            Allow: new[] { "*" },
            AskBefore: new[] { "run_command" });

        Turn Runs(string marker)
            => Turn.Calls1("run_command", $$"""{"command":"cmd /c echo {{marker}}"}""", "c-" + marker);

        var provider = new ByStepChatProvider(TwoBranches)
            .Step("Left", Runs("left-one"), Runs("left-two"), Turn.Says("Left done."))
            .Step("Right", Runs("right-one"), Runs("right-two"), Turn.Says("Right done."));

        var decisions = new ConcurrencyWatchingDecisions();

        await fixture.RunAsync(
            fixture.Build(provider, policy: asks, maxParallelSteps: 2, decisions: decisions),
            "run something on both branches");

        Assert.Equal(4, decisions.Requests.Count);
        Assert.Equal(1, decisions.PeakOpenCards);
    }

    // ── which card an event belongs to ──────────────────────────────────────

    /// <summary>
    /// Step numbers are PLAN positions, not a dispatch counter. The UI resolves an event to its card
    /// by this number and its cards come from the plan in plan order, so the moment readiness order
    /// differs from plan order — which is every parallel run — a counter would point at the wrong
    /// card. Here the second branch does less work and finishes first; its events must still say 2.
    /// </summary>
    [Fact]
    public async Task An_event_carries_its_plan_position_not_the_order_it_finished_in()
    {
        using var fixture = new EngineFixture();

        var provider = new ByStepChatProvider(TwoBranches)
            .Step("Left",
                Writes("l1.txt", "a"), Writes("l2.txt", "b"), Writes("l3.txt", "c"),
                Turn.Says("Left done."))
            .Step("Right", Turn.Says("Right done, and first."));

        var events = await fixture.RunAsync(
            fixture.Build(provider, maxParallelSteps: 2), "one long branch and one short");

        var right = Assert.Single(StepsDone(events), e => e.Summary.Contains("Right"));
        var left = Assert.Single(StepsDone(events), e => e.Summary.Contains("Left"));

        Assert.Equal(2, right.StepNo());
        Assert.Equal(1, left.StepNo());

        // Every artifact of the long branch is attributed to step 1, whichever step finished first.
        Assert.All(
            events.Where(e => e.Kind == EventKind.ArtifactProduced),
            e => Assert.Equal(1, e.StepNo()));
    }

    // ── failure, in parallel ────────────────────────────────────────────────

    /// <summary>
    /// One branch failing must skip what depends on it and leave the other branch alone. The cascade
    /// runs from inside a step task while another step is still going, which is the interesting part:
    /// it touches the scheduler and the outcome map from a second thread.
    /// </summary>
    [Fact]
    public async Task A_failing_branch_skips_its_dependents_and_spares_its_sibling()
    {
        using var fixture = new EngineFixture();

        const string plan = """
            {"disposition":"task","title":"One good branch, one bad",
             "steps":[{"title":"Bad","dependsOn":[]},
                      {"title":"Good","dependsOn":[]},
                      {"title":"After the bad one","dependsOn":[0]}]}
            """;

        var provider = new ByStepChatProvider(plan)
            .Step("Bad",
                Turn.Calls1("read_file", """{"path":"nowhere.txt"}""", "miss"),
                Turn.Says("I could not find it."))
            .Step("Good", Writes("good.txt", "fine"), Turn.Says("Good done."))
            .Step("After the bad one", Turn.Says("Should never run."));

        var events = await fixture.RunAsync(
            fixture.Build(provider, maxParallelSteps: 2), "run both branches");

        var good = Assert.Single(StepsDone(events), e => e.Summary.Contains("Good"));
        Assert.Equal(StepOutcomeKind.Succeeded, good.StepOutcome());
        Assert.True(fixture.Exists("good.txt"));

        var skipped = Assert.Single(StepsDone(events), e => e.Summary.Contains("After the bad one"));
        Assert.Equal(StepOutcomeKind.Skipped, skipped.StepOutcome());

        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
    }

    /// <summary>
    /// Two branches writing ONE file. Found by writing the test above: they collided on the
    /// filesystem — <c>AtomicWrite</c> moves the new content over the target, and on Windows two
    /// moves onto one path make the loser fail with "Access to the path is denied". A Win32 message
    /// no model can act on, and a step marked Incomplete for a collision the engine itself caused.
    ///
    /// <para>The store serialises a path's writes now, which also closes the worse version of the
    /// same race: the sequence number was taken after the write under a different lock, so two
    /// writes could land on disk in one order and be journalled in the other — and every revert
    /// decision is read off that order.</para>
    /// </summary>
    [Fact]
    public async Task Two_branches_writing_one_file_do_not_collide_on_disk()
    {
        using var fixture = new EngineFixture();
        fixture.Write("shared.txt", "original");

        Turn[] FiveWrites(string side) =>
            Enumerable.Range(1, 5)
                .Select(i => Writes("shared.txt", $"{side} {i}"))
                .ToArray();

        var provider = new ByStepChatProvider(TwoBranches)
            .Step("Left", FiveWrites("left").Append(Turn.Says("Left done.")).ToArray())
            .Step("Right", FiveWrites("right").Append(Turn.Says("Right done.")).ToArray());

        var events = await fixture.RunAsync(
            fixture.Build(provider, maxParallelSteps: 2), "both branches write the same file");

        Assert.DoesNotContain(
            events,
            e => e.Kind == EventKind.ToolResult && e.Summary.Contains("Access to the path is denied"));

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(10, events.Count(e => e.Kind == EventKind.ArtifactProduced));
    }

    // ── N1, in the configuration it exists for ──────────────────────────────

    /// <summary>
    /// The 2026-09-08 review's N1, end to end. Two concurrent steps write the same file; one is
    /// rejected and reverts. Its revert must not reach past the other step's accepted write.
    ///
    /// <para>Until this test the fix for it was proved at the artifact store's level and never once
    /// through the engine — because the engine could not be built in the only configuration where
    /// the defect exists. That is the shape of the gap this whole file is closing.</para>
    /// </summary>
    [Fact]
    public async Task A_rejected_branch_does_not_revert_over_its_siblings_accepted_write()
    {
        using var fixture = new EngineFixture();
        fixture.Write("shared.txt", "original");

        var provider = new ByStepChatProvider(TwoBranches)
            .Step("Left",
                Writes("shared.txt", "left first"),
                Writes("shared.txt", "left second"),
                Turn.Says("Left done."))
            .Step("Right", Writes("shared.txt", "right accepted"), Turn.Says("Right done."));

        // Left is rejected and reverts; Right is accepted. The reviewer answers by step title, so
        // this is deterministic however the two interleave.
        var reviewer = new VerdictByStepProvider()
            .On("Left", Verdicts.Fail("the left branch did not do what it claimed"));

        var events = await fixture.RunAsync(
            fixture.Build(
                provider, router: Routers.WithReviewer(), reviewProvider: reviewer,
                reviewRetries: 0, maxParallelSteps: 2),
            "both branches write the same file");

        // The review really did happen, and really did reject Left.
        Assert.Contains("Left", reviewer.Asked);
        Assert.Contains(
            events.Where(e => e.Kind == EventKind.StepCompleted),
            e => e.Summary.Contains("Left") && e.StepOutcome() == StepOutcomeKind.ReviewRejected);

        // "original" is what N1 produced: content belonging to no step at all, because the rejected
        // branch restored what its FIRST write displaced and took the sibling's accepted write with
        // it. Either surviving value is correct - Left's revert may find the file already changed
        // under it and decline, or it may put back what it displaced, which is Right's write.
        var content = fixture.Read("shared.txt");
        Assert.True(
            content is "left second" or "right accepted",
            $"shared.txt holds '{content}', which no step wrote");
    }
}
