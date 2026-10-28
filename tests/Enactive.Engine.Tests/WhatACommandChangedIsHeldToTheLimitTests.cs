namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Bench build-error, 2026-10-18: refused twice on edit_file, the worker rewrote the forbidden Report.cs with
/// <c>powershell ... -replace ... | Set-Content</c>. The request's change limit is asked only of tools that name their
/// paths, so nothing held the command to it, and the run ended with the file changed. A call of a tool that names no
/// paths, in a step under the limit, is now measured around: what it changed of the files the run found is asked about
/// after the fact, and a refused file is put back byte for byte where the snapshot can prove the bytes - named otherwise.
/// </summary>
public sealed class WhatACommandChangedIsHeldToTheLimitTests
{
    private const string Limit = "Leave prices.txt as it is.";
    private const string Request = "Check the order totals against the price list. " + Limit + " Correct the totals in orders.txt.";
    private const string Refuse = """{"refused":["prices.txt"],"reason":"the request says to leave the price list as it is"}""";

    private static readonly ToolCall Command = new("k1", "run_command", """{"command":"echo 2.5> prices.txt"}""");
    private static readonly ToolRegistry Tools = new(EngineFixture.ShippedTools());

    private static ChangeLimitGuard Guard(IChatProvider planner)
        => new(Request, [Limit], planner, new ModelRef("planner", "strong"), new RunBudget(null, DateTimeOffset.UtcNow), 1000);

    private static async Task<StepFrame> FrameAsync(EngineFixture fx, IWorkspaceChanges? changes, bool measured = true)
        => new(Guid.NewGuid(), Guid.NewGuid(), 1, fx.Workspace, Tools.Definitions, [], new ExecutionJournal(), new(),
            fx.Artifacts.BeginStep(), changes: changes, stepStart: measured && changes is not null ? await changes.TakeAsync(default) : null);

    /// <summary>The effect of one "command": <paramref name="command"/> run between the two snapshots.</summary>
    private static async Task<CommandEffect.Outcome> AroundAsync(EngineFixture fx, FakeChatProvider planner, Action command,
        Func<StepFrame, Task>? setUp = null, Action? sinceRunStart = null)
    {
        using var changes = new WorkspaceChanges(fx.Root);
        if (sinceRunStart is not null)
        {
            // The run began here; what the run did before this step comes after.
            fx.Artifacts.MeasuredFrom(new RunStart(changes, await changes.TakeAsync(default)));
            sinceRunStart();
        }
        var frame = await FrameAsync(fx, changes);
        if (setUp is not null) await setUp(frame);
        var effect = new CommandEffect(Guard(planner), frame, "Correct the totals");
        var before = (await effect.BeforeAsync(default))!;
        command();
        return await effect.AfterAsync(Command, before, "I will fix the price instead.", default);
    }

    private static EngineFixture Repository(params (string Path, string Content)[] files)
    {
        var fx = new EngineFixture { PlannerOverride = new Planner() };
        foreach (var (path, content) in files) fx.Write(path, content);
        TestGit.Repository(fx.Root);
        return fx;
    }

    [Fact]
    public async Task Applies_only_with_a_limit_a_measured_step_and_a_tool_that_names_no_paths()
    {
        using var fx = new EngineFixture { PlannerOverride = new Planner() };
        using var changes = new WorkspaceChanges(fx.Root);
        var measured = await FrameAsync(fx, changes);
        var guard = Guard(new FakeChatProvider());
        var command = Tools.DefinitionOf("run_command");

        Assert.True(CommandEffect.Applies(guard, measured, command));
        Assert.False(CommandEffect.Applies(null, measured, command));
        Assert.False(CommandEffect.Applies(guard, await FrameAsync(fx, changes, measured: false), command));   // steps side by side
        Assert.False(CommandEffect.Applies(guard, measured, Tools.DefinitionOf("write_file")));               // names its paths
        Assert.False(CommandEffect.Applies(guard, measured, Tools.DefinitionOf("read_file")));                // changes nothing
    }

