namespace Enactive.Engine.Tests;

using System.Text.Json;
using System.Text.RegularExpressions;
using Enactive.Core.Artifacts;
using Enactive.Core.Execution;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Run bbf67b, 2026-10-19, step 2: the worker added two tests the usual way - <c>edit_file</c> with <c>old_string</c> an
/// existing test (the anchor) and <c>new_string</c> that test with the new one after it. The suite went from 43 passing
/// to 45 and the report named both new tests. The reviewer was shown each call as the first 120 characters of its
/// arguments - the start of the ANCHOR - concluded the edits "replace code with different tests" than the report said,
/// and failed the step; the right work was put back and the run ended Failed. An edit is now shown as what it changed.
/// </summary>
public sealed class TheReviewerSeesWhatAnEditChangedTests
{
    private const string Anchor =
        "    [Fact]\n    public void GetBestMove_ReturnsLastAvailableCell()\n    {\n        // Board almost full\n        Assert.Equal(8, Ai.Best(Board.AlmostFull()));\n    }\n";

    private const string Added =
        "\n    [Fact]\n    public void GetBestMove_WinTakesPriorityOverBlock()\n    {\n        Assert.Equal(2, Ai.Best(Board.WinOrBlock()));\n    }\n";

    private const string TestFile =
        "namespace TicTacToe.Tests;\n\npublic class ComputerAITests\n{\n" + Anchor + "}\n";

    private static IEnumerable<string> DiffLines(string text)
        => text.Split('\n').Where(l => l.StartsWith("+ ", StringComparison.Ordinal) || l.StartsWith("- ", StringComparison.Ordinal));

    // ── the defect, end to end ─────────────────────────────────────────────────────────

