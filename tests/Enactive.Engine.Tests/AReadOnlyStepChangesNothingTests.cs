namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Run 014ad0a9, 2026-09-29: "Analyze code and existing test coverage" made 53 edits to test files and one to the
/// application's markup - fixing tests that had failed before the run - and after sixteen minutes had not begun
/// the analysis. A step the planner marks read-only looks and reports; its file changes are refused. Deliberately
/// not code: invoices checked, then corrected.
/// </summary>
public sealed class AReadOnlyStepChangesNothingTests
{
    private const string Plan = """
        {"disposition":"task","title":"invoices",
         "steps":[{"title":"Check the invoice totals","dependsOn":[],"readOnly":true},
                  {"title":"Correct the wrong totals","dependsOn":[0]}]}
        """;

    [Fact]
    public async Task A_read_only_step_is_told_so_and_its_file_changes_are_refused_but_scratch_is_its_own()
    {
        using var fx = new EngineFixture();
        fx.Write("invoices/a.txt", "total: 10");
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1("read_file", """{"path":"invoices/a.txt"}""", "r1"),
            Turn.Calls1("edit_file", """{"path":"invoices/a.txt","old_string":"total: 10","new_string":"total: 12"}""", "e1"),
            Turn.Calls1("write_file", """{"path":".enactive/scratch/notes.md","content":"a.txt: total should be 12"}""", "w1"),
            Turn.Says("a.txt: the total should be 12."),
            Turn.Calls1("edit_file", """{"path":"invoices/a.txt","old_string":"total: 10","new_string":"total: 12"}""", "e2"),
            Turn.Says("Corrected a.txt."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "check the invoices and correct the totals");

        var first = string.Join("\n", worker.Requests[1].Messages.Select(m => m.Content));
        Assert.Contains("This step is READ-ONLY", first, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Kind == EventKind.ToolResult
                                     && e.Summary.StartsWith("edit_file -> refused: 'invoices/a.txt' was not changed: this step was planned as read-only", StringComparison.Ordinal));
        Assert.True(events.Any(e => e.Kind == EventKind.ToolResult && e.Summary.StartsWith("write_file -> ok", StringComparison.Ordinal)), events.Text());
        Assert.Equal("total: 12", fx.Read("invoices/a.txt"));                   // the step after it made the change
        var secondInstruction = string.Join("\n", worker.Requests[^2].Messages.Where(m => m.Content?.Contains("Correct the wrong totals") == true).Select(m => m.Content));
        Assert.DoesNotContain("This step is READ-ONLY", secondInstruction[secondInstruction.LastIndexOf("Proceed with this step", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reviewer_of_a_read_only_step_is_told_it_was_planned_to_change_nothing()
    {
        using var fx = new EngineFixture();
        fx.Write("invoices/a.txt", "total: 10");
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1("read_file", """{"path":"invoices/a.txt"}""", "r1"),
            Turn.Says("a.txt: the total should be 12."),
            Turn.Says("Nothing to correct after all."));
        var reviewer = new FakeChatProvider(Turn.Says("""{"verdict":"pass","reason":"the step did its part","calls":[1],"files":[]}"""), Turn.Says("""{"verdict":"pass","reason":"the step did its part","calls":[1],"files":[]}"""));

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "check the invoices and correct the totals");

        var firstReview = string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content));
        Assert.Contains("It was planned as READ-ONLY", firstReview, StringComparison.Ordinal);
        // A change the request asks for, done a step early, is not a failure of this step; one it does not ask for is.
        Assert.Contains("A change the request asks for, made here instead of in a later step, does not fail this step", firstReview, StringComparison.Ordinal);
        var secondReview = string.Join("\n", reviewer.Requests[^1].Messages.Select(m => m.Content));
        Assert.DoesNotContain("It was planned as READ-ONLY", secondReview, StringComparison.Ordinal);
    }

    [Fact]
    public void The_planner_is_told_what_a_read_only_step_is()
        => Assert.Contains("\"readOnly\":true", Planner.SystemPromptFor(null), StringComparison.Ordinal);
}

public sealed class APathKeepsTheDotInItsNameTests
{
    [Theory]
    [InlineData("./a/b.md", "a/b.md")]
    [InlineData(@".\a\b.md", "a/b.md")]
    [InlineData("/a/", "a")]
    [InlineData(".enactive/scratch/n.md", ".enactive/scratch/n.md")]
    [InlineData("./.github/x.yml", ".github/x.yml")]
    [InlineData(".", "")]
    public void Only_a_leading_dot_slash_is_spelling(string path, string normal)
        => Assert.Equal(normal, Enactive.Core.Context.ShellLookup.Normal(path));
}
