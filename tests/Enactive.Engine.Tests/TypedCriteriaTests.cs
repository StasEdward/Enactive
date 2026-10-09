namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Xunit;
using WikiLint = NoNewBuildErrorsTests.WikiLint;

/// <summary>
/// Phase 3: the planner states acceptance criteria as types the engine checks itself, added to -
/// never instead of - the run's own criteria; a criterion the engine cannot check is dropped, and the
/// run goes on.
/// </summary>
public sealed class TypedCriteriaTests
{
    private static IReadOnlyList<PlannedCriterion> Planned(string criteria)
    {
        using var doc = JsonDocument.Parse($$"""{"criteria":{{criteria}}}""");
        return TypedCriteria.Read(doc.RootElement);
    }

    // ── validation (3.2) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void What_the_engine_can_check_is_accepted_and_the_rest_dropped_with_its_reason()
    {
        var root = Directory.CreateTempSubdirectory("typed").FullName;
        try
        {
            var (accepted, dropped) = TypedCriteria.Validate(Planned("""
                [{"kind":"file_exists","path":"report.md"},
                 {"kind":"file_contains","path":"report.md","text":"Total"},
                 {"kind":"file_exists","path":"../outside.md"},
                 {"kind":"file_contains","path":"report.md"},
                 {"kind":"semantic","text":"it reads well"},
                 {"kind":"tests_pass"},
                 {"kind":"make_it_good"},
                 "not even an object"]
                """), root, []);

            Assert.Equal(["file_exists report.md (not empty)", "file_contains report.md \"Total\"",
                "the workspace's tests - every test project found when the final checks run"], accepted.Select(c => c.Command).ToArray());
            Assert.All(accepted, c => Assert.Equal(CriterionOrigin.Proposed, c.Origin));   // 3.3: its origin is known
            Assert.Equal(5, dropped.Count);
            Assert.Contains(dropped, d => d.Contains("not inside the workspace", StringComparison.Ordinal));
            Assert.Contains(dropped, d => d.Contains("names no text", StringComparison.Ordinal));
            Assert.Contains(dropped, d => d.Contains("reviewer's to judge", StringComparison.Ordinal));
            Assert.Contains(dropped, d => d.Contains("'make_it_good' is not a kind", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// "The tests pass" names no command when the plan is read, whatever tests there are then: the test projects are
    /// found, and each run by its ecosystem's command, when the final checks run (TheTestsAreFoundWhenTheyAreCheckedTests).
    /// </summary>
    [Fact]
    public void Tests_pass_is_accepted_and_names_the_test_projects_found_when_it_is_checked()
    {
        var root = Directory.CreateTempSubdirectory("typed").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "wiki.lint"), "rules");
            File.WriteAllText(Path.Combine(root, "links.txt"), "PASS a->b");
            var (accepted, _) = TypedCriteria.Validate(Planned("""[{"kind":"tests_pass"},{"kind":"tests_pass","target":"links"}]"""), root, [new WikiLint()]);

            Assert.Equal(["Tests pass", "Tests pass (links)"], accepted.Select(c => c.Name).ToArray());
            Assert.Equal("the tests of links, as found when the final checks run", accepted[1].Command);
            Assert.All(accepted, c => Assert.Equal(TypedCriterionKind.TestsPass, c.Typed!.Kind));
        }
        finally { Directory.Delete(root, true); }
    }

    // ── through a run ────────────────────────────────────────────────────────────────────

    private const string Plan = """
        {"disposition":"quick_action","title":"write the report",
         "criteria":[{"kind":"file_exists","path":"report.md"},{"kind":"file_contains","path":"report.md","text":"Total"},
                     {"kind":"make_it_good"}]}
        """;

    private static WorkEvent[] Checks(IEnumerable<WorkEvent> events)
        => events.Where(e => e.IsCheck()).ToArray();

    /// <summary>
    /// THE ONE THAT MATTERS: the planner's criteria are checked by the engine against what the work
    /// left, and a criterion it wrote wrong costs the run nothing but a line saying so.
    /// </summary>
    [Fact]
    public async Task The_planners_criteria_are_checked_and_a_bad_one_does_not_break_the_run()
    {
        using var fx = new EngineFixture { TypedCriteria = true };
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1("write_file", """{"path":"report.md","content":"# Report\nTotal: 3\n"}""", "w1"),
            Turn.Says("Wrote the report."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "write the report");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(2, Checks(events).Count(e => e.Summary.StartsWith("PASS", StringComparison.Ordinal)));
        Assert.Contains(events, e => e.Summary.Contains("Planner criterion dropped - 'make_it_good'", StringComparison.Ordinal));
    }

    /// <summary>A planner criterion that FAILS holds the run back, like any proposed check that ran and said no.</summary>
    [Fact]
    public async Task A_failed_planner_criterion_holds_the_run_back()
    {
        using var fx = new EngineFixture { TypedCriteria = true };
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1("write_file", """{"path":"report.md","content":"# Report\nnothing counted\n"}""", "w1"),
            Turn.Says("Wrote the report."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "write the report");

        Assert.Equal(RunOutcomeKind.Failed, events.Last().Outcome());
        Assert.Contains(Checks(events), e => e.Summary.StartsWith("FAIL", StringComparison.Ordinal)
                                             && e.Summary.Contains("does not contain \"Total\"", StringComparison.Ordinal));
    }

    /// <summary>A file already there before the work proves nothing about the work (the baseline sees the typed criterion too).</summary>
    [Fact]
    public async Task A_file_that_was_already_there_cannot_prove_the_work()
    {
        using var fx = new EngineFixture { TypedCriteria = true };
        fx.Write("report.md", "# Report\nTotal: 1\n");
        var worker = new FakeChatProvider(Turn.Says(Plan), Turn.Says("Nothing to do."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "write the report");

        Assert.Contains(events, e => e.Summary.Contains("already passes before any work", StringComparison.Ordinal));
    }

    /// <summary>3.4: the template's criteria stay; the planner's are added to them.</summary>
    [Fact]
    public async Task The_planner_adds_to_the_runs_own_criteria_and_takes_none_away()
    {
        using var fx = new EngineFixture { TypedCriteria = true };
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1("write_file", """{"path":"report.md","content":"Total: 3"}""", "w1"),
            Turn.Says("Wrote the report."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"),
            successCriteria: [new("the person's check", "echo checked")]), "write the report");

        var checks = Checks(events);
        Assert.Contains(checks, e => e.Summary.Contains("the person's check", StringComparison.Ordinal));
        Assert.Contains(checks, e => e.Summary.Contains("file_contains report.md", StringComparison.Ordinal));
    }

    /// <summary>Off, the old planner: its criteria are not read and nothing is checked that was not before.</summary>
    [Fact]
    public async Task With_typed_criteria_off_the_run_is_as_it_was()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1("write_file", """{"path":"report.md","content":"no total"}""", "w1"),
            Turn.Says("Wrote the report."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "write the report");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Empty(Checks(events));
        Assert.DoesNotContain(events, e => e.Summary.Contains("Planner criterion", StringComparison.Ordinal));
    }

