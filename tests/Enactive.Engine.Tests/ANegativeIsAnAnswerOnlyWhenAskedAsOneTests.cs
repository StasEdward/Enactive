namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Run 4f1d97, 2026-09-28: three page steps that had written their findings were left Incomplete by a
/// last call that "failed" - findstr finding no line (exit 1), dir on a missing file (exit 1), and an
/// edit whose old_string was stale. An exit 1 is not forgiven; a negative asked with a tool that can
/// say it settles the question, and an edit is settled by its file holding what it wanted.
/// </summary>
public sealed class ANegativeIsAnAnswerOnlyWhenAskedAsOneTests
{
    private static string Shell(string command) => System.Text.Json.JsonSerializer.Serialize(new { command });

    // ── which shell calls are lookups, and about what ──────────────────────────────────

    [Theory]
    [InlineData("findstr /c:\"**DISCREPANCY**\" Docs/DRIFT_ollama.md | find /c /v \"\"", "findstr", "Docs/DRIFT_ollama.md")]
    [InlineData("dir /b src\\Enactive.Agents\\BackgroundDecisionHandler.cs src\\Enactive.App.Ui\\ApprovalStore.cs", "dir",
        "src/Enactive.Agents/BackgroundDecisionHandler.cs|src/Enactive.App.Ui/ApprovalStore.cs")]
    [InlineData("Test-Path ./wiki/a.md", "test-path", "wiki/a.md")]
    public void A_shell_lookup_is_recognised_with_the_paths_it_asks_about(string command, string program, string paths)
    {
        Assert.Equal(program, ShellLookup.Program("run_command", Shell(command)));
        Assert.Equal(paths.Split('|'), ShellLookup.Paths("run_command", Shell(command)));
    }

    [Theory]
    [InlineData("dotnet build Enactive.sln")]
    [InlineData("git status")]
    public void A_command_that_is_not_a_lookup_is_not_one(string command)
        => Assert.Null(ShellLookup.Program("run_command", Shell(command)));

    // ── what settles a failed lookup ───────────────────────────────────────────────────

    private static readonly ToolDefinition[] Tools =
    [
        new("run_command", "shell", "{}", Kind: ToolKind.Command),
        new("file_stats", "stats", "{}", Kind: ToolKind.Read),
        new("count_matches", "count", "{}", Kind: ToolKind.Read),
        new("edit_file", "edit", "{}", Kind: ToolKind.Write, RepairsFileFailures: true),
        new("write_file", "write", "{}", Kind: ToolKind.Write, RepairsFileFailures: true)
    ];

    private static ToolCall Call(string tool, string args) => new(Guid.NewGuid().ToString("N"), tool, args);

    [Fact]
    public void A_failed_lookup_is_settled_by_a_structured_answer_for_every_path_it_asked_about()
    {
        var open = new OpenFailures(Tools);
        open.Failed(Call("run_command", Shell("dir /b src\\A.cs src\\B.cs")), "Command exited with code 1.");
        Assert.Equal(1, open.Count);

        open.FoundNothing(Call("file_stats", """{"path":"src/A.cs"}"""), "Not a folder or file in this workspace: src/A.cs");
        open.Succeeded(Call("file_stats", """{"path":"src/Unrelated.cs"}"""), []);
        Assert.Equal(1, open.Count);                                            // B.cs is still unanswered

        open.FoundNothing(Call("file_stats", """{"path":"./src/B.cs"}"""), "Not a folder or file in this workspace: src/B.cs");
        open.Succeeded(Call("file_stats", """{"path":"src/Unrelated.cs"}"""), []);
        Assert.Equal(0, open.Count);
    }

    [Fact]
    public void A_count_with_no_matches_settles_a_search_that_exited_1()
    {
        var open = new OpenFailures(Tools);
        open.Failed(Call("run_command", Shell("findstr /c:\"X\" Docs/r.md | find /c /v \"\"")), "Command exited with code 1.");
        open.Succeeded(Call("count_matches", """{"path":"Docs/r.md","pattern":"X"}"""), []);
        Assert.Equal(0, open.Count);
    }

    /// <summary>"Not forgiven": an exit 1 from a command that asks nothing is not settled by a lookup.</summary>
    [Fact]
    public void An_exit_1_that_is_not_a_lookup_stays_open_whatever_is_looked_up()
    {
        var open = new OpenFailures(Tools);
        open.Failed(Call("run_command", Shell("dotnet test tests/A.csproj")), "Command exited with code 1.");
        open.Succeeded(Call("file_stats", """{"path":"tests/A.csproj"}"""), []);
        Assert.Equal(1, open.Count);
    }

    // ── what settles a failed edit ─────────────────────────────────────────────────────

    [Fact]
    public void A_failed_edit_is_settled_by_its_file_holding_what_it_wanted_and_by_nothing_else()
    {
        var open = new OpenFailures(Tools);
        open.Failed(Call("edit_file", """{"path":"r.md","old_string":"| 10 | Settings | Pending |","new_string":"| 10 | Settings | Reviewed |"}"""),
            "'old_string' does not appear in r.md");

        open.Succeeded(Call("write_file", """{"path":"r.md","content":"x"}"""), [new(Guid.NewGuid(), Enactive.Core.Artifacts.ArtifactKind.FileSet, "r.md", "r.md")]);
        Assert.Equal(1, open.Count);                                            // another write is not the change
        Assert.Empty(open.Settle(_ => "| 10 | Settings | Pending |"));        // the old text is still there
        Assert.Equal(["r.md"], open.Settle(_ => "# report\n| 10 | Settings | Reviewed |\n"));
        Assert.Equal(0, open.Count);
    }

    // ── what the edit tool says ────────────────────────────────────────────────────────

    [Fact]
    public async Task An_edit_whose_change_is_already_in_says_so_and_one_that_is_stale_says_to_read_first()
    {
        using var fx = new EngineFixture();
        fx.Write("r.md", "# report\n| 10 | Settings | Reviewed |\n");

        var done = await fx.Invoke(new EditFileTool(), """{"path":"r.md","old_string":"| 10 | Settings | Pending |","new_string":"| 10 | Settings | Reviewed |"}""");
        Assert.False(done.Success);
        Assert.Contains("the change is already in the file", done.Error, StringComparison.Ordinal);
        Assert.Contains("(line 2)", done.Error, StringComparison.Ordinal);

        var stale = await fx.Invoke(new EditFileTool(), """{"path":"r.md","old_string":"| 11 | Templates | Pending |","new_string":"| 11 | Templates | Reviewed |"}""");
        Assert.Contains("sending the same 'old_string' again will fail again", stale.Error, StringComparison.Ordinal);
    }

    // ── through a run ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE ONE THAT MATTERS: a step writes its findings, checks with findstr (exit 1 - nothing found),
    /// is told to ask structurally, asks with count_matches, and finishes - not Incomplete.
    /// </summary>
    [Fact]
    public async Task A_step_told_to_ask_structurally_finishes()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"check"}"""),
            Turn.Calls1("write_file", """{"path":"r.md","content":"no problems found\n"}""", "w1"),
            Turn.Calls1("run_command", Shell("findstr /c:\"PROBLEM\" r.md"), "c1"),
            Turn.Calls1("count_matches", """{"path":"r.md","pattern":"PROBLEM"}""", "n1"),
            Turn.Says("No problems recorded."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "check the report");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(worker.Requests[3].Messages, m => (m.Content ?? "").Contains("Ask the question with", StringComparison.Ordinal)
                                                           && (m.Content ?? "").Contains("count_matches", StringComparison.Ordinal));
    }
}
