namespace Enactive.Engine.Tests;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Execution;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Run 2eedd5, 2026-10-20, step 1: the worker read five test files in one <c>read_files</c> call. The reviewer was shown the
/// call as its arguments cut at 120 characters - two and a half of the five paths - and its result cut to its start and its
/// end. It failed the step because GameEngineTests.cs and GameEngineExtraTests.cs "were never read in any shown call";
/// they were. A read is now shown by every file it read, and a result cut to fit says per file which of its lines are shown.
/// </summary>
public sealed class TheReviewerSeesWhatWasReadTests
{
    private static readonly string[] RunPaths =
    [
        "TicTacToe.Tests/ComputerAIExtraTests.cs", "TicTacToe.Tests/ComputerAITests.cs", "TicTacToe.Tests/ComputerAI_PriorityTests.cs",
        "TicTacToe.Tests/GameEngineExtraTests.cs", "TicTacToe.Tests/GameEngineTests.cs"
    ];

    /// <summary>A file of <paramref name="lines"/> lines, each one saying which file and line it is - and nothing after the
    /// last break, so every line has something on it to look for.</summary>
    private static string Numbered(int file, int lines)
        => string.Concat(Enumerable.Range(1, lines).Select(n => $"{LineText(file, n)}\n"));

    /// <summary>'&lt;' only at a line's start, so part of a line is never taken for the whole of one.</summary>
    private static string LineText(int file, int n)
        => $"<F{file}L{n:D4}>" + new string('x', n * 7 % 41);

