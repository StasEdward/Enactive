namespace Enactive.Engine.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Phase 2: a step hands its result on as values, through <c>submit_step_output</c>, and the steps
/// after it receive the values - not a retelling of them. Held to amendment C's seven invariants,
/// each of which is a submission protocol that failed in a real run.
/// </summary>
public sealed class StepOutputTests
{
    private static readonly StepOutputSchema Pages = new("step1", 1,
    [
        new StepOutputField("pages", StepOutputFieldType.PathList, "the pages to process", MaxItems: 3),
        new StepOutputField("summary", StepOutputFieldType.Text, "what was found", MaxLength: 20),
        new StepOutputField("count", StepOutputFieldType.Integer, "how many", Required: false)
    ]);

    private static StepOutputContract.Verdict Check(string arguments, StepOutputSchema? schema = null)
        => StepOutputContract.Check(schema ?? Pages, arguments, path => path.StartsWith("pages/", StringComparison.Ordinal),
            id => id is >= 1 and <= 5);

    // ── amendment C, one invariant at a time ─────────────────────────────────────────────

    /// <summary>C.1: the tool's required list is the schema's, made from the one object the engine checks against.</summary>
    [Fact]
    public void The_tool_the_model_sees_is_made_from_the_contract_it_is_checked_against()
    {
        var tool = StepOutputContract.Tool(Pages);
        var schema = JsonNode.Parse(tool.JsonSchema)!;

        Assert.Equal(["pages", "summary"], schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray());
        Assert.Equal(StepOutputContract.ToolName, tool.Name);
    }

    /// <summary>C.2: a refusal names what is missing and says to keep the rest - it does not list everything required.</summary>
    [Fact]
    public void A_refusal_names_what_is_missing_and_says_to_keep_the_rest()
    {
        var verdict = Check("""{"pages":["pages/a.md"]}""");

        var error = Assert.Single(verdict.Errors);
        Assert.Contains("Missing: summary", error, StringComparison.Ordinal);
        Assert.Contains("keeping what you already sent", error, StringComparison.Ordinal);
        Assert.DoesNotContain("pages", error, StringComparison.Ordinal);
    }

    /// <summary>C.3: evidence is not a second thing to repeat; a result is whole without it.</summary>
    [Fact]
    public void Evidence_is_not_required()
        => Assert.True(Check("""{"pages":["pages/a.md"],"summary":"one page"}""").Accepted);

