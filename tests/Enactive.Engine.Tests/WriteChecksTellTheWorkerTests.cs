namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Checks;
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
    private sealed class Checker(int exitCode = 0, string output = "", bool hangs = false) : ITool
    {
        public List<string> Seen { get; } = [];
        public List<string[]> FilesThen { get; } = [];
        public string? Root { get; set; }
        public ToolDefinition Definition => new("run_command", "run", "{\"type\":\"object\"}", Kind: ToolKind.Command, RunsSuccessChecks: true);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            using (var doc = JsonDocument.Parse(argumentsJson))
                Seen.Add(doc.RootElement.GetProperty("command").GetString()!);
            var notes = Path.Combine(ctx.WorkspaceRoot, "notes");
            FilesThen.Add(Directory.Exists(notes) ? Directory.GetFiles(notes).Select(Path.GetFileName).OfType<string>().Order().ToArray() : []);
            if (hangs) await Task.Delay(Timeout.Infinite, ct);
            return new ToolResult(exitCode == 0, output, exitCode == 0 ? null : $"exit code {exitCode}", [],
                new Dictionary<string, object?> { ["exitCode"] = exitCode });
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
        Assert.Contains(events, e => e.Kind == EventKind.ContextAssembled && e.Summary.Contains("Write-time check 'spell': FAIL", StringComparison.Ordinal));
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
        Assert.Contains("Write-time check 'spell': PASS", ResultOf(worker, "w2")!, StringComparison.Ordinal);
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
        var (fx, checker) = Fixture(new Checker(), Snapshot(Spell(timeout: WriteChecks.TimeoutLimitSeconds + 1)));
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
    /// The regression guard: a run whose checks hold no ENABLED write check - none at all, a step check over the same
    /// files, a write check turned off - shows the worker exactly what it showed before write checks existed.
    /// </summary>
    [Fact]
    public async Task Without_write_checks_the_worker_sees_byte_for_byte_what_it_saw_before()
    {
        async Task<string[]> ResultsWith(CheckSnapshot? checks)
        {
            var (fx, checker) = Fixture(new Checker(1, "bad"), checks);
            using var _ = fx;
            var worker = new FakeChatProvider(Turn.Says(OneStep), Writes(("w1", "notes/a.md"), ("w2", "notes/b.md")),
                Writes(("w3", "notes/a.md")), Turn.Says("Edited."));
            await fx.RunAsync(fx.Build(worker), "edit the notes");
            Assert.Empty(checker.Seen);
            return ToolResults(worker).ToArray();
        }

        var without = await ResultsWith(null);
        // The step check covers other files, so whatever runs step checks leaves this run alone too.
        var withOthers = await ResultsWith(Snapshot(Spell(matches: "elsewhere/**", fire: CheckFire.Step),
            Spell(timeout: 5) with { Id = "off", Enabled = false }));

        Assert.Equal(3, without.Length);
        Assert.Equal(without, withOthers);
    }
}