    /// <summary>The contract review answers with commands; the criteria the engine decides itself are not its to lose.</summary>
    [Fact]
    public async Task The_contract_review_cannot_lose_a_criterion_the_engine_decides()
    {
        var typed = new SuccessCriterionDefinition("report.md exists", "file_exists report.md (not empty)", Origin: CriterionOrigin.Proposed)
            { Typed = new TypedCriterion(TypedCriterionKind.FileExists, Path: "report.md") };
        var answer = """{"sources":[{"id":"O001","assessment":"a report"}],"checks":[],"forbidden_effects":[],"action_policy":null,"unresolved":null}""";

        var result = await PlanCheckReview.RunAsync(new PlanResult(IntentDisposition.QuickAction, "work", null) { Checks = [typed] },
            "Write a report", new WorkContext(null, "workspace", null, null, null, [], []),
            new FakeChatProvider(Turn.Says(answer)), "strong", new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default);

        Assert.Null(result.IncompleteReason);
        Assert.Equal(typed, Assert.Single(result.Checks));
    }

    [Fact]
    public void The_planner_is_told_about_typed_criteria_only_when_they_are_on()
    {
        Assert.DoesNotContain("\"criteria\"", Planner.SystemPromptFor(null), StringComparison.Ordinal);
        Assert.Contains("\"criteria\"", Planner.SystemPromptFor(null, typedCriteria: true), StringComparison.Ordinal);
    }
}
