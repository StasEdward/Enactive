namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Enactive.Core.Permissions;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Plan Phase 1.1-1.2, through a run: with step outputs and typed criteria on, a dependency on a step that hands on a
/// list must say what it does with the list. Unsaid, the plan goes back to the planner once with the diagnosis; what
/// the planner still leaves unsaid is a run obligation that forbids Completed - asked about once, before any work,
/// when somebody can answer. What a step consumes becomes a coverage criterion the run is held to at its end.
/// </summary>
public sealed class APlanThatDoesNotCoverAListTests
{
    // Step 2 depends on the pages and says neither "consumes" nor "after", and no covers_all is written for it.
    private const string Unclassified = """
        {"disposition":"task","title":"summarise the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"summarise pages","dependsOn":[0]}]}
        """;

    // The same plan, corrected: step 2 consumes the pages and has a results output to account for each one in.
    private const string Consuming = """
        {"disposition":"task","title":"summarise the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"summarise pages","dependsOn":[0],"consumes":[{"step":0,"field":"pages"}],
                   "output":{"notes":{"type":"results","description":"a note per page"}}}]}
        """;

    private const string Cyclic = """
        {"disposition":"task","title":"summarise the wiki",
         "steps":[{"title":"find pages","dependsOn":[1],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"summarise pages","dependsOn":[0]}]}
        """;

    private const string Diagnosis = "without saying whether it consumes them";

    private static EngineFixture Wiki(int pages = 2, bool stepOutputs = true, bool typedCriteria = true)
    {
        var fx = new EngineFixture { StepOutputs = stepOutputs, TypedCriteria = typedCriteria };
        for (var i = 1; i <= pages; i++)
            fx.Write($"wiki/p{i:00}.md", $"# Page {i}\n");
        return fx;
    }

    private static string PagesJson(int n) => JsonSerializer.Serialize(new { pages = Enumerable.Range(1, n).Select(i => $"wiki/p{i:00}.md") });

    private static Turn[] Find(int n = 2) => [Turn.Calls1(StepOutputContract.ToolName, PagesJson(n), "s0"), Turn.Says("Found them.")];

    private static bool Asked(ChatRequest request, string text)
        => request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content?.Contains(text, StringComparison.Ordinal) == true;

    private static string Reason(List<WorkEvent> events) => events.Last().Summary;

    private static IEnumerable<DecisionRequest> CoverageQuestions(ScriptedDecisionHandler decisions)
        => decisions.Requests.Where(r => r.Topic == "The plan does not cover a list");

    // ── the replan leaves it unsaid ─────────────────────────────────────────────────────

    /// <summary>
    /// THE ONE THAT MATTERS. The plan goes back once with the diagnosis; the planner returns it unchanged; the person
    /// is asked once and goes on; the steps run - and the run ends Incomplete, naming the list, never Completed.
    /// </summary>
    [Fact]
    public async Task An_unclassified_list_dependency_is_replanned_once_and_left_unsaid_keeps_the_run_from_completed()
    {
        using var fx = Wiki();
        fx.Decisions.Answer = "go_on";
        var worker = new FakeChatProvider([Turn.Says(Unclassified), Turn.Says(Unclassified), .. Find(), Turn.Says("Summarised.")]);

        var events = await fx.RunAsync(fx.Build(worker), "summarise the wiki");

        // Asked once, with the diagnosis and what a consume costs.
        var replan = Assert.Single(worker.Requests, r => Asked(r, Diagnosis));
        var said = replan.Messages.Last().Content!;
        Assert.Contains("every item", said, StringComparison.Ordinal);
        Assert.Contains("\"after\"", said, StringComparison.Ordinal);
        // The question, once, before any step.
        var question = Assert.Single(CoverageQuestions(fx.Decisions));
        Assert.Equal(["go_on", "stop"], question.Options.Select(o => o.Id));
        Assert.Equal("go_on", question.RecommendedOptionId);
        Assert.Contains("Go on - the run cannot end as completed", question.Options[0].Label, StringComparison.Ordinal);
        Assert.Contains("does not waive", question.FullDetail, StringComparison.Ordinal);
        Assert.False(question.MayBeRemembered);
        Assert.Null(question.Subject);
        var asked = events.FindIndex(e => e.Kind == EventKind.DecisionRequested && e.Summary.StartsWith("The plan does not cover a list", StringComparison.Ordinal));
        Assert.True(asked >= 0 && asked < events.FindIndex(e => e.Kind == EventKind.StepStarted));
        // The steps ran, and the run is not Completed, and says why.
        Assert.Equal(2, events.Count(e => e.Kind == EventKind.StepCompleted));
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Contains("the plan does not cover a list: step 2 depends on step 1", Reason(events), StringComparison.Ordinal);
    }

