namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Tasks;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Plan Phase 1.1: a step that depends on a step's list says what it does with it - consumes it ("consumes", and
/// with what evidence) or only waits for that step ("after"). The declaration is read from the planner, kept on the
/// plan, sent back to the planner when the plan is repaired, and carried through a checkpoint and a resume.
/// </summary>
public sealed class AStepSaysWhatItConsumesTests
{
    // ── the evidence words ───────────────────────────────────────────────────────────────

    /// <summary>A kind is stored by name and may still be stored by number somewhere: a new one goes last.</summary>
    [Fact]
    public void A_file_write_is_a_kind_of_evidence_added_after_the_ones_already_stored()
    {
        Assert.Equal(0, (int)EvidenceKind.FileRead);
        Assert.Equal(1, (int)EvidenceKind.Command);
        Assert.Equal(2, (int)EvidenceKind.Call);
        Assert.Equal(3, (int)EvidenceKind.FileWrite);
    }

    [Theory]
    [InlineData("mentioned", EvidenceKind.Call)]
    [InlineData("call", EvidenceKind.Call)]          // the word stored criteria and older plans use
    [InlineData("file_read", EvidenceKind.FileRead)]
    [InlineData("file_write", EvidenceKind.FileWrite)]
    [InlineData("command", EvidenceKind.Command)]
    public void The_planner_names_each_evidence_kind_by_a_word(string word, EvidenceKind kind)
        => Assert.Equal(kind, EvidenceCoverage.KindNamed(word));

    /// <summary>An item named in a successful call is shown as "mentioned": it proves neither reading nor the work.</summary>
    [Fact]
    public void An_item_named_in_a_call_is_shown_as_mentioned_not_as_a_call()
    {
        Assert.Equal("mentioned", EvidenceCoverage.NameOf(EvidenceKind.Call));
        Assert.Equal("file_write", EvidenceCoverage.NameOf(EvidenceKind.FileWrite));
    }

    /// <summary>
    /// A criterion is decided from evidence the engine records, and it records no write evidence yet: a criterion
    /// asking for file_write could never pass, so it is refused as before, not accepted because the word is now known.
    /// </summary>
    [Theory]
    [InlineData("""{"criteria":[{"kind":"covers_all","source":{"step":0,"field":"pages"},"results":{"step":1,"field":"notes"},"evidence":"file_write"}]}""")]
    [InlineData("""{"steps":[{"title":"find"},{"title":"review","criteria":[{"kind":"semantic","text":"The notes say what each page is about","evidence":["file_write"]}]}]}""")]
    public void A_criterion_asking_for_write_evidence_is_still_refused(string stated)
    {
        using var first = JsonDocument.Parse("""{"pages":{"type":"path[]"}}""");
        using var second = JsonDocument.Parse("""{"notes":{"type":"results"}}""");
        var plan = DagPlan.FromSpecs(
        [
            new PlanStepSpec("find", [], DependenciesDeclared: true) { Output = Planner.ParseOutput(first.RootElement, 1) },
            new PlanStepSpec("review", [0], DependenciesDeclared: true) { Output = Planner.ParseOutput(second.RootElement, 2) }
        ]);
        using var doc = JsonDocument.Parse(stated);

        var (accepted, dropped) = TypedCriteria.Validate(TypedCriteria.Read(doc.RootElement), Path.GetTempPath(), [], plan, semantic: true);

        Assert.Empty(accepted);
        Assert.Contains("'file_write' is not an evidence kind", Assert.Single(dropped));
    }

    // ── the planner ──────────────────────────────────────────────────────────────────────