    /// <summary>C.4: prose over its limit is cut and marked, not refused; an empty field is still refused.</summary>
    [Fact]
    public void Prose_over_its_limit_is_cut_not_refused_and_empty_prose_is_refused()
    {
        var cut = Check("""{"pages":["pages/a.md"],"summary":"a summary that is far longer than twenty characters"}""");
        Assert.True(cut.Accepted);
        Assert.Contains("cut by the engine", cut.Values!["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("summary was cut to 20", Assert.Single(cut.Notes), StringComparison.Ordinal);

        Assert.Contains(Check("""{"pages":["pages/a.md"],"summary":"  "}""").Errors,
            e => e.Contains("summary is empty", StringComparison.Ordinal));
    }

    /// <summary>C.6: every limit the engine applies is in the words of the tool, because grammar back-ends strip it from the schema.</summary>
    [Fact]
    public void Every_limit_is_in_the_tools_description()
    {
        var description = StepOutputContract.Tool(Pages).Description;
        Assert.Contains("at most 3 items", description, StringComparison.Ordinal);
        Assert.Contains("up to 20 characters - longer is cut", description, StringComparison.Ordinal);
        Assert.Contains("must exist", description, StringComparison.Ordinal);
    }

    /// <summary>C.7: limits are the work's. Where the work set none, the engine sets none.</summary>
    [Fact]
    public void Where_the_work_set_no_limit_the_engine_sets_none()
    {
        var unbounded = new StepOutputSchema("s", 1, [new StepOutputField("names", StepOutputFieldType.StringList, "names")]);
        var many = new JsonObject { ["names"] = new JsonArray(Enumerable.Range(0, 500).Select(i => (JsonNode)$"n{i}").ToArray()) };
        Assert.True(Check(many.ToJsonString(), unbounded).Accepted);

        Assert.Contains(Check("""{"pages":["pages/a","pages/b","pages/c","pages/d"],"summary":"x"}""").Errors,
            e => e.Contains("4 items, and this step's output takes at most 3", StringComparison.Ordinal));
    }

    [Fact]
    public void A_path_that_is_not_in_the_workspace_is_named()
        => Assert.Contains(Check("""{"pages":["pages/a.md","nowhere.md"],"summary":"x"}""").Errors,
            e => e.Contains("not in the workspace: nowhere.md", StringComparison.Ordinal));

    [Fact]
    public void A_field_the_output_does_not_have_is_named_with_the_fields_it_does()
        => Assert.Contains(Check("""{"pages":["pages/a.md"],"summary":"x","extra":1}""").Errors,
            e => e.Contains("'extra' is not a field", StringComparison.Ordinal) && e.Contains("pages, summary, count", StringComparison.Ordinal));

    // ── through a run ────────────────────────────────────────────────────────────────────

    private const string TwoSteps = """
        {"disposition":"task","title":"find then list",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages to process","maxItems":5}}},
                  {"title":"list them","dependsOn":[0]}]}
        """;

    private static EngineFixture Wiki(bool on = true)
    {
        var fx = new EngineFixture { StepOutputs = on };
        fx.Write("pages/a.md", "a");
        fx.Write("pages/b.md", "b");
        return fx;
    }

    private static string Conversation(FakeChatProvider provider, string containing)
        => string.Join("\n", provider.Requests.Select(r => string.Join("\n", r.Messages.Select(m => m.Content ?? "")))
            .Last(text => text.Contains(containing, StringComparison.Ordinal)));

    /// <summary>
    /// THE ONE THAT MATTERS: refused for a page that is not there, corrected, accepted - and the next
    /// step receives the pages as values. C.5 on the way: the same refused submission sent twice is
    /// said to be the same.
    /// </summary>
    [Fact]
    public async Task A_step_hands_its_result_on_as_values_and_the_next_step_receives_them()
    {
        using var fx = Wiki();
        const string wrong = """{"pages":["pages/a.md","pages/missing.md"]}""";
        var worker = new FakeChatProvider(
            Turn.Says(TwoSteps),
            Turn.Calls1(StepOutputContract.ToolName, wrong, "s1"),
            Turn.Calls1(StepOutputContract.ToolName, wrong, "s2"),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["pages/a.md","pages/b.md"]}""", "s3"),
            Turn.Says("Found two pages."),
            Turn.Says("Listed them."));

        var events = await fx.RunAsync(fx.Build(worker), "list the pages");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains("same submission as the last one", Conversation(worker, "pages/b.md\"]"), StringComparison.Ordinal);
        var recorded = Assert.Single(events, e => e.Kind == EventKind.StepOutputRecorded);
        Assert.Contains("pages/b.md", recorded.PayloadJson!, StringComparison.Ordinal);

        var second = Conversation(worker, "Proceed with this step of the plan: list them");
        Assert.Contains("Results handed on by the steps this one depends on", second, StringComparison.Ordinal);
        Assert.Contains("""{"pages":["pages/a.md","pages/b.md"]}""", second, StringComparison.Ordinal);
    }

    /// <summary>A step that was to hand on a result and did not: told once, then not called finished.</summary>
    [Fact]
    public async Task A_step_that_hands_nothing_on_is_told_once_and_then_not_finished()
    {
        using var fx = Wiki();
        var worker = new FakeChatProvider(Turn.Says(TwoSteps), Turn.Says("Found them."), Turn.Says("Found them, really."));

        var events = await fx.RunAsync(fx.Build(worker), "list the pages");

        // The plan, the step's first closing message, and the turn after the reminder.
        Assert.Contains("is not finished until it hands its result on",
            string.Join("\n", worker.Requests[2].Messages.Select(m => m.Content ?? "")), StringComparison.Ordinal);
        Assert.Contains(events, e => e.Kind == EventKind.StepCompleted
                                     && e.Summary.Contains("without handing on its declared output", StringComparison.Ordinal));
    }

    /// <summary>Off, a declared output is dropped: no tool, no requirement, the step finishes by prose as before.</summary>
    [Fact]
    public async Task With_step_outputs_off_a_declared_output_changes_nothing()
    {
        using var fx = Wiki(on: false);
        var worker = new FakeChatProvider(Turn.Says(TwoSteps), Turn.Says("Found them."), Turn.Says("Listed them."));

        var events = await fx.RunAsync(fx.Build(worker), "list the pages");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.DoesNotContain(worker.Requests.Skip(1).SelectMany(r => r.Tools ?? []), t => t.Name == StepOutputContract.ToolName);
    }

    /// <summary>A checkpoint carries the contract and what was handed on, through JSON: the resumed dependent still receives the values.</summary>
    [Fact]
    public async Task A_resumed_dependent_still_receives_what_was_handed_on()
    {
        using var fx = Wiki();
        var store = new Recording();
        await fx.RunAsync(fx.Build(new FakeChatProvider(
            Turn.Says(TwoSteps),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["pages/b.md"]}""", "s1"), Turn.Says("Found one."),
            Turn.Says("Listed it.")), checkpoints: store), "list the pages");

        var afterFirst = JsonSerializer.Deserialize<RunCheckpoint>(JsonSerializer.Serialize(store.Saved.First(c => c.Finished == 1)))!;
        Assert.NotNull(afterFirst.Steps[0].Output);
        Assert.NotNull(afterFirst.Steps[0].Result);

        var resumed = new FakeChatProvider(Turn.Says("Listed it."));
        await fx.ResumeAsync(fx.Build(resumed, checkpoints: store), afterFirst);

        Assert.Contains("""{"pages":["pages/b.md"]}""", string.Join("\n", resumed.Requests[0].Messages.Select(m => m.Content ?? "")),
            StringComparison.Ordinal);
    }

    private sealed class Recording : IRunCheckpointStore
    {
        public List<RunCheckpoint> Saved { get; } = [];
        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct) { lock (Saved) Saved.Add(checkpoint); return Task.CompletedTask; }
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved.ToArray());
        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) => Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId));
        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
    }

    // ── the planner ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_planner_reads_a_declared_output_and_leaves_out_a_type_it_cannot_check()
    {
        using var doc = JsonDocument.Parse("""
            {"pages":{"type":"path[]","description":"pages","maxItems":12},
             "mood":{"type":"feeling"},
             "note":{"type":"text","required":false,"maxLength":400}}
            """);
        var schema = Planner.ParseOutput(doc.RootElement, 2)!;

        Assert.Equal("step2", schema.Id);
        Assert.Equal(["pages", "note"], schema.Fields.Select(f => f.Name).ToArray());
        Assert.Equal(12, schema.Fields[0].MaxItems);
        Assert.False(schema.Fields[1].Required);
    }

    /// <summary>Amendment E: the paragraph is sent only when step outputs are on.</summary>
    [Fact]
    public void The_planner_is_told_about_outputs_only_when_they_are_on()
    {
        Assert.DoesNotContain("\"output\"", Planner.SystemPromptFor(null), StringComparison.Ordinal);
        Assert.Contains("\"output\"", Planner.SystemPromptFor(null, stepOutputs: true), StringComparison.Ordinal);
    }
}