    /// <summary>Nobody to ask - unattended, scheduled, --approve deny: the run goes on, and its Inbox line names the list.</summary>
    [Fact]
    public async Task Unattended_the_run_goes_on_without_a_question_and_its_inbox_line_names_the_list()
    {
        using var fx = Wiki();
        var unattended = new UnattendedDecisionHandler();
        var worker = new FakeChatProvider([Turn.Says(Unclassified), Turn.Says(Unclassified), .. Find(), Turn.Says("Summarised.")]);

        var events = await fx.RunAsync(fx.Build(worker, decisions: unattended), "summarise the wiki");

        Assert.Empty(unattended.Refusals);
        Assert.Equal(2, events.Count(e => e.Kind == EventKind.StepCompleted));
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        var reason = JsonDocument.Parse(events.Last().PayloadJson!).RootElement.GetProperty("reason").GetString();
        Assert.Contains("the plan does not cover a list", InboxLines.For("Incomplete", 0, 0, reason).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopped_at_the_question_the_run_is_cancelled_and_no_step_starts()
    {
        using var fx = Wiki();
        fx.Decisions.Answer = "stop";
        var worker = new FakeChatProvider([Turn.Says(Unclassified), Turn.Says(Unclassified), .. Find(), Turn.Says("Summarised.")]);

        var events = await fx.RunAsync(fx.Build(worker), "summarise the wiki");

        Assert.Single(CoverageQuestions(fx.Decisions));
        Assert.Equal(RunOutcomeKind.Cancelled, events.Last().Outcome());
        Assert.Contains("Stopped at the question about list coverage; no work started.", Reason(events), StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.StepStarted);
        Assert.Equal(2, worker.Requests.Count);
    }

    /// <summary>A corrected plan that cannot run is not taken: the plan from before it can, and runs with its gap.</summary>
    [Fact]
    public async Task A_replan_that_returns_a_cycle_keeps_the_plan_it_was_asked_to_correct()
    {
        using var fx = Wiki();
        fx.Decisions.Answer = "go_on";
        var worker = new FakeChatProvider([Turn.Says(Unclassified), Turn.Says(Cyclic), .. Find(), Turn.Says("Summarised.")]);

        var events = await fx.RunAsync(fx.Build(worker), "summarise the wiki");

        Assert.Single(worker.Requests, r => Asked(r, Diagnosis));
        Assert.Equal(2, events.Count(e => e.Kind == EventKind.StepCompleted));
        Assert.DoesNotContain(events, e => e.Summary.Contains("No steps were started", StringComparison.Ordinal));
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Contains("the plan does not cover a list", Reason(events), StringComparison.Ordinal);
    }

    /// <summary>Regression guard: a structural defect still stops the run as it did, and is not taken for a coverage gap.</summary>
    [Fact]
    public async Task A_plan_with_a_cycle_still_stops_before_any_step()
    {
        using var fx = Wiki();
        var worker = new FakeChatProvider([Turn.Says(Cyclic), Turn.Says(Cyclic), .. Find(), Turn.Says("Summarised.")]);

        var events = await fx.RunAsync(fx.Build(worker), "summarise the wiki");

        Assert.Equal(2, worker.Requests.Count);
        Assert.DoesNotContain(worker.Requests, r => Asked(r, Diagnosis));
        Assert.Empty(CoverageQuestions(fx.Decisions));
        Assert.DoesNotContain(events, e => e.Kind == EventKind.StepStarted);
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Contains(events, e => e.Summary.Contains("No steps were started", StringComparison.Ordinal));
    }

    // Step 1 already waits for step 2, and step 2 consumes step 1's pages: the dependency the consume needs closes a cycle.
    private const string ClosingACycle = """
        {"disposition":"task","title":"summarise the wiki",
         "steps":[{"title":"find pages","dependsOn":[1],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"summarise pages","dependsOn":[],"consumes":[{"step":0,"field":"pages"}]}]}
        """;

    /// <summary>
    /// A coverage gap never stops a run. The dependency a consume needs is not added when it would make the plan unable
    /// to run: the plan runs as it was, and the consume it cannot honour is an obligation.
    /// </summary>
    [Fact]
    public async Task A_consume_whose_dependency_would_close_a_cycle_is_an_obligation_not_a_stop()
    {
        using var fx = Wiki();
        fx.Decisions.Answer = "go_on";
        var worker = new FakeChatProvider([Turn.Says(ClosingACycle), Turn.Says(ClosingACycle), Turn.Says("Summarised."), .. Find()]);

        var events = await fx.RunAsync(fx.Build(worker), "summarise the wiki");

        Assert.Single(worker.Requests, r => Asked(r, "unable to run"));
        Assert.DoesNotContain(events, e => e.Summary.Contains("No steps were started", StringComparison.Ordinal));
        Assert.Equal(2, events.Count(e => e.Kind == EventKind.StepCompleted));
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Contains("would make it unable to run", Reason(events), StringComparison.Ordinal);
    }

    /// <summary>A coverage criterion the run was given says what the step does with the list, as a planner's would.</summary>
    [Fact]
    public async Task A_dependency_a_given_coverage_criterion_covers_is_not_sent_back()
    {
        using var fx = Wiki();
        const string reviewing = """
            {"disposition":"task","title":"summarise the wiki",
             "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                      {"title":"summarise pages","dependsOn":[0],"output":{"notes":{"type":"results","description":"a note per page"}}}]}
            """;
        var given = new Enactive.Core.Templates.SuccessCriterionDefinition("Every page is summarised", "covers_all")
        {
            Typed = new Enactive.Core.Templates.TypedCriterion(Enactive.Core.Templates.TypedCriterionKind.EvidenceCoversAll,
                SourceStep: 0, SourceField: "pages", ResultsStep: 1, ResultsField: "notes", Evidence: EvidenceKind.Call)
        };
        var worker = new FakeChatProvider([Turn.Says(reviewing), .. Find(), .. Summarise(read: 2, of: 2)]);

        var events = await fx.RunAsync(fx.Build(worker, successCriteria: [given]), "summarise the wiki");

        Assert.DoesNotContain(worker.Requests, r => Asked(r, "account for the lists"));
        Assert.Empty(CoverageQuestions(fx.Decisions));
        // Already checked by the criterion the run was given: not derived again beside it.
        Assert.DoesNotContain(events, e => e.Summary.Contains("is covered by step", StringComparison.Ordinal));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    // ── the replan says it ──────────────────────────────────────────────────────────────

    private static Turn[] Summarise(int read, int of)
        => [.. Enumerable.Range(1, read).Select(i => Turn.Calls1("read_file", $$"""{"path":"wiki/p{{i:00}}.md"}""", $"r{i}")),
            Turn.Calls1(StepOutputContract.ToolName,
                JsonSerializer.Serialize(new { notes = Enumerable.Range(1, of).ToDictionary(i => $"wiki/p{i:00}.md", i => $"page {i}") }), "s1"),
            Turn.Says("Summarised.")];

    [Fact]
    public async Task A_replan_that_says_what_the_step_consumes_is_held_to_covering_it_and_completes_when_it_does()
    {
        using var fx = Wiki();
        var worker = new FakeChatProvider([Turn.Says(Unclassified), Turn.Says(Consuming), .. Find(), .. Summarise(read: 2, of: 2)]);

        var events = await fx.RunAsync(fx.Build(worker), "summarise the wiki");

        Assert.Single(worker.Requests, r => Asked(r, Diagnosis));
        Assert.Empty(CoverageQuestions(fx.Decisions));
        Assert.Contains(events, e => e.Kind == EventKind.ContextAssembled
                                     && e.Summary.Contains("Every pages item of step 1 is covered by step 2 (mentioned)", StringComparison.Ordinal));
        Assert.Contains(events, e => e.IsCheck() && e.Summary.StartsWith("PASS", StringComparison.Ordinal)
                                     && e.Summary.Contains("Every pages item of step 1 is covered by step 2", StringComparison.Ordinal));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    /// <summary>The criterion derived from the consume decides the run: two of twelve pages is not a finished run.</summary>
    [Fact]
    public async Task A_step_that_covers_two_of_twelve_items_it_consumes_does_not_complete_the_run()
    {
        using var fx = Wiki(pages: 12);
        var worker = new FakeChatProvider([Turn.Says(Unclassified), Turn.Says(Consuming), .. Find(12), .. Summarise(read: 2, of: 12)]);

        var events = await fx.RunAsync(fx.Build(worker), "summarise the wiki");

        Assert.Contains(events, e => e.IsCheck() && e.Summary.StartsWith("FAIL", StringComparison.Ordinal)
                                     && e.Summary.Contains("covered 2 of 12", StringComparison.Ordinal));
        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    // ── the switches ────────────────────────────────────────────────────────────────────

    /// <summary>Regression guard: with either switch off nothing is classified, replanned, asked or derived.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task With_step_outputs_or_typed_criteria_off_nothing_changes(bool stepOutputs, bool typedCriteria)
    {
        using var fx = Wiki(stepOutputs: stepOutputs, typedCriteria: typedCriteria);
        Turn[] find = stepOutputs ? Find() : [Turn.Says("Found them.")];
        var worker = new FakeChatProvider([Turn.Says(Unclassified), .. find, Turn.Says("Summarised.")]);

        var events = await fx.RunAsync(fx.Build(worker), "summarise the wiki");

        Assert.DoesNotContain(worker.Requests, r => Asked(r, Diagnosis));
        Assert.Empty(CoverageQuestions(fx.Decisions));
        Assert.DoesNotContain(events, e => e.Summary.Contains("is covered by step", StringComparison.Ordinal));
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
    }

    // ── a checkpoint, and a resume ──────────────────────────────────────────────────────

    private sealed class Recording : IRunCheckpointStore
    {
        public List<RunCheckpoint> Saved { get; } = [];
        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct) { lock (Saved) Saved.Add(checkpoint); return Task.CompletedTask; }
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved.ToArray());
        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) => Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId));
        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// The obligation is kept in the checkpoint, survives being written and read, and a resume is held to it without
    /// asking again - the answer was given when the run began, and resume does not plan.
    /// </summary>
    [Fact]
    public async Task The_obligation_survives_a_checkpoint_and_a_resume_does_not_ask_again()
    {
        using var fx = Wiki();
        fx.Decisions.Answer = "go_on";
        var store = new Recording();
        await fx.RunAsync(fx.Build(new FakeChatProvider([Turn.Says(Unclassified), Turn.Says(Unclassified), .. Find(), Turn.Says("Summarised.")]),
            checkpoints: store), "summarise the wiki");

        var afterFind = store.Saved.First(c => c.Steps[0].Status == "Done" && c.Steps[1].Status != "Done");
        var obligation = Assert.Single(afterFind.CoverageObligations!);
        Assert.Contains("step 2 depends on step 1", obligation, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize<RunCheckpoint>(JsonSerializer.Serialize(afterFind))!;
        Assert.Equal(afterFind.CoverageObligations, back.CoverageObligations);

        fx.Decisions.Requests.Clear();
        var events = await fx.ResumeAsync(fx.Build(new FakeChatProvider([Turn.Says("Summarised.")]), checkpoints: store), back);

        Assert.Empty(CoverageQuestions(fx.Decisions));
        Assert.Equal(RunOutcomeKind.Incomplete, events.Last().Outcome());
        Assert.Contains("the plan does not cover a list: " + obligation, Reason(events), StringComparison.Ordinal);
    }

    /// <summary>
    /// A checkpoint from before the contract never declared what its steps consume: the resumed run says list coverage
    /// is not guaranteed - and invents no obligation for it.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_planned_before_the_contract_says_coverage_is_not_guaranteed()
    {
        using var fx = Wiki();
        var store = new Recording();
        await fx.RunAsync(fx.Build(new FakeChatProvider([Turn.Says(Consuming), .. Find(), .. Summarise(read: 2, of: 2)]),
            checkpoints: store), "summarise the wiki");
        var afterFind = store.Saved.First(c => c.Steps[0].Status == "Done" && c.Steps[1].Status != "Done");
        Assert.Null(afterFind.CoverageObligations);

        foreach (var (version, said) in new (int?, bool)[] { (PlanContractVersions.Current, false), (null, true) })
        {
            var events = await fx.ResumeAsync(fx.Build(new FakeChatProvider(Summarise(read: 2, of: 2)), checkpoints: store),
                afterFind with { PlanContractVersion = version });

            Assert.Equal(said, events.Any(e => e.Kind == EventKind.ContextAssembled
                && e.Summary.Contains("planned before the coverage contract", StringComparison.Ordinal)
                && e.Summary.Contains("not guaranteed", StringComparison.Ordinal)));
            Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        }
    }
}
