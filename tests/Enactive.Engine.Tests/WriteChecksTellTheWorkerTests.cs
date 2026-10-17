namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Checks;
using Enactive.Core.Context;
using Enactive.Core.Execution;
using Enactive.Core.Intents;
using Enactive.Core.Templates;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Plan Phase 3.2, <c>FireOn = Write</c>: after a successful write that changed a path a write check's globs cover, the
/// check runs and what it said is added to the result of that write - the tool result the worker reads next - and to the
/// step's journal as the engine's, for the reviewer. Always advisory, once per check per turn, only for checks short
/// enough to run inside the worker's turn, never in a staged run, and nothing at all when the run has no write checks.
/// Not code-specific: the "lint" here is any command over the files a step writes.
/// </summary>
public sealed class WriteChecksTellTheWorkerTests
{
    private const string OneStep = """
        {"disposition":"task","title":"edit","steps":[{"title":"Edit the notes","dependsOn":[]}]}
        """;

    private const string TwoSteps = """
        {"disposition":"task","title":"edit",
         "steps":[{"title":"Edit the notes","dependsOn":[]},{"title":"Edit them again","dependsOn":[0]}]}
        """;

    /// <summary>The checks' command tool: records each command and whether the written files were there when it ran.</summary>
    /// <param name="numbered">Ends the output with which run this is (#1, #2...), to tell the runs apart.</param>
    /// <param name="noExitCode">Reports no exit code, so the check is NOT CHECKED with its output in the reason.</param>
    private sealed class Checker(int exitCode = 0, string output = "", bool hangs = false, bool numbered = false, bool noExitCode = false) : ITool
    {
        public List<string> Seen { get; } = [];
        public List<string[]> FilesThen { get; } = [];
        public bool Cancelled { get; private set; }
        /// <summary>Called as each run starts - a test's way to stop the run while a check is running.</summary>
        public Action? OnRun { get; set; }
        public ToolDefinition Definition => new("run_command", "run", "{\"type\":\"object\"}", Kind: ToolKind.Command, RunsSuccessChecks: true);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            using (var doc = JsonDocument.Parse(argumentsJson))
                Seen.Add(doc.RootElement.GetProperty("command").GetString()!);
            var notes = Path.Combine(ctx.WorkspaceRoot, "notes");
            FilesThen.Add(Directory.Exists(notes) ? Directory.GetFiles(notes).Select(Path.GetFileName).OfType<string>().Order().ToArray() : []);
            OnRun?.Invoke();
            if (hangs)
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            var said = numbered ? $"{output} #{Seen.Count}" : output;
            return new ToolResult(exitCode == 0, said, exitCode == 0 ? null : $"exit code {exitCode}", [],
                noExitCode ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["exitCode"] = exitCode });
        }
    }

    private static CheckSnapshot Snapshot(params CheckDefinition[] checks)
        => new(checks.Select(c => new ResolvedCheck(c, CheckOrigin.Workspace)).ToArray(), []);

    private static CheckDefinition Spell(int timeout = 10, string matches = "notes/**", CheckFire fire = CheckFire.Write)
        => new("spell", "Spells the notes", "spell notes", [matches], fire, CheckSeverity.Advisory, timeout, null);

    private static (EngineFixture Fx, Checker Checker) Fixture(Checker checker, CheckSnapshot? checks)
    {
        var fx = new EngineFixture { Checks = checks };
        fx.ToolsOverride = EngineFixture.ShippedTools().Where(t => t.Definition.Name != "run_command").Append(checker).ToArray();
        return (fx, checker);
    }

    private static Turn Writes(params (string Id, string Path)[] writes)
        => new(null, writes.Select(w => new ToolCall(w.Id, "write_file",
            JsonSerializer.Serialize(new { path = w.Path, content = "teh notes" }))).ToArray());

    /// <summary>What the worker was shown as the result of the call <paramref name="id"/>, in the first request after it.</summary>
    private static string? ResultOf(FakeChatProvider worker, string id)
        => worker.Requests.SelectMany(r => r.Messages).FirstOrDefault(m => m.Role == ChatRole.Tool && m.ToolCallId == id)?.Content;

    private static IEnumerable<string> ToolResults(FakeChatProvider worker)
        => worker.Requests[^1].Messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.Content ?? "");

    [Fact]
    public async Task A_matching_write_is_told_what_the_check_said_and_the_reviewer_sees_it_and_a_fail_decides_nothing()
    {
        var (fx, checker) = Fixture(new Checker(1, "notes/a.md:1: 'teh' is not a word"), Snapshot(Spell()));
        using var _ = fx;
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md")), Turn.Says("Edited the notes."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "edit the notes");

        Assert.Equal(["spell notes"], checker.Seen);
        var told = ResultOf(worker, "w1");
        Assert.NotNull(told);
        Assert.Contains("Write-time check 'spell': FAIL (exit 1)", told, StringComparison.Ordinal);
        Assert.Contains("'teh' is not a word", told, StringComparison.Ordinal);
        Assert.Contains("advisory", told, StringComparison.OrdinalIgnoreCase);
        // The write's own result comes first: the check is added to it, not put in its place.
        Assert.DoesNotMatch(@"^\s*\[Write-time check", told!);

        var reviewed = string.Join("\n", reviewer.Requests.SelectMany(r => r.Messages).Select(m => m.Content));
        Assert.Contains(WriteChecks.JournalTool, reviewed, StringComparison.Ordinal);
        Assert.Contains("'teh' is not a word", reviewed, StringComparison.Ordinal);

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        // A check's result like any other (Plan Phase 3.3): a criterion event, fired on write.
        Assert.Contains(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains("Write-time check 'spell': FAIL", StringComparison.Ordinal)
                                     && e.CriterionCheck()?.FiredOn == CheckBoundary.Write);
    }

    /// <summary>
    /// WriteChecks.WorkerOutputChars: a long finding is told to the worker as its start and its END - where a linter's
    /// count and a compiler's summary are - with the cut said, so a part does not read as the whole.
    /// </summary>
    [Fact]
    public async Task A_long_finding_is_told_to_the_worker_in_part_and_says_so()
    {
        var findings = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"notes/a.md:{i}: 'teh' is not a word")) + "\n200 words misspelled";
        var (fx, _) = Fixture(new Checker(1, findings), Snapshot(Spell()));
        using var __ = fx;
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md")), Turn.Says("Edited."));

        await fx.RunAsync(fx.Build(worker), "edit the notes");

        var told = ResultOf(worker, "w1")!;
        Assert.Contains("characters not shown here; the end follows", told, StringComparison.Ordinal);
        Assert.Contains("notes/a.md:1: 'teh' is not a word", told, StringComparison.Ordinal);
        Assert.Contains("200 words misspelled", told, StringComparison.Ordinal);
        Assert.DoesNotContain("notes/a.md:100: ", told, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_no_check_covers_runs_nothing_and_is_told_nothing()
    {
        var (fx, checker) = Fixture(new Checker(1, "bad"), Snapshot(Spell()));
        using var _ = fx;
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "docs/a.md")), Turn.Says("Edited."));

        var events = await fx.RunAsync(fx.Build(worker), "edit the notes");

        Assert.Empty(checker.Seen);
        Assert.DoesNotContain("Write-time check", ResultOf(worker, "w1")!, StringComparison.Ordinal);
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    [Fact]
    public async Task Two_matching_writes_in_one_turn_run_the_check_once_after_the_last_and_told_with_it()
    {
        var (fx, checker) = Fixture(new Checker(0, "all spelled"), Snapshot(Spell()));
        using var _ = fx;
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md"), ("w2", "notes/b.md")), Turn.Says("Edited."));

        var events = await fx.RunAsync(fx.Build(worker), "edit the notes");

        Assert.Single(checker.Seen);
        Assert.Equal(["a.md", "b.md"], checker.FilesThen.Single());
        Assert.DoesNotContain("Write-time check", ResultOf(worker, "w1")!, StringComparison.Ordinal);
        Assert.Contains("Write-time check 'spell': PASS (exit 0) - run after writing notes/a.md, notes/b.md", ResultOf(worker, "w2")!, StringComparison.Ordinal);
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    [Fact]
    public async Task Writes_in_two_turns_run_the_check_once_per_turn()
    {
        var (fx, checker) = Fixture(new Checker(0, "all spelled"), Snapshot(Spell()));
        using var _ = fx;
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md")), Writes(("w2", "notes/b.md")), Turn.Says("Edited."));

        await fx.RunAsync(fx.Build(worker), "edit the notes");

        Assert.Equal(2, checker.Seen.Count);
        Assert.Contains("Write-time check 'spell': PASS", ResultOf(worker, "w1")!, StringComparison.Ordinal);
        Assert.Contains("Write-time check 'spell': PASS", ResultOf(worker, "w2")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_too_long_for_a_turn_is_not_run_at_write_time_and_said_once()
    {
        var (fx, checker) = Fixture(new Checker(), Snapshot(Spell(timeout: CheckLimits.WriteTimeoutLimitSeconds + 1)));
        using var _ = fx;
        var worker = new FakeChatProvider(Turn.Says(TwoSteps), Writes(("w1", "notes/a.md")), Turn.Says("Edited."),
            Writes(("w2", "notes/b.md")), Turn.Says("Edited again."));

        var events = await fx.RunAsync(fx.Build(worker), "edit the notes");

        Assert.Empty(checker.Seen);
        Assert.Single(events, e => e.Summary.Contains("'spell'", StringComparison.Ordinal)
                                   && e.Summary.Contains("not run at write time", StringComparison.Ordinal));
        Assert.DoesNotContain("Write-time check", ResultOf(worker, "w1")!, StringComparison.Ordinal);
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    [Fact]
    public async Task A_check_the_policy_would_ask_about_is_not_run_and_the_worker_is_told_once()
    {
        var (fx, checker) = Fixture(new Checker(), Snapshot(Spell()));
        using var _ = fx;
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md")), Writes(("w2", "notes/b.md")), Turn.Says("Edited."));
        var asks = PermissionPolicy.PermissiveDefault with { AskBefore = ["run_command"] };

        var events = await fx.RunAsync(fx.Build(worker, policy: asks), "edit the notes");

        Assert.Empty(checker.Seen);
        Assert.Empty(fx.Decisions.Requests);
        Assert.Single(ToolResults(worker), r => r.Contains("Write-time check 'spell': not run: needs permission", StringComparison.Ordinal));
        Assert.Contains("not run: needs permission", ResultOf(worker, "w1")!, StringComparison.Ordinal);
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    [Fact]
    public async Task A_staged_run_runs_no_write_check_and_says_why_once()
    {
        var (fx, checker) = Fixture(new Checker(), Snapshot(Spell()));
        using var _ = fx;
        var worker = new FakeChatProvider(Turn.Says(TwoSteps), Writes(("w1", "notes/a.md")), Turn.Says("Edited."),
            Writes(("w2", "notes/b.md")), Turn.Says("Edited again."));

        var events = await fx.RunAsync(fx.Build(worker, settings: new RunSettings(2, "Execute", "developer", Staged: true)), "edit the notes");

        Assert.Empty(checker.Seen);
        Assert.Single(events, e => e.Summary.Contains("staged", StringComparison.OrdinalIgnoreCase)
                                   && e.Summary.Contains("write-time checks", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ToolResults(worker), r => r.Contains("Write-time check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_check_that_outruns_its_own_timeout_is_stopped_and_said_so()
    {
        var (fx, checker) = Fixture(new Checker(hangs: true), Snapshot(Spell(timeout: 1)));
        using var _ = fx;
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md")), Turn.Says("Edited."));

        var events = await fx.RunAsync(fx.Build(worker), "edit the notes");

        Assert.Single(checker.Seen);
        Assert.Contains("Write-time check 'spell': did not finish within 1s", ResultOf(worker, "w1")!, StringComparison.Ordinal);
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>
    /// The regression guard: a run whose checks hold no enabled write check - none at all, a step check, a write check
    /// turned off - and one whose write check covers nothing the step writes show the worker, and the feed, exactly what
    /// they showed before write checks existed.
    /// </summary>
    [Fact]
    public async Task Without_a_write_check_that_applies_the_worker_and_the_feed_see_byte_for_byte_what_they_saw_before()
    {
        async Task<(string[] Results, string[] Events)> RunWith(CheckSnapshot? checks)
        {
            var (fx, checker) = Fixture(new Checker(1, "bad"), checks);
            using var _ = fx;
            var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md"), ("w2", "notes/b.md")),
                Writes(("w3", "notes/a.md")), Turn.Says("Edited."));
            var events = await fx.RunAsync(fx.Build(worker), "edit the notes");
            Assert.Empty(checker.Seen);
            return (ToolResults(worker).ToArray(),
                // The fixture's folder is a new one per run, and the workspace is named after it.
                // The run's one line on which checks judge it (WorkspaceChecks.Inventory) is said whenever there are any -
                // it is about the run's checks, not about write time, and is left out of the comparison.
                events.Where(e => !e.Summary.StartsWith("Declared checks judging this run", StringComparison.Ordinal))
                    .Select(e => $"{e.Kind}: {e.Summary.Replace(fx.Root, "<root>", StringComparison.OrdinalIgnoreCase)
                    .Replace(fx.Workspace.Name, "<workspace>", StringComparison.OrdinalIgnoreCase)}").ToArray());
        }

        var without = await RunWith(null);
        // The step check covers other files, so whatever runs step checks leaves this run alone too.
        var withOthers = await RunWith(Snapshot(Spell(matches: "elsewhere/**", fire: CheckFire.Step),
            Spell(timeout: 5) with { Id = "off", Enabled = false }));
        // A write check that IS armed for the step, and covers nothing it writes.
        var coveringNothing = await RunWith(Snapshot(Spell(matches: "elsewhere/**")));

        Assert.Equal(3, without.Results.Length);
        Assert.Equal(without.Results, withOthers.Results);
        Assert.Equal(without.Results, coveringNothing.Results);
        Assert.Equal(without.Events, withOthers.Events);
        Assert.Equal(without.Events, coveringNothing.Events);
    }

    /// <summary>
    /// A write check declared the obvious way - a command and the paths it is about, no timeout - runs at write time.
    /// Given the general two-minute default it was over the write-time limit, and the feature did nothing by default.
    /// </summary>
    [Fact]
    public async Task A_write_check_declared_without_a_timeout_runs_at_write_time()
    {
        var (fx, checker) = Fixture(new Checker(0, "all spelled"), null);
        using var _ = fx;
        fx.Write(Path.Combine(".enactive", "checks", "spell.json"),
            """{"id":"spell","command":"spell notes","matches":["notes/**"],"fireOn":"Write"}""");
        fx.Checks = new Enactive.Workspace.CheckStore(fx.Root, fx.Root + "-no-global").Snapshot();
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md")), Turn.Says("Edited."));

        var events = await fx.RunAsync(fx.Build(worker), "edit the notes");

        Assert.Equal(["spell notes"], checker.Seen);
        Assert.Contains("Write-time check 'spell': PASS", ResultOf(worker, "w1")!, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Summary.Contains("not run at write time", StringComparison.Ordinal));
    }

    /// <summary>
    /// One journal entry per check per step, the latest run's: the engine's entries are kept longest when the evidence is
    /// shortened, and one per turn of an edit loop crowded the step's own calls out of what the reviewer reads.
    /// </summary>
    [Fact]
    public async Task The_reviewer_is_shown_the_latest_run_of_a_check_once_not_every_turn()
    {
        var (fx, checker) = Fixture(new Checker(1, "'teh' is not a word", numbered: true), Snapshot(Spell()));
        using var _ = fx;
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md")), Writes(("w2", "notes/a.md")),
            Writes(("w3", "notes/b.md")), Turn.Says("Edited."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        await fx.RunAsync(fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer), "edit the notes");

        Assert.Equal(3, checker.Seen.Count);
        var reviewed = string.Join("\n", reviewer.Requests.SelectMany(r => r.Messages).Select(m => m.Content));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(reviewed, "Run by the engine after writing"));
        Assert.Contains("'teh' is not a word #3", reviewed, StringComparison.Ordinal);
        Assert.Contains("the latest of 3 times", reviewed, StringComparison.Ordinal);
        Assert.DoesNotContain("'teh' is not a word #1", reviewed, StringComparison.Ordinal);
    }

    /// <summary>WriteChecks.WorkerOutputChars holds for a check that checked nothing too: its reason can carry the output.</summary>
    [Fact]
    public async Task A_check_that_checked_nothing_is_told_within_the_same_cap()
    {
        var output = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"line {i} of what the checker printed"));
        var (fx, _) = Fixture(new Checker(output: output, noExitCode: true), Snapshot(Spell()));
        using var __ = fx;
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md")), Turn.Says("Edited."));

        await fx.RunAsync(fx.Build(worker), "edit the notes");

        var told = ResultOf(worker, "w1")!;
        var line = told[told.IndexOf("[Write-time check", StringComparison.Ordinal)..];
        Assert.StartsWith("[Write-time check 'spell': NOT CHECKED", line, StringComparison.Ordinal);
        Assert.Contains("not shown here", line, StringComparison.Ordinal);
        Assert.True(line.Length < WriteChecks.WorkerOutputChars + 300, $"{line.Length} characters told: {line}");
    }

    /// <summary>A write that failed, or was refused, changed nothing a check could be owed for.</summary>
    [Fact]
    public async Task A_failed_or_refused_write_runs_no_check()
    {
        var (fx, checker) = Fixture(new Checker(), Snapshot(Spell(matches: "**")));
        using (fx)
        {
            var worker = new FakeChatProvider(Turn.Says(OneStep),
                Turn.Calls1("edit_file", """{"path":"notes/missing.md","old_string":"a","new_string":"b"}""", "e1"),
                Writes(("w1", "../outside/notes/a.md")), Turn.Says("Could not edit."));
            await fx.RunAsync(fx.Build(worker), "edit the notes");
            Assert.DoesNotContain(ToolResults(worker), r => r.Contains("Write-time check", StringComparison.Ordinal));
        }
        Assert.Empty(checker.Seen);

        var (denied, deniedChecker) = Fixture(new Checker(), Snapshot(Spell(matches: "**")));
        using (denied)
        {
            var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md")), Turn.Says("Could not edit."));
            await denied.RunAsync(denied.Build(worker, policy: PermissionPolicy.PermissiveDefault with { Deny = ["write_file"] }), "edit the notes");
            Assert.False(denied.Exists("notes/a.md"));
        }
        Assert.Empty(deniedChecker.Seen);
    }

    /// <summary>
    /// Stopping the run while a write check runs stops the check and the run: the cancellation is not caught as the
    /// check's own timeout and told to the worker as "did not finish", with the step going on as if nothing were asked.
    /// </summary>
    [Fact]
    public async Task Stopping_the_run_during_a_write_check_stops_it_and_is_not_told_as_the_checks_result()
    {
        var (fx, checker) = Fixture(new Checker(hangs: true), Snapshot(Spell(timeout: CheckLimits.WriteTimeoutLimitSeconds)));
        using var _ = fx;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        checker.OnRun = () => stop.CancelAfter(TimeSpan.FromMilliseconds(50));
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md")), Turn.Says("Edited."));
        var context = new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []);
        var intent = new Intent(Guid.NewGuid(), "edit the notes", IntentSource.CommandBar, context, DateTimeOffset.UtcNow);

        var events = new List<WorkEvent>();
        var stopped = false;
        try
        {
            await foreach (var ev in fx.Build(worker).SubmitIntentAsync(intent, stop.Token))
                events.Add(ev);
        }
        catch (OperationCanceledException) { stopped = true; }

        Assert.True(checker.Cancelled);
        Assert.True(stopped || events.Last().Outcome() == RunOutcomeKind.Cancelled, events.Text());
        Assert.DoesNotContain(events, e => e.Summary.Contains("Write-time check", StringComparison.Ordinal));
        Assert.Equal(2, worker.Requests.Count);
    }

    /// <summary>A command the policy denies is said to be denied - not to need an approval nobody can give.</summary>
    [Fact]
    public async Task A_check_the_policy_denies_is_not_run_and_the_worker_is_told_it_is_denied()
    {
        var (fx, checker) = Fixture(new Checker(), Snapshot(Spell()));
        using var _ = fx;
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md")), Turn.Says("Edited."));

        await fx.RunAsync(fx.Build(worker, policy: PermissionPolicy.PermissiveDefault with { Deny = ["run_command"] }), "edit the notes");

        Assert.Empty(checker.Seen);
        var told = ResultOf(worker, "w1")!;
        Assert.Contains("Write-time check 'spell': not run: the policy denies this command", told, StringComparison.Ordinal);
        Assert.DoesNotContain("approval", told, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A clock a test moves itself.</summary>
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    /// <summary>
    /// WriteChecks.TurnBudgetSeconds: one turn's write checks together hold the worker's turn for no longer than the
    /// budget. A check runs only while its own timeout still fits in what is left; the rest wait for the next turn, and
    /// the worker is told so once.
    /// </summary>
    [Fact]
    public async Task One_turns_write_checks_keep_to_the_turns_budget_and_say_which_waited()
    {
        using var fx = new EngineFixture();
        var tools = new Enactive.Tools.ToolRegistry(EngineFixture.ShippedTools());
        List<ChatMessage> messages = [ChatMessage.Tool("w1", "Wrote notes/a.md")];
        var frame = new StepFrame(Guid.NewGuid(), Guid.NewGuid(), 1, fx.Workspace, tools.Definitions, messages, new ExecutionJournal(),
            new(), fx.Artifacts.BeginStep());
        var each = WriteChecks.TurnBudgetSeconds * 2 / 5;
        var checks = WriteChecks.ForStep(Snapshot(Spell(each) with { Id = "c1" }, Spell(each) with { Id = "c2" }, Spell(each) with { Id = "c3" }),
            staged: false, frame, out _)!;
        var clock = new Clock();
        checks.Time = clock;
        checks.Wrote(new ToolCall("w1", "write_file", "{}"),
            new ToolResult(true, "ok", null, [], new Dictionary<string, object?>(), ChangedPaths: ["notes/a.md"]));
        var ran = new List<string>();

        var said = await checks.RunOwedAsync(null, (criterion, _) =>
        {
            ran.Add(criterion.Name);
            clock.Advance(TimeSpan.FromSeconds(each));
            return Task.FromResult(new CriterionResult(criterion.Name, criterion.Command, false, CriterionOutcome.Passed, 0, null));
        }, CancellationToken.None);

        Assert.Equal(["c1", "c2"], ran);
        Assert.Contains("[Write-time check(s) 'c3': not run this turn", messages[0].Content, StringComparison.Ordinal);
        Assert.Single(said, s => s.Line.Contains("not run this turn", StringComparison.Ordinal));
        Assert.False(checks.Owed);
    }

    /// <summary>
    /// The run being stopped while a check runs is the run's, not the check's: it propagates out of the write checks, and
    /// the worker is told nothing - caught as the check's own timeout, it read as a slow check and the stop was swallowed.
    /// </summary>
    [Fact]
    public async Task Stopping_the_run_while_a_check_runs_propagates_and_tells_the_worker_nothing()
    {
        using var fx = new EngineFixture();
        var tools = new Enactive.Tools.ToolRegistry(EngineFixture.ShippedTools());
        List<ChatMessage> messages = [ChatMessage.Tool("w1", "Wrote notes/a.md")];
        var frame = new StepFrame(Guid.NewGuid(), Guid.NewGuid(), 1, fx.Workspace, tools.Definitions, messages, new ExecutionJournal(),
            new(), fx.Artifacts.BeginStep());
        var checks = WriteChecks.ForStep(Snapshot(Spell(CheckLimits.WriteTimeoutLimitSeconds)), staged: false, frame, out _)!;
        checks.Wrote(new ToolCall("w1", "write_file", "{}"),
            new ToolResult(true, "ok", null, [], new Dictionary<string, object?>(), ChangedPaths: ["notes/a.md"]));
        using var stop = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checks.RunOwedAsync(null, async (criterion, token) =>
        {
            stop.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return new CriterionResult(criterion.Name, criterion.Command, false, CriterionOutcome.Passed, 0, null);
        }, stop.Token));

        Assert.Equal("Wrote notes/a.md", messages[0].Content);
        Assert.Empty(frame.Journal.Actions);
    }
}