    private static ToolContext Context(EngineFixture fx)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: fx.Artifacts);

    private static string Compacted(string json)
        => json.Replace('\n', ' ') is var flat && flat.Length > 120 ? flat[..120] + "…" : json;

    /// <summary>Records a real tool's result as the step's accounting does, with what it says it read.</summary>
    private static void Record(ExecutionJournal journal, string tool, string arguments, ToolResult result)
        => journal.Record(1, tool, Compacted(arguments), ActionOutcome.Succeeded, result.Output, WorkspaceEffect.None,
            filesRead: (IReadOnlyList<FileRead>)result.Metadata[FileRead.MetadataKey]!);

    // ── the defect, end to end ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_read_of_five_files_reaches_the_reviewer_naming_all_five_and_what_of_each_it_is_shown()
    {
        using var fx = new EngineFixture();
        for (var i = 0; i < RunPaths.Length; i++) fx.Write(RunPaths[i], Numbered(i, 90));
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"find the least-covered behaviour"}"""),
            Turn.Calls1("read_files", JsonSerializer.Serialize(new { paths = RunPaths }), "r1"),
            Turn.Says("Read all five test files; GameEngineTests.cs covers the least."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "identify the least-covered behaviour in the tests");

        var sent = string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content)).Replace("\r\n", "\n");
        Assert.Contains("-> read_files read 5 files: " + string.Join(", ", RunPaths), sent, StringComparison.Ordinal);
        // Five files of about 2,300 characters are more than the call's share of the evidence: it is cut, and says per file
        // what it shows - the two the run's reviewer called unread included, as read.
        Assert.Contains("characters not shown here", sent, StringComparison.Ordinal);
        Assert.Contains("Shown here of the files this call read:", sent, StringComparison.Ordinal);
        foreach (var path in RunPaths)
            Assert.Matches(new Regex($@"\n  {Regex.Escape(path)}: (no whole line|lines? [\d,]+.*) of 91\n?"), sent);
        // The middle one: neither the start of the result nor its end reaches it - read, and said to be.
        Assert.Contains("  TicTacToe.Tests/ComputerAI_PriorityTests.cs: no whole line of 91", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"paths\"", sent, StringComparison.Ordinal);
    }

    // ── what is said is what is shown ──────────────────────────────────────────────────

    /// <summary>The lines a note claims for <paramref name="path"/>, from the evidence text.</summary>
    private static HashSet<int> Claimed(string text, string path)
    {
        var line = Regex.Match(text, $@"\n  {Regex.Escape(path)}: (?:no whole line|lines? (?<r>[\d,\-]+(?: and [\d,\-]+)*)) of ");
        Assert.True(line.Success, $"no note for {path} in:\n{text}");
        var claimed = new HashSet<int>();
        if (!line.Groups["r"].Success) return claimed;
        foreach (var range in line.Groups["r"].Value.Split(" and "))
        {
            var ends = range.Split('-').Select(n => int.Parse(n, NumberStyles.AllowThousands, CultureInfo.InvariantCulture)).ToArray();
            for (var n = ends[0]; n <= ends[^1]; n++) claimed.Add(n);
        }
        return claimed;
    }

    /// <summary>The lines of a file whose whole text the evidence shows, as a line of its own - or as the first of a result.</summary>
    private static HashSet<int> Visible(string text, int file, IEnumerable<int> lines)
        => lines.Where(n => Regex.IsMatch(text, $@"(\n|\n<- ){Regex.Escape(LineText(file, n))}\r?\n")).ToHashSet();

    /// <summary>
    /// Exact, not approximate: through the tool's own start-and-end preview of a long file and the evidence's cut of the
    /// whole result, the lines a note claims are the lines the reviewer can read whole - none it cannot, none left out.
    /// </summary>
    [Theory]
    [InlineData(1_500)]
    [InlineData(2_500)]
    [InlineData(4_000)]
    [InlineData(6_000)]
    [InlineData(9_000)]
    public async Task A_read_of_several_files_cut_to_fit_says_exactly_which_lines_of_each_are_shown(int budget)
    {
        using var fx = new EngineFixture();
        int[] sizes = [30, 400, 10, 300, 50];
        var paths = sizes.Select((_, f) => $"src/Module{f}/Deeply/Nested/Folder/File{f}.cs").ToArray();
        for (var f = 0; f < sizes.Length; f++) fx.Write(paths[f], Numbered(f, sizes[f]));
        var arguments = JsonSerializer.Serialize(new { paths });
        var result = await new ReadFilesTool().InvokeAsync(arguments, Context(fx), CancellationToken.None);
        Assert.True(result.Success, result.Error);
        var journal = new ExecutionJournal();
        Record(journal, "read_files", arguments, result);

        var text = journal.Describe(maxChars: budget).Text.Replace("\r\n", "\n");

        Assert.True(text.Length <= budget, $"{text.Length} characters against {budget}");
        Assert.Contains("-> read_files read 5 files: " + string.Join(", ", paths), text, StringComparison.Ordinal);
        for (var f = 0; f < sizes.Length; f++)
        {
            var content = Enumerable.Range(1, sizes[f]).ToArray();
            Assert.Equal(Visible(text, f, content).Order(), Claimed(text, paths[f]).Intersect(content).Order());
            Assert.Contains($"{paths[f]}: ", text, StringComparison.Ordinal);
        }    }

    [Theory]
    [InlineData(1, 400, 1_500)]
    [InlineData(1, 400, 4_000)]
    [InlineData(250, 60, 1_500)]
    [InlineData(550, 400, 1_500)]
    public async Task A_window_of_one_file_cut_to_fit_says_exactly_which_of_its_lines_are_shown(int offset, int limit, int budget)
    {
        using var fx = new EngineFixture();
        // Long enough lines that 400 of them pass read_file's own 8,000 characters and it cuts the window too.
        const string path = "docs/a/very/long/path/to/a/file/that/the/arguments/alone/would/never/have/shown/whole/when/cut/at/120/characters/notes.md";
        fx.Write(path, Numbered(7, 600));
        var arguments = JsonSerializer.Serialize(new { path, offset, limit });
        var result = await new ReadFileTool().InvokeAsync(arguments, Context(fx), CancellationToken.None);
        Assert.True(result.Success, result.Error);
        var journal = new ExecutionJournal();
        Record(journal, "read_file", arguments, result);

        var text = journal.Describe(maxChars: budget).Text.Replace("\r\n", "\n");

        Assert.True(text.Length <= budget, $"{text.Length} characters against {budget}");
        Assert.Contains("[1] -> read_file read " + path + "\n", text, StringComparison.Ordinal);
        var content = Enumerable.Range(1, 600).ToArray();
        var claimed = Claimed(text, path);
        Assert.Equal(Visible(text, 7, content).Order(), claimed.Intersect(content).Order());
        Assert.NotEmpty(claimed);
        Assert.All(claimed, n => Assert.InRange(n, offset, offset + limit - 1));
    }

    [Fact]
    public async Task A_single_read_with_a_long_path_keeps_the_path_whole_and_a_short_result_has_no_note()
    {
        using var fx = new EngineFixture();
        var path = string.Join("/", Enumerable.Range(1, 12).Select(i => $"folder{i}")) + "/a_file_with_a_long_name.cs";
        fx.Write(path, "class A { }\n");
        var arguments = JsonSerializer.Serialize(new { path });
        var result = await new ReadFileTool().InvokeAsync(arguments, Context(fx), CancellationToken.None);
        var journal = new ExecutionJournal();
        Record(journal, "read_file", arguments, result);

        var text = journal.Describe().Text.Replace("\r\n", "\n");

        Assert.True(path.Length > 120);
        Assert.Contains("[1] -> read_file read " + path + "\n<- class A { }", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Shown here of the files", text, StringComparison.Ordinal);
    }

    // ── the budget ─────────────────────────────────────────────────────────────────────

    /// <summary>The names and the notes are paid for: a step of many cut reads stays within its budget.</summary>
    [Theory]
    [InlineData(1_500)]
    [InlineData(3_000)]
    [InlineData(6_000)]
    public async Task Many_reads_name_their_files_say_what_they_show_and_keep_to_the_budget(int budget)
    {
        using var fx = new EngineFixture();
        var journal = new ExecutionJournal();
        for (var call = 0; call < 8; call++)
        {
            var paths = Enumerable.Range(0, 5).Select(f => $"src/Area{call}/Component{f}/Implementation{f}.cs").ToArray();
            for (var f = 0; f < paths.Length; f++) fx.Write(paths[f], Numbered(f, 60));
            var arguments = JsonSerializer.Serialize(new { paths });
            Record(journal, "read_files", arguments, await new ReadFilesTool().InvokeAsync(arguments, Context(fx), CancellationToken.None));
        }
        journal.Record(1, "run_command", """{"command":"dotnet test"}""", ActionOutcome.Succeeded, "Passed!  - Failed: 0, Passed: 52", exitCode: 0);

        var view = journal.Describe(maxChars: budget);

        Assert.True(view.Text.Length <= budget, $"{view.Text.Length} characters against {budget}");
        Assert.Contains("Passed: 52", view.Text, StringComparison.Ordinal);
        Assert.Equal(view.VisibleActionIds.Count - 1, Regex.Matches(view.Text, @"-> read_files read 5 files: ").Count);
    }

    [Fact]
    public void Past_a_generous_bound_the_files_are_counted_not_named()
    {
        var files = Enumerable.Range(1, 12)
            .Select(i => new FileRead($"src/{new string('d', 100)}/File{i}.cs", 10, true, []))
            .ToArray();

        var summary = FileRead.Summary(files);

        Assert.StartsWith("12 files: src/", summary, StringComparison.Ordinal);
        Assert.Matches(@", and \d+ more not named here$", summary);
        Assert.True(summary.Length <= FileRead.MaxPathsChars + 40, $"{summary.Length} characters");
        Assert.Contains("File1.cs", summary, StringComparison.Ordinal);
    }

    // ── what is not a read keeps its presentation ──────────────────────────────────────

    [Theory]
    [InlineData(ActionOutcome.Answered)]
    [InlineData(ActionOutcome.Failed)]
    public void A_read_that_did_not_go_through_keeps_the_presentation_it_had(ActionOutcome outcome)
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "read_file", """{"path":"a.cs"}""", outcome, "File not found: a.cs.", WorkspaceEffect.None,
            filesRead: [new FileRead("a.cs", 1, true, [new FileRead.Span(0, 0, 1, 1, true, true)])]);

        var text = journal.Describe().Text;

        Assert.Contains("[1] -> read_file {\"path\":\"a.cs\"}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("read_file read a.cs", text, StringComparison.Ordinal);
        Assert.DoesNotContain("shown by the files it read", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_is_shown_as_it_was()
    {
        var command = Compacted(JsonSerializer.Serialize(new { command = "dotnet test --filter " + new string('x', 200) }));
        var journal = new ExecutionJournal();
        journal.Record(1, "run_command", command, ActionOutcome.Succeeded, "Passed!", exitCode: 0);

        Assert.Contains("[1] [process exit=0] -> run_command " + command + "\n<- Passed!",
            journal.Describe().Text.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    // ── it is carried wherever the call is ─────────────────────────────────────────────

    [Fact]
    public void The_run_wide_evidence_keeps_what_each_read_read()
    {
        using var fx = new EngineFixture();
        var session = new RunSession(new RunScope(Guid.NewGuid(), Guid.NewGuid(),
            new RunBudget(ExecutionLimits.None, DateTimeOffset.UtcNow), []), []);
        session.ConfigurePlan(false, null);
        var step = session.BeginStep([], fx.Artifacts.BeginStep());
        var read = new FileRead("a.txt", 2, true, [new FileRead.Span(0, 2, 1, 2, true, true)]);
        step.Journal.Record(1, "read_file", """{"path":"a.txt"}""", ActionOutcome.Succeeded, "x\n", filesRead: [read]);

        Assert.Equal(read, Assert.Single(Assert.Single(session.RunEvidence().Actions).FilesRead!));
    }

    [Fact]
    public void A_parked_step_brings_back_what_its_reads_read()
    {
        using var fx = new EngineFixture();
        var progress = new TaskProgress(fx.Root);
        var task = Guid.NewGuid();
        var read = new FileRead("TicTacToe.Tests/GameEngineTests.cs", 264, true,
            [new FileRead.Span(40, 900, 1, 30, true, false), new FileRead.Span(1000, 500, 250, 264, false, true)]);
        var action = new ExecutedAction(DateTimeOffset.UtcNow, 1, "read_files", "{}", ActionOutcome.Succeeded, "…",
            WorkspaceEffect.None, FilesRead: [read]);
        progress.Park(task, new ParkedPosition(1, [], [action], []));

        var back = Assert.Single(Assert.Single(progress.TakeParked(task, 1)!.Actions).FilesRead!);

        Assert.Equal(read.Path, back.Path);
        Assert.Equal(read.TotalLines, back.TotalLines);
        Assert.Equal(read.Spans, back.Spans);
    }

    /// <summary>
    /// A step parked at a question is carried on from its calls as they were: the read made before it stopped is shown to
    /// the reviewer by its files, as it would have been had nobody been asked.
    /// </summary>
    [Fact]
    public async Task A_step_carried_on_after_a_question_shows_its_earlier_read_by_its_files()
    {
        using var fx = new EngineFixture();
        string[] paths = ["docs/archive/2026/october/meeting-notes-first-week.md", "docs/archive/2026/october/meeting-notes-second-week.md",
            "docs/archive/2026/october/draft.md"];
        foreach (var path in paths) fx.Write(path, "notes\n");
        var taskId = Guid.NewGuid();
        var ledger = new DecisionLedger(fx.Root);
        var worker = EngineFixture.WorkerWith("read_files", "delete_file");
        Turn[] turns =
        [
            Turn.Says("""{"disposition":"quick_action","title":"tidy up"}"""),
            Turn.Calls1("read_files", JsonSerializer.Serialize(new { paths }), "r1"),
            Turn.Calls1("delete_file", """{"path":"docs/archive/2026/october/draft.md"}""", "d1"),
            Turn.Says("Read the three notes and removed the draft.")
        ];

        await Submit(fx, fx.Build(new FakeChatProvider(turns), worker, decisions: new ParkingDecisionHandler()), taskId);
        var question = Assert.Single(ledger.Pending());
        Assert.True(ledger.Answer(taskId, question.RequestId, "allow"));
        var reviewer = new FakeChatProvider(Verdicts.Pass());
        await Submit(fx, fx.Build(new FakeChatProvider(turns[0], turns[2], turns[3]), worker, router: Routers.WithReviewer(),
            reviewProvider: reviewer, decisions: new ParkingDecisionHandler()), taskId);

        var sent = string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content));
        Assert.Contains("-> read_files read 3 files: " + string.Join(", ", paths), sent, StringComparison.Ordinal);
    }

    private static async Task Submit(EngineFixture fx, Orchestrator engine, Guid taskId)
    {
        var context = new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var _ in engine.SubmitIntentAsync(
                           new Enactive.Core.Intents.Intent(taskId, "tidy up the notes", Enactive.Core.Intents.IntentSource.CommandBar, context,
                               DateTimeOffset.UtcNow), cts.Token)) { }
    }
}