    [Fact]
    public async Task A_refused_change_is_put_back_and_said()
    {
        using var fx = Repository(("prices.txt", "apple 3\r\n"));
        var planner = new FakeChatProvider(Turn.Says(Refuse).Reporting(300, 20));

        var outcome = await AroundAsync(fx, planner, () => fx.Write("prices.txt", "apple 2.5"));

        Assert.StartsWith("REFUSED after the fact: this command changed 'prices.txt'", outcome.Appended, StringComparison.Ordinal);
        Assert.Contains("the request says to leave the price list as it is", outcome.Appended, StringComparison.Ordinal);
        Assert.Contains("It was put back as it was before the command.", outcome.Appended, StringComparison.Ordinal);
        var revert = Assert.Single(outcome.Events, e => e.Kind == EventKind.ArtifactReverted);
        Assert.Equal(["prices.txt"], revert.RevertedPaths());
        Assert.Equal("apple 3\r\n", fx.Read("prices.txt"));
        Assert.Contains(outcome.Events, e => e.Kind == EventKind.UsageReported);
        Assert.Contains("as the command left it: git tree ", revert.Summary, StringComparison.Ordinal);   // a person's lost edit can be had back
    }

    /// <summary>
    /// <c>git checkout -- prices.txt</c> after an earlier step changed it: the command puts the file back as the run found
    /// it, which no limit forbids - as restore_file is never asked about. Asked, it was refused and the earlier change
    /// written back over it.
    /// </summary>
    [Fact]
    public async Task A_command_that_puts_a_file_back_as_the_run_found_it_is_not_asked()
    {
        using var fx = Repository(("prices.txt", "apple 3"));
        var planner = new FakeChatProvider(Turn.Says(Refuse));

        var outcome = await AroundAsync(fx, planner, () => fx.Write("prices.txt", "apple 3"),
            sinceRunStart: () => fx.Write("prices.txt", "apple 2"));

        Assert.Empty(planner.Requests);
        Assert.Null(outcome.Appended);
        Assert.Equal("apple 3", fx.Read("prices.txt"));
    }

    /// <summary>One command, the file the request asks for and the one it forbids: only the forbidden one goes back.</summary>
    [Fact]
    public async Task An_allowed_change_stands_beside_a_refused_one()
    {
        using var fx = Repository(("prices.txt", "apple 3"), ("orders.txt", "2 apples: 5"));
        var planner = new FakeChatProvider(Turn.Says(Refuse));

        await AroundAsync(fx, planner, () => { fx.Write("prices.txt", "apple 2.5"); fx.Write("orders.txt", "2 apples: 6"); },
            sinceRunStart: () => { });

        Assert.Equal("apple 3", fx.Read("prices.txt"));
        Assert.Equal("2 apples: 6", fx.Read("orders.txt"));
    }