    [Fact]
    public async Task An_insertion_after_an_anchor_reaches_the_reviewer_as_the_lines_it_added()
    {
        using var fx = new EngineFixture();
        fx.Write("TicTacToe.Tests/ComputerAITests.cs", TestFile);
        // new_string first, as the model in the run sent it: compacted, the arguments are the anchor's first lines.
        var edit = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["new_string"] = Anchor + Added, ["old_string"] = Anchor, ["path"] = "TicTacToe.Tests/ComputerAITests.cs"
        });
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"add a test"}"""),
            Turn.Calls1("edit_file", edit, "e1"),
            Turn.Says("Added GetBestMove_WinTakesPriorityOverBlock after GetBestMove_ReturnsLastAvailableCell."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "add a test that a win takes priority over a block");

        Assert.Contains(GetBestMoveWin(), fx.Read("TicTacToe.Tests/ComputerAITests.cs"), StringComparison.Ordinal);
        var sent = string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content));
        Assert.Contains("-> edit_file changed TicTacToe.Tests/ComputerAITests.cs: 6 line(s) added, 0 removed", sent, StringComparison.Ordinal);
        Assert.Contains("+     public void GetBestMove_WinTakesPriorityOverBlock()", sent, StringComparison.Ordinal);
        // The anchor is not presented as the change: neither as its arguments nor as an added or removed line.
        Assert.DoesNotContain("\"new_string\"", sent, StringComparison.Ordinal);
        Assert.DoesNotContain(DiffLines(sent), l => l.Contains("GetBestMove_ReturnsLastAvailableCell", StringComparison.Ordinal));
        Assert.DoesNotContain(DiffLines(sent), l => l.Contains("namespace", StringComparison.Ordinal));
    }

    private static string GetBestMoveWin() => "GetBestMove_WinTakesPriorityOverBlock";

    // ── the diff ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_insertion_after_an_anchor_is_its_added_lines_with_a_line_of_context()
    {
        var change = WrittenChange.Of(TestFile, TestFile.Replace(Anchor, Anchor + Added, StringComparison.Ordinal), "t.cs")!;

        Assert.Equal(6, change.Added);
        Assert.Equal(0, change.Removed);
        Assert.Equal(
            ["@@ -10,2 +10,8 @@", "      }", "+ ", "+     [Fact]", "+     public void GetBestMove_WinTakesPriorityOverBlock()", "+     {",
             "+         Assert.Equal(2, Ai.Best(Board.WinOrBlock()));", "+     }", "  }"],
            change.Lines);
    }

    [Fact]
    public void A_replacement_shows_what_it_removed_and_what_it_added()
    {
        var before = "a\nb\nc\nold value\nd\ne\n";
        var change = WrittenChange.Of(before, before.Replace("old value", "new value\nand one more", StringComparison.Ordinal), "notes.md")!;

        Assert.Equal("notes.md: 2 line(s) added, 1 removed", change.Summary);
        Assert.Equal(["@@ -3,3 +3,4 @@", "  c", "- old value", "+ new value", "+ and one more", "  d"], change.Lines);
    }

    [Fact]
    public void Changes_far_apart_are_two_hunks_and_the_lines_between_them_are_not_shown()
    {
        var lines = Enumerable.Range(1, 30).Select(i => $"line {i}").ToArray();
        var before = string.Join("\n", lines);
        var after = before.Replace("line 3\n", "line three\n", StringComparison.Ordinal).Replace("line 27\n", "line twenty-seven\n", StringComparison.Ordinal);

        var change = WrittenChange.Of(before, after, "f.txt")!;

        Assert.Equal(2, change.Lines.Count(l => l.StartsWith("@@", StringComparison.Ordinal)));
        Assert.Contains("@@ -26,3 +26,3 @@", change.Lines);
        Assert.DoesNotContain("  line 15", change.Lines);
    }

    [Fact]
    public void No_file_before_or_nothing_changed_is_no_change()
    {
        Assert.Null(WrittenChange.Of(null, "new\n", "n.md"));
        Assert.Null(WrittenChange.Of("same\n", "same\n", "s.md"));
    }

    [Fact]
    public void A_long_change_is_kept_to_its_bound_and_says_how_much_is_not_kept()
    {
        var after = string.Join("\n", Enumerable.Range(1, 400).Select(i => $"added line number {i} with some words on it"));
        var change = WrittenChange.Of("first\n", "first\n" + after + "\n", "big.md")!;

        Assert.Equal(400, change.Added);
        Assert.True(string.Join("\n", change.Lines).Length <= WrittenChange.MaxChars);
        Assert.True(change.LinesNotKept > 0);
        Assert.Contains("line(s) of this diff not shown here", change.Body(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_line_of_a_diff_is_clipped_and_says_how_long_it_is()
    {
        var minified = "var x=" + new string('1', 5_000) + ";";
        var change = WrittenChange.Of("a\nvar x=0;\nb\n", "a\n" + minified + "\nb\n", "app.min.js")!;

        var added = Assert.Single(change.Lines, l => l.StartsWith("+ ", StringComparison.Ordinal));
        Assert.True(added.Length < 300, $"{added.Length} characters");
        Assert.EndsWith("… (5,007 characters in the line)", added, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shortened_diff_gives_up_the_unchanged_lines_first_and_stays_in_its_room()
    {
        var change = WrittenChange.Of(TestFile, TestFile.Replace(Anchor, Anchor + Added, StringComparison.Ordinal), "t.cs")!;

        var body = change.Body(150);

        Assert.True(body.Length <= 150, $"{body.Length} characters");
        Assert.Contains("+     public void GetBestMove_WinTakesPriorityOverBlock()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("      }", body.Split('\n'));
        Assert.Contains("not shown here", body, StringComparison.Ordinal);
    }

    // ── the evidence ───────────────────────────────────────────────────────────────────

    private static WrittenChange Insertion(string name)
        => WrittenChange.Of(TestFile, TestFile.Replace(Anchor, Anchor + Added.Replace("GetBestMove_WinTakesPriorityOverBlock", name,
            StringComparison.Ordinal), StringComparison.Ordinal), "TicTacToe.Tests/ComputerAITests.cs")!;

    private static string Compacted(string json)
        => json.Replace('\n', ' ') is var flat && flat.Length > 120 ? flat[..120] + "…" : json;

    private static string AnchoredArguments()
        => Compacted(JsonSerializer.Serialize(new { new_string = Anchor + Added, old_string = Anchor }));

    [Fact]
    public void A_step_of_many_calls_still_shows_each_edit_by_what_it_added_and_keeps_to_its_budget()
    {
        var journal = new ExecutionJournal();
        for (var i = 0; i < 16; i++)
            journal.Record(1, "read_file", $$"""{"path":"src/File{{i}}.cs"}""", ActionOutcome.Succeeded, new string('r', 900), WorkspaceEffect.None);
        journal.Record(1, "edit_file", AnchoredArguments(), ActionOutcome.Succeeded,
            "Replaced 1 passage in 'TicTacToe.Tests/ComputerAITests.cs' at line 5 (+180 bytes). The rest of the file is unchanged.",
            WorkspaceEffect.Changed, change: Insertion("GetBestMove_WinTakesPriorityOverBlock"));
        journal.Record(1, "edit_file", AnchoredArguments(), ActionOutcome.Succeeded,
            "Replaced 1 passage in 'TicTacToe.Tests/ComputerAITests.cs' at line 5 (+180 bytes). The rest of the file is unchanged.",
            WorkspaceEffect.Changed, change: Insertion("MakeMove_Turn_StatusMessage_IsExact"));
        journal.Record(1, "run_command", """{"command":"dotnet test"}""", ActionOutcome.Succeeded, "Passed!  - Failed: 0, Passed: 45", exitCode: 0);

        var text = journal.Describe(maxChars: ExecutionJournal.DefaultBudget).Text;

        Assert.True(text.Length <= ExecutionJournal.DefaultBudget, $"{text.Length} characters");
        Assert.Contains("+     public void GetBestMove_WinTakesPriorityOverBlock()", text, StringComparison.Ordinal);
        Assert.Contains("+     public void MakeMove_Turn_StatusMessage_IsExact()", text, StringComparison.Ordinal);
        Assert.Contains("A call that changed a file is shown by WHAT IT CHANGED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"new_string\"", text, StringComparison.Ordinal);
        Assert.Contains("Passed: 45", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1_500)]
    [InlineData(3_000)]
    [InlineData(6_000)]
    public void Long_edits_are_bounded_say_what_they_leave_out_and_the_whole_stays_within_the_budget(int budget)
    {
        var big = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"    public void Generated_{i}() {{ }}"));
        var journal = new ExecutionJournal();
        for (var i = 0; i < 3; i++)
            journal.Record(1, "edit_file", AnchoredArguments(), ActionOutcome.Succeeded, "Replaced 1 passage.", WorkspaceEffect.Changed,
                change: WrittenChange.Of("class C\n{\n}\n", "class C\n{\n" + big + "\n}\n", $"C{i}.cs"));

        var view = journal.Describe(maxChars: budget);
        var text = view.Text;

        Assert.True(text.Length <= budget, $"{text.Length} characters against {budget}");
        // Each call shown is shown by its change; one the budget cannot hold is said not to be shown, as any call is.
        Assert.Equal(view.VisibleActionIds.Count, Regex.Matches(text, @"-> edit_file changed C\d\.cs: 300 line\(s\) added, 0 removed").Count);
        if (budget >= 3_000) Assert.Equal(3, view.VisibleActionIds.Count);
        else Assert.Contains("are not shown here", text, StringComparison.Ordinal);
        Assert.Contains("+     public void Generated_1() { }", text, StringComparison.Ordinal);
        Assert.Contains("line(s) of this diff not shown here", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cut as one text, an edit kept its tool's line and the END of its diff, and the first lines it added - the new
    /// test's name - went in the middle, with "characters not shown". Shortened as a diff it keeps whole lines from the
    /// start of the change and says how many lines it leaves out.
    /// </summary>
    [Fact]
    public void An_edit_cut_to_fit_keeps_its_first_changed_lines_and_counts_the_lines_it_leaves_out()
    {
        var journal = new ExecutionJournal();
        for (var i = 0; i < 12; i++)
            journal.Record(1, "read_file", $$"""{"path":"src/File{{i}}.cs"}""", ActionOutcome.Succeeded, new string((char)('a' + i), 900), WorkspaceEffect.None);
        var body = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"        Assert.Equal({i}, Compute({i}));"));
        journal.Record(1, "edit_file", AnchoredArguments(), ActionOutcome.Succeeded,
            "Replaced 1 passage in 'T.cs' at line 4 (+2400 bytes). The rest of the file is unchanged.", WorkspaceEffect.Changed,
            change: WrittenChange.Of("class T\n{\n}\n", "class T\n{\n    [Fact]\n    public void Computes_every_value()\n    {\n" + body + "\n    }\n}\n", "T.cs"));

        var text = journal.Describe(maxChars: 3_000).Text;
        var edit = text[text.IndexOf("-> edit_file", StringComparison.Ordinal)..];

        Assert.Contains("+     public void Computes_every_value()", edit, StringComparison.Ordinal);
        Assert.Matches(@"… \d+ line\(s\) of this diff not shown here", edit);
        Assert.DoesNotContain("characters not shown here", edit, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ActionOutcome.Failed)]
    [InlineData(ActionOutcome.Refused)]
    public void An_edit_that_did_not_go_through_keeps_the_presentation_it_had(ActionOutcome outcome)
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "edit_file", AnchoredArguments(), outcome, "'old_string' does not appear in t.cs", WorkspaceEffect.None,
            change: Insertion("GetBestMove_WinTakesPriorityOverBlock"));

        var text = journal.Describe().Text;

        Assert.Contains("[1] -> edit_file " + AnchoredArguments(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("+     public void", text, StringComparison.Ordinal);
        Assert.DoesNotContain("changed TicTacToe", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WHAT IT CHANGED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_that_is_not_an_edit_is_shown_as_it_was()
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "read_file", """{"path":"a.cs"}""", ActionOutcome.Succeeded, "class A {}", WorkspaceEffect.None);

        Assert.Contains("[1] -> read_file {\"path\":\"a.cs\"}\n<- class A {}", journal.Describe().Text.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    // ── the tools report it ────────────────────────────────────────────────────────────

    private static ToolContext Context(EngineFixture fx)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: fx.Artifacts);

    [Fact]
    public async Task Edit_file_reports_what_it_changed_and_a_failed_edit_reports_nothing()
    {
        using var fx = new EngineFixture();
        fx.Write("t.cs", TestFile);

        var done = await new EditFileTool().InvokeAsync(
            JsonSerializer.Serialize(new { path = "t.cs", old_string = Anchor, new_string = Anchor + Added }), Context(fx), CancellationToken.None);
        var failed = await new EditFileTool().InvokeAsync(
            JsonSerializer.Serialize(new { path = "t.cs", old_string = "not there", new_string = "x" }), Context(fx), CancellationToken.None);

        Assert.True(done.Success, done.Error);
        var change = Assert.IsType<WrittenChange>(done.Metadata[WrittenChange.MetadataKey]);
        Assert.Contains("+     public void GetBestMove_WinTakesPriorityOverBlock()", change.Lines);
        Assert.False(failed.Success);
        Assert.False(failed.Metadata.ContainsKey(WrittenChange.MetadataKey));
    }

    [Fact]
    public async Task Write_file_reports_what_it_changed_in_a_file_it_replaced_and_nothing_for_a_new_one()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "# Notes\n\nfirst\nsecond\n");

        var replaced = await new WriteFileTool().InvokeAsync(
            JsonSerializer.Serialize(new { path = "notes.md", content = "# Notes\n\nfirst\nsecond, changed\n" }), Context(fx), CancellationToken.None);
        var created = await new WriteFileTool().InvokeAsync(
            JsonSerializer.Serialize(new { path = "new.md", content = "hello\n" }), Context(fx), CancellationToken.None);

        Assert.True(replaced.Success, replaced.Error);
        var change = Assert.IsType<WrittenChange>(replaced.Metadata[WrittenChange.MetadataKey]);
        Assert.Equal(["@@ -3,3 +3,3 @@", "  first", "- second", "+ second, changed", "  "], change.Lines);
        Assert.True(created.Success, created.Error);
        Assert.Null(created.Metadata[WrittenChange.MetadataKey]);
    }
}