    private const string Declared = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]"},"tags":{"type":"string[]"}}},
                  {"title":"review pages","dependsOn":[0],
                   "consumes":[{"step":0,"field":"pages","evidence":"file_read","subset":true},
                               {"step":0,"field":"tags"},
                               {"step":0,"field":"pages","evidence":"file_write"},
                               {"step":0,"field":"pages","evidence":"call"},
                               {"step":"zero","field":"pages"}, {"field":"pages"}, {"step":0,"field":""}, {"step":0}, 7]},
                  {"title":"tidy up","dependsOn":[],"after":[0,"one",9,-1,0]}]}
        """;

    private static Task<PlanResult> Plan(string answer)
        => new Planner().PlanAsync("review the wiki", Context, new FakeChatProvider(Turn.Says(answer)), "planner", CancellationToken.None);

    private static WorkContext Context => new(Guid.NewGuid(), "workspace", null, null, null, [], []);

    [Fact]
    public async Task What_a_step_consumes_is_read_and_a_malformed_entry_is_dropped()
    {
        var plan = (await Plan(Declared)).Plan!;
        var consumes = plan.Steps[1].Consumes!;

        Assert.Equal(4, consumes.Count);                                         // the five malformed ones are gone
        Assert.Equal(new ConsumeSpec(0, "pages") { Evidence = EvidenceKind.FileRead, Subset = true }, consumes[0]);
        Assert.Equal(new ConsumeSpec(0, "tags") { Evidence = EvidenceKind.Call }, consumes[1]);   // mentioned unless said
        Assert.Equal(EvidenceKind.FileWrite, consumes[2].Evidence);
        Assert.Equal(EvidenceKind.Call, consumes[3].Evidence);
        Assert.All(consumes, c => Assert.Null(c.UnknownEvidence));
        Assert.Null(plan.Steps[0].Consumes);
    }

    [Fact]
    public async Task A_step_that_only_waits_is_ordered_after_its_steps_and_remembers_the_edge_is_only_order()
    {
        var plan = (await Plan(Declared)).Plan!;
        var tidy = plan.Steps[2];

        Assert.Equal([plan.Steps[0].Id], tidy.DependsOn);                        // "one", 9 and -1 dropped; 0 once
        Assert.Equal([plan.Steps[0].Id], tidy.OrderOnly);
        Assert.Null(plan.Steps[1].OrderOnly);
    }

    /// <summary>A word the engine does not know is not quietly taken as "mentioned": it is kept, and is a defect of the plan.</summary>
    [Fact]
    public async Task An_unknown_evidence_word_is_kept_and_surfaces_as_a_defect()
    {
        var plan = (await Plan(Declared.Replace("\"evidence\":\"file_write\"", "\"evidence\":\"vibes\"", StringComparison.Ordinal))).Plan!;

        Assert.Equal("vibes", plan.Steps[1].Consumes![2].UnknownEvidence);
        Assert.Contains(PlanConsumes.Classify(plan).Defects, d => d.Contains("'vibes'", StringComparison.Ordinal));
    }

    /// <summary>A plan sent back to be repaired says what it said: the planner is not made to declare it all again.</summary>
    [Fact]
    public async Task A_plan_sent_back_for_repair_carries_its_consumes_and_after()
    {
        var first = await Plan(Declared.Replace("\"evidence\":\"file_write\"", "\"evidence\":\"vibes\"", StringComparison.Ordinal));
        var repairer = new FakeChatProvider(Turn.Says("not a plan"));

        await new Planner().ReplanAsync("review the wiki", Context, first, "it was wrong", repairer, "planner", CancellationToken.None,
            maxSteps: null, proposeChecks: false, turnCeiling: 0, stepOutputs: true);

        var sent = repairer.Requests[0].Messages.Single(m => m.Role == Enactive.Core.Chat.ChatRole.Assistant).Content!;
        using (var doc = JsonDocument.Parse(sent))
        {
            var steps = doc.RootElement.GetProperty("steps");
            var consumes = steps[1].GetProperty("consumes");
            Assert.Equal("""{"step":0,"field":"pages","evidence":"file_read","subset":true}""", consumes[0].GetRawText());
            Assert.Equal("""{"step":0,"field":"tags","evidence":"mentioned"}""", consumes[1].GetRawText());
            Assert.Equal("vibes", consumes[2].GetProperty("evidence").GetString());   // as written, so the defect can be named
            Assert.Equal("[0]", steps[2].GetProperty("after").GetRawText());
            Assert.Equal("[]", steps[2].GetProperty("dependsOn").GetRawText());       // the order-only edge is said once, as after
        }

        // And read back, it is the same plan.
        var again = (await Plan(sent)).Plan!;
        Assert.Equal(first.Plan!.Steps[1].Consumes, again.Steps[1].Consumes);
        Assert.Equal([again.Steps[0].Id], again.Steps[2].OrderOnly);
        Assert.Equal([again.Steps[0].Id], again.Steps[2].DependsOn);
    }

    /// <summary>Told only when it can be used: a consume names a declared output, and outputs are declared only with step outputs on.</summary>
    [Fact]
    public void The_planner_is_told_about_consumes_and_after_only_with_step_outputs()
    {
        var told = Planner.SystemPromptFor(null, stepOutputs: true);
        Assert.Contains("\"consumes\"", told, StringComparison.Ordinal);
        Assert.Contains("\"after\"", told, StringComparison.Ordinal);
        Assert.Contains("a list you consume is a list you must cover", told, StringComparison.Ordinal);
        Assert.DoesNotContain("\"consumes\"", Planner.SystemPromptFor(null), StringComparison.Ordinal);
    }

    // ── the plan ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void After_becomes_a_dependency_the_scheduler_waits_on_and_is_remembered_as_order_only()
    {
        var plan = DagPlan.FromSpecs(
        [
            new PlanStepSpec("a", []),
            new PlanStepSpec("b", []),
            new PlanStepSpec("c", [0]) { After = [1, 1, 5, -2], Consumes = [new ConsumeSpec(0, "pages")] }
        ]);
        var c = plan.Steps[2];

        Assert.Equal([plan.Steps[0].Id, plan.Steps[1].Id], c.DependsOn);
        Assert.Equal([plan.Steps[1].Id], c.OrderOnly);
        Assert.Equal([new ConsumeSpec(0, "pages")], c.Consumes);
        Assert.Empty(plan.Steps[1].DependsOn);                                  // "after" is a statement about dependencies: no chain is made up
    }

    // ── the checkpoint ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Consumes_order_only_edges_and_the_plan_contract_survive_the_checkpoint_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonCheckpointStore(WorkspaceInfo.For(root));
            var first = Guid.NewGuid();
            var consumes = new[] { new ConsumeSpec(0, "pages") { Evidence = EvidenceKind.FileWrite, Subset = true },
                                   new ConsumeSpec(0, "tags") { UnknownEvidence = "vibes" } };
            var written = new RunCheckpoint(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, "r", "t", null, null,
                [new CheckpointStep(first, "a", [], "Normal", "Done"),
                 new CheckpointStep(Guid.NewGuid(), "b", [first], "Normal", "Pending") { Consumes = consumes, OrderOnly = [first] }],
                [], [], [], 1, 0) { PlanContractVersion = PlanContractVersions.Current };

            await store.SaveAsync(written, default);
            var back = (await store.LoadAsync(written.RunId, default))!;

            Assert.Equal(PlanContractVersions.Current, back.PlanContractVersion);
            Assert.Equal(consumes, back.Steps[1].Consumes);
            Assert.Equal([first], back.Steps[1].OrderOnly);
            Assert.Null(back.Steps[0].Consumes);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* a temp folder */ } }
    }

    /// <summary>A checkpoint written before the contract has no version: it is not made to look as if it had one.</summary>
    [Fact]
    public void A_checkpoint_from_before_the_contract_has_no_version()
    {
        var legacy = """
            {"RunId":"00000000-0000-0000-0000-000000000001","TaskId":"00000000-0000-0000-0000-000000000002",
             "StartedAt":"2026-10-01T00:00:00+00:00","At":"2026-10-01T00:00:00+00:00","Request":"r","Title":"t",
             "Steps":[{"Id":"00000000-0000-0000-0000-000000000003","Title":"a","DependsOn":[],"Complexity":"Normal","Status":"Pending"}],
             "Digest":[],"Transcript":[],"Artifacts":[],"StepsRun":0,"TokensSpent":0}
            """;
        var back = JsonSerializer.Deserialize<RunCheckpoint>(legacy)!;

        Assert.Null(back.PlanContractVersion);
        Assert.Null(back.Steps[0].Consumes);
        Assert.Null(back.Steps[0].OrderOnly);
    }

    // ── through a run ────────────────────────────────────────────────────────────────────

    private const string FindReviewTidy = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review pages","dependsOn":[0],"consumes":[{"step":0,"field":"pages","evidence":"file_read"}]},
                  {"title":"tidy up","dependsOn":[],"after":[1]}]}
        """;

    private static Turn[] Find => [
        Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md"]}""", "s0"), Turn.Says("Found one.")];

    /// <summary>
    /// The run writes what each step consumes and waits for into its checkpoints, with the plan contract it was planned
    /// under; a resume reads them back into the plan (its own checkpoints still carry them), and a checkpoint from
    /// before the contract stays one - resuming it does not stamp it with a contract it was not planned under.
    /// </summary>
    [Fact]
    public async Task A_run_writes_what_its_steps_consume_and_a_resume_reads_it_back()
    {
        using var fx = new EngineFixture { StepOutputs = true };
        fx.Write("wiki/a.md", "# A\n");
        var store = new Recording();
        await fx.RunAsync(fx.Build(new FakeChatProvider([Turn.Says(FindReviewTidy), .. Find, Turn.Says("Reviewed."), Turn.Says("Tidied.")]),
            checkpoints: store), "review the wiki");

        var afterFind = store.Saved.First(c => c.Steps[0].Status == "Done" && c.Steps[1].Status != "Done");
        Assert.Equal(PlanContractVersions.Current, afterFind.PlanContractVersion);
        Assert.Equal([new ConsumeSpec(0, "pages") { Evidence = EvidenceKind.FileRead }], afterFind.Steps[1].Consumes);
        Assert.Equal([afterFind.Steps[1].Id], afterFind.Steps[2].OrderOnly);
        Assert.Equal([afterFind.Steps[1].Id], afterFind.Steps[2].DependsOn);

        foreach (var version in new int?[] { PlanContractVersions.Current, null })
        {
            var from = JsonSerializer.Deserialize<RunCheckpoint>(JsonSerializer.Serialize(afterFind with { PlanContractVersion = version }))!;
            var before = store.Saved.Count;
            await fx.ResumeAsync(fx.Build(new FakeChatProvider([Turn.Says("Reviewed."), Turn.Says("Tidied.")]), checkpoints: store), from);

            var resumed = store.Saved.Skip(before).ToArray();
            Assert.NotEmpty(resumed);
            Assert.All(resumed, c =>
            {
                Assert.Equal(version, c.PlanContractVersion);
                Assert.Equal(from.Steps[1].Consumes, c.Steps[1].Consumes);
                Assert.Equal([from.Steps[1].Id], c.Steps[2].OrderOnly);
            });
        }
    }

    private sealed class Recording : IRunCheckpointStore
    {
        public List<RunCheckpoint> Saved { get; } = [];
        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct) { lock (Saved) Saved.Add(checkpoint); return Task.CompletedTask; }
        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved.ToArray());
        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) => Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId));
        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
    }
}