    /// <summary>A call that could not be measured goes unchecked - and says so, so "not needed" is told from "could not".</summary>
    [Fact]
    public async Task A_call_that_could_not_be_measured_is_said()
    {
        using var fx = Repository(("prices.txt", "apple 3"));
        using var changes = new WorkspaceChanges(fx.Root);
        var effect = new CommandEffect(Guard(new FakeChatProvider()), await FrameAsync(fx, changes), "Correct the totals");

        var outcome = await effect.AfterAsync(Command, before: null, null, default);

        Assert.Contains(outcome.Events, e => e.Kind == EventKind.ErrorObserved
            && e.Summary.Contains("went unmeasured against the request's limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_change_that_cannot_be_put_back_says_why()
    {
        using var fx = Repository(("prices.txt", "apple 3"));
        TestGit.Run(fx.Root, "config", "core.autocrlf", "true");
        var planner = new FakeChatProvider(Turn.Says(Refuse));

        var outcome = await AroundAsync(fx, planner, () => fx.Write("prices.txt", "apple 2.5"));

        Assert.Contains("could not be put back (a line-ending conversion applies to it)", outcome.Appended, StringComparison.Ordinal);
        Assert.Contains("Put it back yourself", outcome.Appended, StringComparison.Ordinal);
        Assert.Equal(["prices.txt"], Assert.Single(outcome.Events, e => e.Kind == EventKind.ArtifactReverted).KeptPaths());
        Assert.Equal("apple 2.5", fx.Read("prices.txt"));
    }

    [Fact]
    public async Task What_the_command_added_or_the_run_made_is_not_asked()
    {
        using var fx = Repository(("orders.txt", "2 apples: 5"));
        var planner = new FakeChatProvider();

        var outcome = await AroundAsync(fx, planner, () =>
            {
                fx.Write("new.txt", "made by the command");
                fx.Write("made.txt", "changed by the command");
                fx.Write(".enactive/scratch/notes.md", "n");
            },
            setUp: frame => frame.Store.CreateAsync("made.txt", ArtifactKind.FileSet, "made",
                s => s.WriteAsync(Encoding.UTF8.GetBytes("made by the run")).AsTask(), default));

        Assert.Empty(planner.Requests);
        Assert.Null(outcome.Appended);
        Assert.Empty(outcome.Events);
    }

    [Fact]
    public async Task A_rename_is_asked_by_the_name_it_left()
    {
        using var fx = Repository(("prices.txt", "apple 3\napple 4\napple 5\n"));
        var planner = new FakeChatProvider(Turn.Says(Refuse));

        var outcome = await AroundAsync(fx, planner, () => File.Move(Path.Combine(fx.Root, "prices.txt"), Path.Combine(fx.Root, "old-prices.txt")));

        Assert.Contains("It moved prices.txt away", planner.Requests[0].Messages[1].Content, StringComparison.Ordinal);
        Assert.Equal("apple 3\napple 4\napple 5\n", fx.Read("prices.txt"));
        Assert.True(fx.Exists("old-prices.txt"));
        var revert = Assert.Single(outcome.Events, e => e.Kind == EventKind.ArtifactReverted);
        Assert.Equal(["prices.txt"], revert.RevertedPaths());
        Assert.Equal(["old-prices.txt"], revert.KeptPaths());
        Assert.Contains("'old-prices.txt'", outcome.Appended, StringComparison.Ordinal);
    }

    // ── a run ─────────────────────────────────────────────────────────────

    private static string Contract(bool limited) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["sources"] = new[] { new { id = "O001", assessment = "the whole request" } },
        ["checks"] = Array.Empty<object>(), ["forbidden_effects"] = Array.Empty<object>(),
        ["change_limits"] = limited ? new[] { new { source_quote = Limit } } : Array.Empty<object>(),
        ["action_policy"] = null, ["unresolved"] = null
    });

    /// <summary>The planner: a quick action, the contract, and the guard's question answered by which file it is about.</summary>
    private static FakeChatProvider Planner(bool limited = true) => new(
        Turn.Says("""{"disposition":"quick_action","title":"orders"}"""),
        Turn.Says(Contract(limited)))
    {
        Answering = r => r.Messages.Any(m => m.Content?.Contains("A command a step of a plan ran has changed files", StringComparison.Ordinal) == true)
            ? Turn.Says(r.Messages.Any(m => m.Content?.Contains("It changed prices.txt", StringComparison.Ordinal) == true)
                ? Refuse : """{"refused":[],"reason":""}""")
            : null
    };

    private static async Task<IReadOnlyList<WorkEvent>> RunAsync(EngineFixture fx, FakeChatProvider planner, string command)
    {
        var worker = new FakeChatProvider(
            Turn.Calls1("run_command", System.Text.Json.JsonSerializer.Serialize(new { command }), "c1"),
            Turn.Says("Done."));
        return await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn()), Request);
    }

    private static string? Result(IEnumerable<WorkEvent> events)
        => events.FirstOrDefault(e => e.Kind == EventKind.ToolResult && e.Summary.StartsWith("run_command ->", StringComparison.Ordinal))?.Summary;

    /// <summary>THE ONE THAT MATTERS: the bench's shape - a command rewrites the file the request says to leave.</summary>
    [Fact]
    public async Task A_command_that_rewrites_a_forbidden_file_is_refused_and_the_file_put_back()
    {
        using var fx = Repository(("prices.txt", "apple 3\r\n"), ("orders.txt", "2 apples: 5"));

        var events = await RunAsync(fx, Planner(), "echo apple 2.5> prices.txt");

        Assert.Equal("apple 3\r\n", fx.Read("prices.txt"));
        Assert.Contains("REFUSED after the fact: this command changed 'prices.txt'", Result(events), StringComparison.Ordinal);
        Assert.Contains(events, e => e.Kind == EventKind.ArtifactReverted && e.RevertedPaths()?.Contains("prices.txt") == true);
    }

    [Fact]
    public async Task A_command_s_change_the_request_allows_stays()
    {
        using var fx = Repository(("prices.txt", "apple 3"), ("orders.txt", "2 apples: 5"));

        var events = await RunAsync(fx, Planner(), "echo 2 apples: 6 > orders.txt");

        Assert.Contains("2 apples: 6", fx.Read("orders.txt"), StringComparison.Ordinal);
        Assert.DoesNotContain("REFUSED", Result(events), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outside_git_the_file_is_named_and_the_step_told_why()
    {
        using var fx = new EngineFixture { PlannerOverride = new Planner() };
        fx.Write("prices.txt", "apple 3");
        fx.Write("orders.txt", "2 apples: 5");

        var events = await RunAsync(fx, Planner(), "echo apple 2.5> prices.txt");

        Assert.Contains("2.5", fx.Read("prices.txt"), StringComparison.Ordinal);
        Assert.Contains("could not be put back (not a git workspace)", Result(events), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_that_fails_after_writing_is_still_held()
    {
        using var fx = Repository(("prices.txt", "apple 3"), ("orders.txt", "2 apples: 5"));

        var events = await RunAsync(fx, Planner(), "echo apple 2.5> prices.txt & exit 3");

        Assert.Equal("apple 3", fx.Read("prices.txt"));
        Assert.StartsWith("run_command -> failed:", Result(events), StringComparison.Ordinal);
        Assert.Contains("REFUSED after the fact", Result(events), StringComparison.Ordinal);
    }

    /// <summary>A command the engine reads as a test run: the step is told the tests' summary - and the refusal with it.</summary>
    private sealed class Tested : Enactive.Core.Builds.IEcosystem
    {
        public string Name => "tested";
        public Enactive.Core.Builds.EcosystemTargets? Detect(string workspaceRoot) => null;
        public bool Owns(string relativePath) => false;
        public string BuildCommand(string target) => "echo built";
        public string TestCommand(string target) => "echo PASS t1";
        public bool RunsTests(string command) => command.Contains("PASS", StringComparison.Ordinal);
        public string? DescribeTests(string output) => output.Contains("PASS", StringComparison.Ordinal) ? "1 test passed." : null;
        public IReadOnlyList<Enactive.Core.Builds.BuildDiagnostic> ParseDiagnostics(string output, string workspaceRoot) => [];
        public Enactive.Core.Builds.TestRunReport? ParseTests(string output) => null;
    }

    [Fact]
    public async Task A_refusal_reaches_the_step_when_the_command_runs_tests()
    {
        using var fx = Repository(("prices.txt", "apple 3"), ("orders.txt", "2 apples: 5"));
        fx.EcosystemsOverride = [new Tested()];
        var worker = new FakeChatProvider(
            // Long enough to be cut: only then is a test run told by its summary (TestRunDescribed).
            Turn.Calls1("run_command", """{"command":"echo apple 2.5> prices.txt & for /L %i in (1,1,6000) do @echo PASS t%i"}""", "c1"),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, Planner())),
            router: Routers.WithPlannerOn()), Request);

        var told = worker.Requests[1].Messages.Last(m => m.Role == ChatRole.Tool).Content;
        Assert.Contains("1 test passed.", told, StringComparison.Ordinal);
        Assert.Contains("REFUSED after the fact", told, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_without_a_limit_takes_no_snapshots()
    {
        using var fx = Repository(("prices.txt", "apple 3"), ("orders.txt", "2 apples: 5"));
        var planner = Planner(limited: false);

        await RunAsync(fx, planner, "echo apple 2.5> prices.txt");

        Assert.Equal(2, planner.Requests.Count);
        Assert.Contains("2.5", fx.Read("prices.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Many_files_are_named_up_to_ten_and_counted()
    {
        var files = Enumerable.Range(1, 12).Select(i => ($"f{i:00}.txt", "as it was")).ToArray();
        using var fx = Repository(files);
        var planner = new FakeChatProvider(Turn.Says(System.Text.Json.JsonSerializer.Serialize(
            new { refused = files.Select(f => f.Item1).ToArray(), reason = "leave them as they are" })));

        var outcome = await AroundAsync(fx, planner, () => { foreach (var (path, _) in files) fx.Write(path, "formatted"); });

        Assert.Contains("and 2 more", outcome.Appended, StringComparison.Ordinal);
        Assert.DoesNotContain("f12.txt", outcome.Appended, StringComparison.Ordinal);
        Assert.Equal(12, Assert.Single(outcome.Events, e => e.Kind == EventKind.ArtifactReverted).RevertedPaths()!.Count);
        Assert.All(files, f => Assert.Equal("as it was", fx.Read(f.Item1)));
    }
}
