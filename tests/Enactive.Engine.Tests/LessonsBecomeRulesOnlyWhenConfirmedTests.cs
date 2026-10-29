namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Core.Rules;
using Enactive.Core.Workers;
using Enactive.Settings;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Plan Phase 5: what the engine saw go wrong in a run is put to a person as a candidate, and only what the person keeps
/// reaches a later run's prompts - as the rules the run was STARTED with, kept with its settings, so a resume is given
/// the same text whatever was edited since. The automatic history that reached the prompt until 2026-09-28 is what this
/// is not: run 9c1a061b was steered by fifteen earlier outcomes nobody had agreed to.
/// </summary>
public sealed class LessonsBecomeRulesOnlyWhenConfirmedTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write the report"}""";
    private const string Verdict = "the report names line 11 for the untested branch; it is line 12";
    private const string RuleText = "Name a source line by the number the file shows, never by counting.";

    private static FakeChatProvider WorkerThatIsRepaired() => new(
        Turn.Says(QuickAction),
        Turn.Calls1("write_file", """{"path":"report.md","content":"untested: line 11"}""", "w1"),
        Turn.Says("Wrote the report."),
        Turn.Calls1("edit_file", """{"path":"report.md","old_string":"line 11","new_string":"line 12"}""", "e1"),
        Turn.Says("Fixed the line."));

    private static async Task<List<WorkEvent>> RecordedRunAsync(EngineFixture fx, Orchestrator engine, IRuleStore rules)
    {
        var context = new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []);
        var intent = new Intent(Guid.NewGuid(), "write the report", IntentSource.CommandBar, context, DateTimeOffset.UtcNow);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var events = new List<WorkEvent>();
        await foreach (var ev in new RunRecorder(RunStoreFactory.Create(fx.Workspace), rules: rules)
                           .RecordAsync(engine.SubmitIntentAsync(intent, cts.Token), cts.Token))
            events.Add(ev);
        return events;
    }

    private static string Prompts(FakeChatProvider provider)
        => string.Join("\n", provider.Requests.SelectMany(r => r.Messages).Select(m => m.Content));

    /// <summary>
    /// THE LOOP. A review that failed a step becomes a candidate with what it said, from that run - and reaches no prompt,
    /// this run's or the next one's, until a person keeps it. Dismissed, it is not put to them again when a later run
    /// sees the same thing.
    /// </summary>
    [Fact]
    public async Task A_failed_review_becomes_a_candidate_no_run_is_given_and_a_dismissed_one_does_not_come_back()
    {
        using var fx = new EngineFixture();
        var rules = new SqliteRuleStore(fx.Workspace);
        try
        {
            var worker = WorkerThatIsRepaired();
            var events = await RecordedRunAsync(fx,
                fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
                    reviewProvider: new FakeChatProvider(Verdicts.Fail(Verdict), Verdicts.Pass())), rules);
            Assert.True(events.Has(EventKind.TaskCompleted), events.Text());

            var candidate = Assert.Single(await rules.ListAsync(CancellationToken.None));
            Assert.Equal(RuleStatus.Candidate, candidate.Status);
            Assert.Equal(RuleScope.Workspace, candidate.Scope);
            Assert.Equal(Verdict, candidate.Text);
            Assert.Equal(events[0].RunId, candidate.SourceRunId);
            Assert.Contains("FAIL", candidate.Origin);
            Assert.Empty(AppliedRules.Compose([], [candidate]).Rules);
            Assert.DoesNotContain("Workspace rules", Prompts(worker), StringComparison.Ordinal);

            var book = new RuleBook(rules, new JsonRuleStore(fx.PathOf("global-rules.json")));
            await book.DismissAsync(candidate, CancellationToken.None);

            // The same failure, in the next run.
            await RecordedRunAsync(fx,
                fx.Build(WorkerThatIsRepaired(), EngineFixture.Role("developer"), router: Routers.WithReviewer(),
                    reviewProvider: new FakeChatProvider(Verdicts.Fail(Verdict), Verdicts.Pass())), rules);
            var only = Assert.Single(await rules.ListAsync(CancellationToken.None));
            Assert.Equal(RuleStatus.Dismissed, only.Status);
        }
        finally
        {
            Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
        }
    }

    private const string TwoStepPlan = """
        {"disposition":"task","title":"two steps",
         "steps":[{"title":"first","dependsOn":[]},{"title":"second","dependsOn":[0]}]}
        """;

    private static ComposedEngine Engine(FakeChatProvider provider, string rulesFolder)
    {
        var models = new ModelResolver();
        var settings = new AppSettings { Engine = new() { ProposeChecks = false, ReviewRetries = 0, SuccessRetries = 0 } };
        var worker = EngineFixture.WorkerWith();
        return new ComposedEngine(new SingleProviderFactory(provider), new StaticWorkerProvider([worker], worker.Id),
            new ModelRouter(models), models, new ToolRegistry(EngineFixture.ShippedTools()), [], settings, new LogHub(),
            new SessionApprovals())
        {
            Planner = new Planner(checksAuditEnabled: false),
            Approvals = new ApprovalStore(Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N") + "-permissions.json")),
            GlobalChecks = rulesFolder + "-no-checks",
            RulesFolder = rulesFolder
        };
    }

    private static async Task<List<WorkEvent>> EventsOf(ComposedRun composed)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var events = new List<WorkEvent>();
        await foreach (var ev in composed.Events(cts.Token))
            events.Add(ev);
        return events;
    }

    /// <summary>
    /// The rules are read once, when the run is composed: one kept while it runs is not in its prompts, and is in the
    /// next run's - the planner's and the worker's - with the run's log naming the set and its record keeping it whole.
    /// </summary>
    [Fact]
    public async Task A_rule_kept_after_a_run_was_composed_reaches_the_next_run_and_not_that_one()
    {
        using var fx = new EngineFixture();
        var rulesFolder = fx.PathOf("machine");
        var book = new RuleBook(RuleStoreFactory.Create(fx.Workspace), JsonRuleStore.Global(rulesFolder));
        try
        {
            var first = new FakeChatProvider(Turn.Says(QuickAction), Turn.Says("done"));
            await using (var composed = await RunComposer.ComposeAsync(Engine(first, rulesFolder),
                             new RunRequest(fx.Workspace, "write the report", IntentSource.CommandBar) { Defaults = new(2) },
                             new ScriptedDecisionHandler("allow"), CancellationToken.None))
            {
                await book.AddAsync(RuleText, RuleScope.Global, CancellationToken.None);
                await EventsOf(composed);
            }
            Assert.DoesNotContain(RuleText, Prompts(first), StringComparison.Ordinal);

            var second = new FakeChatProvider(Turn.Says(QuickAction), Turn.Says("done"));
            List<WorkEvent> events;
            await using (var composed = await RunComposer.ComposeAsync(Engine(second, rulesFolder),
                             new RunRequest(fx.Workspace, "write the report", IntentSource.CommandBar) { Defaults = new(2) },
                             new ScriptedDecisionHandler("allow"), CancellationToken.None))
                events = await EventsOf(composed);

            Assert.All(second.Requests.Take(2), r =>
                Assert.Contains(RuleText, string.Join("\n", r.Messages.Select(m => m.Content)), StringComparison.Ordinal));
            var rules = Assert.Single(events, e => e.Summary.StartsWith("Workspace rules given", StringComparison.Ordinal));
            var record = (await RunStoreFactory.Create(fx.Workspace).LoadAsync(events[0].RunId, CancellationToken.None))!;
            Assert.Equal(RuleText, Assert.Single(record.Settings!.Rules!.Rules).Text);
            Assert.Contains(record.Settings.Rules.Hash, rules.Summary, StringComparison.Ordinal);
        }
        finally
        {
            Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
        }
    }

    /// <summary>
    /// A paused run's checkpoint carries the rules whole; edited and then deleted before the resume, the resumed run is
    /// still given the text it started with, and its record says so.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_is_given_the_rules_it_was_started_with()
    {
        using var fx = new EngineFixture();
        var rulesFolder = fx.PathOf("machine");
        var book = new RuleBook(RuleStoreFactory.Create(fx.Workspace), JsonRuleStore.Global(rulesFolder));
        try
        {
            var rule = await book.AddAsync(RuleText, RuleScope.Workspace, CancellationToken.None);
            var engine = Engine(new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") }, rulesFolder);
            var pause = new RunPause();
            pause.Request();
            await using (var composed = await RunComposer.ComposeAsync(engine,
                             new RunRequest(fx.Workspace, "write the report", IntentSource.CommandBar) { Defaults = new(2), Pause = pause },
                             new ScriptedDecisionHandler("allow"), CancellationToken.None))
                Assert.Equal(RunOutcomeKind.Paused, (await EventsOf(composed)).Last().Outcome());

            var kept = Assert.Single(await new JsonCheckpointStore(fx.Workspace).LoadAllAsync(CancellationToken.None));
            Assert.Equal(RuleText, Assert.Single(kept.Settings!.Rules!.Rules).Text);

            var edited = await book.KeepAsync(rule, "Something else entirely.", null, CancellationToken.None);
            await book.DeleteAsync(edited, CancellationToken.None);

            List<WorkEvent> resumed;
            await using (var composed = await RunComposer.ComposeAsync(engine,
                             new RunRequest(fx.Workspace, kept.Request, IntentSource.CommandBar, Resume: kept) { Defaults = new(2) },
                             new ScriptedDecisionHandler("allow"), CancellationToken.None))
            {
                Assert.Equal(RuleText, Assert.Single(composed.Intent.Context.Rules.Rules).Text);
                resumed = await EventsOf(composed);
            }
            Assert.Equal(RunOutcomeKind.Completed, resumed.Last().Outcome());
            var record = (await RunStoreFactory.Create(fx.Workspace).LoadAsync(resumed[0].RunId, CancellationToken.None))!;
            Assert.Equal(RuleText, Assert.Single(record.Settings!.Rules!.Rules).Text);
        }
        finally
        {
            Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
        }
    }

    private static RuleEntry Confirmed(string text, RuleScope scope, int minute)
        => new(Guid.NewGuid(), scope, RuleHeading.Deviations, text, null, DateTimeOffset.UnixEpoch.AddMinutes(minute), RuleStatus.Confirmed);

    /// <summary>
    /// Global before the workspace's, each oldest first; candidates and dismissed entries are given to no run; past the
    /// cap the newest are left out and counted, never cut off in the middle of the set without a word.
    /// </summary>
    [Fact]
    public void Only_confirmed_rules_are_given_global_first_and_the_cap_leaves_the_newest_out_and_says_so()
    {
        var workspace = new[]
        {
            Confirmed("workspace, later", RuleScope.Workspace, 2),
            Confirmed("workspace, earlier", RuleScope.Workspace, 1),
            Confirmed("a candidate", RuleScope.Workspace, 0) with { Status = RuleStatus.Candidate },
            Confirmed("dismissed", RuleScope.Workspace, 0) with { Status = RuleStatus.Dismissed }
        };
        var applied = AppliedRules.Compose([Confirmed("global", RuleScope.Global, 9)], workspace);
        Assert.Equal(["global", "workspace, earlier", "workspace, later"], applied.Rules.Select(r => r.Text));
        Assert.Equal(0, applied.Left);
        Assert.Contains("- workspace, earlier", applied.Block(), StringComparison.Ordinal);
        Assert.Equal("", AppliedRules.None.Block());

        var many = Enumerable.Range(0, 20).Select(i => Confirmed(new string((char)('a' + i), 400), RuleScope.Workspace, i)).ToArray();
        var capped = AppliedRules.Compose([], many);
        Assert.Equal(AppliedRules.MaxChars / 400, capped.Rules.Count);
        Assert.Equal(20 - capped.Rules.Count, capped.Left);
        Assert.Equal(many.Take(capped.Rules.Count).Select(r => r.Text), capped.Rules.Select(r => r.Text));
        Assert.Contains("left out", Orchestrator.RulesNote(capped), StringComparison.Ordinal);
        var cut = AppliedRules.Compose([], [Confirmed(new string('x', 2000), RuleScope.Global, 0)]).Rules[0].Text;
        Assert.Equal(AppliedRules.MaxRuleChars, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
    }

    private static RunRecord Run(params RunEventRecord[] events)
        => new(Guid.NewGuid(), Guid.NewGuid(), "a run", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Completed", events, [], []);

    private static RunEventRecord Failed(int step, string notes)
        => new(DateTimeOffset.UnixEpoch, nameof(EventKind.ReviewFailed), $"[{step}] FAIL (step review): {notes}", step,
            WorkEventPayload.ReviewFailedPayload(step, notes));

    /// <summary>
    /// One candidate per step - its first failure; the ones after it judge the repair - at most a handful per run, none
    /// from a failure that carries no notes, and the same observation in other spacing is the one already decided on.
    /// </summary>
    [Fact]
    public void A_run_proposes_a_step_s_first_failure_a_few_at_most_and_nothing_already_decided()
    {
        var run = Run(
            Failed(1, "first"), Failed(1, "the repair, judged"),
            new RunEventRecord(DateTimeOffset.UnixEpoch, nameof(EventKind.ReviewFailed), "FAIL (step review): no payload", 2),
            Failed(3, "third"), Failed(4, "fourth"), Failed(5, "fifth"), Failed(6, "sixth"), Failed(7, "seventh"));
        var found = LessonCandidates.From(run, []);
        Assert.Equal(["first", "third", "fourth", "fifth", "sixth"], found.Select(c => c.Text));
        Assert.All(found, c => Assert.Equal(run.RunId, c.SourceRunId));
        var cutNotes = LessonCandidates.From(Run(Failed(1, new string('y', 3000))), [])[0].Text;
        Assert.Equal(AppliedRules.MaxRuleChars, cutNotes.Length);
        Assert.EndsWith("…", cutNotes, StringComparison.Ordinal);

        var decided = found[0] with { Status = RuleStatus.Dismissed };
        var again = LessonCandidates.From(Run(Failed(1, "  FIRST "), Failed(2, "new")), [decided]);
        Assert.Equal(["new"], again.Select(c => c.Text));
    }

    /// <summary>The engine's own checks carry what they found too, so a check that sent a step back is a lesson as well.</summary>
    [Fact]
    public void An_engine_check_s_failure_carries_its_notes()
    {
        var payload = WorkEventPayload.EngineCheckPayload(2, "lint", "3 errors in src/a.ts");
        var ev = new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch, EventKind.ReviewFailed, "x", payload);
        Assert.Equal("3 errors in src/a.ts", ev.ReviewNotes());
        Assert.True(ev.ByEngineCheck());
        Assert.Equal("lint", ev.EngineCheckId());
        Assert.Equal("""{"step":2,"engineCheck":true,"checkId":"lint"}""", WorkEventPayload.EngineCheckPayload(2, "lint"));
    }

    /// <summary>
    /// The person's side, through the console's words: keep in their own wording and for every workspace moves the entry
    /// to the Global layer; an id that names two entries names none; each store keeps what it was given.
    /// </summary>
    [Fact]
    public async Task Keeping_a_candidate_in_other_words_for_every_workspace_moves_it_to_the_global_layer()
    {
        using var fx = new EngineFixture();
        var workspace = new JsonRuleStore(fx.PathOf(Path.Combine(".enactive", "rules.json")));
        var global = JsonRuleStore.Global(fx.PathOf("machine"));
        var book = new RuleBook(workspace, global);
        var candidate = LessonCandidates.From(Run(Failed(1, Verdict)), [])[0];
        await workspace.SaveAsync(candidate, CancellationToken.None);

        var output = new StringWriter();
        Assert.Equal(0, await RulesCommand.RunAsync(book,
            ["keep", candidate.Id.ToString("N")[..8], "--text", RuleText, "--global"], output));
        Assert.Empty(await workspace.ListAsync(CancellationToken.None));
        var kept = Assert.Single(await global.ListAsync(CancellationToken.None));
        Assert.Equal((RuleStatus.Confirmed, RuleScope.Global, RuleText, candidate.Source), (kept.Status, kept.Scope, kept.Text, kept.Source));

        Assert.Equal(0, await RulesCommand.RunAsync(book, [], output = new StringWriter()));
        Assert.Contains(RuleText, output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await RulesCommand.RunAsync(book, ["dismiss", "zz"], new StringWriter()));

        var a = await book.AddAsync("a", RuleScope.Workspace, CancellationToken.None);
        var b = await book.AddAsync("b", RuleScope.Workspace, CancellationToken.None);
        await workspace.SaveAsync(b with { Id = Guid.Parse(a.Id.ToString("N")[..8] + Guid.NewGuid().ToString("N")[8..]) }, CancellationToken.None);
        Assert.Equal(2, await RulesCommand.RunAsync(book, ["delete", a.Id.ToString("N")[..8]], output = new StringWriter()));
        Assert.Contains("give more of the id", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The SQLite store keeps an entry whole, replaces it by id and deletes it - across instances, as runs read it.</summary>
    [Fact]
    public async Task The_sqlite_store_keeps_replaces_and_deletes_rules_by_id()
    {
        using var fx = new EngineFixture();
        try
        {
            var entry = Confirmed(RuleText, RuleScope.Workspace, 1) with { Origin = "o", Source = "review:1" };
            await new SqliteRuleStore(fx.Workspace).SaveAsync(entry, CancellationToken.None);
            await new SqliteRuleStore(fx.Workspace).SaveAsync(entry with { Text = "changed" }, CancellationToken.None);
            var read = Assert.Single(await new SqliteRuleStore(fx.Workspace).ListAsync(CancellationToken.None));
            Assert.Equal(entry with { Text = "changed" }, read);
            await new SqliteRuleStore(fx.Workspace).DeleteAsync(entry.Id, CancellationToken.None);
            Assert.Empty(await new SqliteRuleStore(fx.Workspace).ListAsync(CancellationToken.None));
        }
        finally
        {
            Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
        }
    }

    private sealed class KeptInbox : Enactive.Core.Inbox.IInboxStore
    {
        public List<Enactive.Core.Inbox.InboxItem> Items { get; } = [];
        public Task AppendAsync(Enactive.Core.Inbox.InboxItem item, CancellationToken ct) { Items.Add(item); return Task.CompletedTask; }
        public Task<IReadOnlyList<Enactive.Core.Inbox.InboxItem>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Enactive.Core.Inbox.InboxItem>>(Items);
        public Task MarkReadAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task MarkAllReadAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static async IAsyncEnumerable<WorkEvent> AFailedStep(Guid run)
    {
        var task = Guid.NewGuid();
        yield return new WorkEvent(Guid.NewGuid(), task, run, DateTimeOffset.UtcNow, EventKind.IntentReceived, "Intent: write the report", null);
        yield return new WorkEvent(Guid.NewGuid(), task, run, DateTimeOffset.UtcNow, EventKind.ReviewFailed,
            $"[1] FAIL (step review): {Verdict}", WorkEventPayload.ReviewFailedPayload(1, Verdict));
        await Task.Yield();
        yield return new WorkEvent(Guid.NewGuid(), task, run, DateTimeOffset.UtcNow, EventKind.TaskCompleted, "done", null);
    }

    /// <summary>
    /// A run nobody watched says in the Inbox that it left lessons to decide - the one place such a run reports back; a
    /// watched run does not, its person sees them beside its artifacts.
    /// </summary>
    [Fact]
    public async Task A_run_nobody_watched_says_in_the_inbox_that_it_left_lessons_and_a_watched_one_does_not()
    {
        using var fx = new EngineFixture();
        try
        {
            var inbox = new KeptInbox();
            var run = Guid.NewGuid();
            await foreach (var _ in new RunRecorder(RunStoreFactory.Create(fx.Workspace), workspaceId: fx.Workspace.Id,
                               rules: new SqliteRuleStore(fx.Workspace), lessonInbox: inbox).RecordAsync(AFailedStep(run), CancellationToken.None)) { }
            var item = Assert.Single(inbox.Items);
            Assert.Equal((LessonCandidates.InboxKind, run, fx.Workspace.Id, "1 lesson to decide"), (item.Kind, item.RunId, item.WorkspaceId, item.Title));
            Assert.Contains(Verdict, item.Summary, StringComparison.Ordinal);

            // Watched: no inbox handed in. And the same observation again proposes nothing, so says nothing.
            await foreach (var _ in new RunRecorder(RunStoreFactory.Create(fx.Workspace), rules: new SqliteRuleStore(fx.Workspace))
                               .RecordAsync(AFailedStep(Guid.NewGuid()), CancellationToken.None)) { }
            await foreach (var _ in new RunRecorder(RunStoreFactory.Create(fx.Workspace), rules: new SqliteRuleStore(fx.Workspace), lessonInbox: inbox)
                               .RecordAsync(AFailedStep(Guid.NewGuid()), CancellationToken.None)) { }
            Assert.Single(inbox.Items);
        }
        finally
        {
            Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
        }
    }

    private sealed class Refusing(ILessonChanges inner) : ILessonChanges
    {
        public Task<IReadOnlyList<RuleEntry>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public string? WhyNot(RuleScope scope) => scope == RuleScope.Global ? "not yours" : null;
        public Task<string?> KeepAsync(RuleEntry entry, string? text, RuleScope scope, CancellationToken ct) => inner.KeepAsync(entry, text, scope, ct);
        public Task<string?> DismissAsync(RuleEntry entry, CancellationToken ct) => inner.DismissAsync(entry, ct);
        public Task<string?> DeleteAsync(RuleEntry entry, CancellationToken ct) => inner.DeleteAsync(entry, ct);
        public Task<string?> AddAsync(string text, RuleScope scope, CancellationToken ct) => inner.AddAsync(text, scope, ct);
    }

    /// <summary>
    /// The panel's list (the window's tab and window, the service's page): candidates apart from rules, the tab counting
    /// what waits; a candidate kept in other words for every workspace lands in the Global layer as those words; a change
    /// the host refuses is said and writes nothing.
    /// </summary>
    [Fact]
    public async Task The_lessons_panel_keeps_a_candidate_in_the_person_s_words_and_says_a_refusal()
    {
        using var fx = new EngineFixture();
        var workspace = new JsonRuleStore(fx.PathOf(Path.Combine(".enactive", "rules.json")));
        var global = JsonRuleStore.Global(fx.PathOf("machine"));
        var candidate = LessonCandidates.From(Run(Failed(1, Verdict)), [])[0];
        await workspace.SaveAsync(candidate, CancellationToken.None);
        await workspace.SaveAsync(Confirmed("kept earlier", RuleScope.Workspace, 1), CancellationToken.None);

        var panel = new LessonsViewModel(new BookLessons(new RuleBook(workspace, global)), "Lessons - here");
        await panel.RefreshAsync();
        Assert.Equal("Lessons (1)", panel.TabLabel);
        Assert.Equal(["kept earlier"], panel.Rules.Select(r => r.Text));
        var row = Assert.Single(panel.Candidates);
        Assert.Contains("step 1", row.Origin, StringComparison.Ordinal);
        Assert.StartsWith("from run", row.Origin, StringComparison.Ordinal);
        var written = new LessonRow(new RuleEntry(Guid.NewGuid(), RuleScope.Workspace, RuleHeading.Interpretations, "x", null,
            DateTimeOffset.UnixEpoch, RuleStatus.Confirmed) { Origin = "written by a person" }, true, (_, _) => Task.CompletedTask);
        Assert.Equal("this workspace · written by a person", written.Meta);

        row.Text = RuleText;
        row.Global = true;
        await row.KeepCommand.ExecuteAsync();
        Assert.Null(panel.Said);
        Assert.Equal("Lessons", panel.TabLabel);
        Assert.Equal((RuleScope.Global, RuleText), (Assert.Single(await global.ListAsync(CancellationToken.None)) is var g ? (g.Scope, g.Text) : default));
        Assert.Equal([RuleText, "kept earlier"], panel.Rules.Select(r => r.Text));

        var refused = new LessonsViewModel(new Refusing(new BookLessons(new RuleBook(workspace, global))), "Lessons - here")
            { NewText = "for everywhere", NewGlobal = true };
        await refused.AddCommand.ExecuteAsync();
        Assert.Equal("not yours", refused.Said);
        Assert.Single(await global.ListAsync(CancellationToken.None));
    }

    /// <summary>
    /// The bench's --rules (Plan Phase 5 measurement): each text kept as a confirmed rule of the run's own workspace, in the
    /// store a run there reads, in the order given - so a measured run is given them, and the machine's Global layer is not
    /// touched.
    /// </summary>
    [Fact]
    public async Task The_bench_gives_a_measured_run_its_rules_in_the_run_s_own_workspace()
    {
        using var fx = new EngineFixture();
        try
        {
            var file = fx.PathOf("rules.json");
            File.WriteAllText(file, """["Name a computer only from a command's output.", " ", "Second."]""");
            await Enactive.Bench.Rules.SeedAsync(fx.Root, file, CancellationToken.None);

            var kept = await RuleStoreFactory.Create(fx.Workspace).ListAsync(CancellationToken.None);
            Assert.All(kept, r => Assert.Equal((RuleStatus.Confirmed, RuleScope.Workspace), (r.Status, r.Scope)));
            Assert.Equal(["Name a computer only from a command's output.", "Second."],
                AppliedRules.Compose([], kept).Rules.Select(r => r.Text));
        }
        finally
        {
            Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
        }
    }

    /// <summary>
    /// A lesson made into a check (Plan Phase 5.4): the panel asks for the Checks form with an id suggested from the lesson;
    /// the form opens with the id and the lesson's words, and NO command - saved without one it is refused and nothing is
    /// written, so the scaffold never runs a guess; saved with the person's command it is a check, and the lesson is set
    /// aside saying what it became.
    /// </summary>
    [Fact]
    public async Task A_lesson_made_into_a_check_opens_the_form_without_a_command_and_is_set_aside_once_saved()
    {
        Assert.Equal("never-change-the-project", LessonsViewModel.CheckIdFor("Never change the project file, ever."));
        Assert.Equal("lesson-12-volumes", LessonsViewModel.CheckIdFor("12 volumes!"));
        Assert.Equal("lesson", LessonsViewModel.CheckIdFor("…"));

        using var fx = new EngineFixture();
        var workspace = new JsonRuleStore(fx.PathOf(Path.Combine(".enactive", "rules.json")));
        var candidate = LessonCandidates.From(Run(Failed(1, "the build was not run after the change")), [])[0];
        await workspace.SaveAsync(candidate, CancellationToken.None);
        var panel = new LessonsViewModel(new BookLessons(new RuleBook(workspace, JsonRuleStore.Global(fx.PathOf("machine")))), "Lessons");
        await panel.RefreshAsync();
        (LessonRow Row, string Id)? asked = null;
        panel.CheckRequested += (row, id) => asked = (row, id);
        await panel.Candidates[0].CheckCommand.ExecuteAsync();
        Assert.Equal("the-build-was-not", asked!.Value.Id);

        var checksFolder = fx.PathOf("global-checks");
        var store = new Enactive.Workspace.CheckStore(fx.Root, checksFolder);
        var checks = new ChecksViewModel(store, Enactive.Core.Checks.CheckScope.Workspace, new StoreCheckChanges(store, fx.Root));
        checks.AddFrom(asked.Value.Id, asked.Value.Row.Text);
        Assert.Equal(("the-build-was-not", "the build was not run after the change", ""),
            (checks.Draft!.Id, checks.Draft.Description, checks.Draft.Command));
        Assert.Equal("Run", checks.Draft.FireOn);
        checks.Draft.SaveCommand.Execute(null);
        Assert.Null(checks.LastSaved);
        Assert.False(File.Exists(fx.PathOf(Path.Combine(".enactive", "checks", "the-build-was-not.json"))));

        checks.Draft!.Command = "dotnet build";
        checks.Draft.SaveCommand.Execute(null);
        Assert.True(checks.LastSaved == "the-build-was-not", checks.Draft?.Problems);
        Assert.True(File.Exists(fx.PathOf(Path.Combine(".enactive", "checks", "the-build-was-not.json"))));

        await panel.MadeIntoCheckAsync(asked.Value.Row, checks.LastSaved!);
        Assert.Empty(panel.Candidates);
        var aside = Assert.Single(await workspace.ListAsync(CancellationToken.None));
        Assert.Equal(RuleStatus.Dismissed, aside.Status);
        Assert.EndsWith("made into the check 'the-build-was-not'", aside.Origin, StringComparison.Ordinal);
    }
}
