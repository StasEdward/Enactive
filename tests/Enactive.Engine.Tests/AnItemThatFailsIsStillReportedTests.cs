namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Run 80c951, 2026-09-28: a step read a guessed path again and again at new offsets; one item step
/// filled its window and the rest were lost behind it; and with every item step failed, the final
/// report was skipped - so the run said nothing about any page.
/// </summary>
public sealed class AnItemThatFailsIsStillReportedTests
{
    // ── a path that is not there ────────────────────────────────────────────────────────

    /// <summary>A missing file is answered with the nearest folder that exists and what it holds - not "at another offset".</summary>
    [Fact]
    public async Task A_missing_file_is_answered_with_where_to_look()
    {
        using var fx = new EngineFixture();
        fx.Write("src/Enactive.Core/Tasks.cs", "x");
        fx.Write("src/Enactive.Agents/Planner.cs", "x");

        var result = await fx.Invoke(new ReadFileTool(), """{"path":"src/Enactive.AppHost/Program.cs","offset":401}""");

        Assert.True(result.IsAnswer);
        Assert.Contains("'src/Enactive.AppHost' does not exist either", result.Error, StringComparison.Ordinal);
        Assert.Contains("'src' holds: Enactive.Agents/, Enactive.Core/", result.Error, StringComparison.Ordinal);
        Assert.Contains("will not find it", result.Error, StringComparison.Ordinal);
    }

    /// <summary>The same read twice in one turn is run once; the second is answered as the same call.</summary>
    [Fact]
    public async Task The_same_read_twice_in_one_turn_is_run_once()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "only once please");
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"read"}"""),
            new Turn(null, [new("r1", "read_file", """{"path":"notes.md"}"""), new("r2", "read_file", """{"path":"notes.md"}""")]),
            Turn.Says("Read it."));

        var events = await fx.RunAsync(fx.Build(worker), "read the notes");

        var after = worker.Requests[2].Messages;
        Assert.Single(after, m => (m.Content ?? "").Contains("only once please", StringComparison.Ordinal));
        Assert.Contains(after, m => m.ToolCallId == "r2" && (m.Content ?? "").StartsWith("Not run: this is the same call", StringComparison.Ordinal));
        Assert.Single(events, e => e.Kind == EventKind.ToolResult && e.Summary.Contains("only once please", StringComparison.Ordinal));
    }

    // ── one item overflows, the next starts fresh, the report names both ────────────────

    private const string Plan = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review page","dependsOn":[0],"forEach":{"step":0,"field":"pages"},
                   "output":{"notes":{"type":"results","description":"a note per page"}}},
                  {"title":"write the report","dependsOn":[1]}]}
        """;

    /// <summary>
    /// THE ONE THAT MATTERS (the regression of run 80c951): the first item step fills its window and
    /// stops; the second starts from a fresh, bounded conversation and finishes; the report step runs
    /// and is told what each came to; the run is not called finished.
    /// </summary>
    [Fact]
    public async Task One_item_overflowing_does_not_stop_the_next_and_the_report_names_both()
    {
        using var fx = new EngineFixture { StepOutputs = true, DynamicSteps = true };
        fx.Write("wiki/a.md", "# A\n");
        fx.Write("wiki/b.md", "# B\n");
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md","wiki/b.md"]}""", "s0"), Turn.Says("Found two."),
            Turn.Says("All about page a: " + new string('a', 35_000)),                  // fills page a's window; never hands on
            Turn.Calls1("read_file", """{"path":"wiki/b.md"}""", "rb"),
            Turn.Calls1(StepOutputContract.ToolName, """{"notes":{"wiki/b.md":"fine"}}""", "sb"), Turn.Says("Reviewed b."),
            Turn.Says("Report written."))
        { Window = 12_000 };

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(events, e => e.Kind == EventKind.StepCompleted
                                     && e.Summary.Contains("review page: wiki/a.md — INCOMPLETE: the context window is full", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("review page: wiki/b.md — done", StringComparison.Ordinal));

        var report = string.Join("\n", worker.Requests.Last().Messages.Select(m => m.Content ?? ""));
        Assert.Contains("Proceed with this step of the plan: write the report", report, StringComparison.Ordinal);
        Assert.Contains("- wiki/a.md: incomplete - the context window is full", report, StringComparison.Ordinal);
        Assert.Contains("- wiki/b.md: done", report, StringComparison.Ordinal);
        Assert.Contains("""{"notes":{"wiki/b.md":"fine"}}""", report, StringComparison.Ordinal);
    }

    /// <summary>With every item step failed, the report is still written - about why.</summary>
    [Fact]
    public async Task With_every_item_failed_the_report_is_still_written()
    {
        using var fx = new EngineFixture { StepOutputs = true, DynamicSteps = true };
        fx.Write("wiki/a.md", "# A\n");
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md"]}""", "s0"), Turn.Says("Found one."),
            Turn.Says("I looked."), Turn.Says("I really did."),                          // never hands its result on
            Turn.Says("Report written: page a was not reviewed."));

        var events = await fx.RunAsync(fx.Build(worker), "review every wiki page");

        Assert.NotEqual(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Contains(events, e => e.Kind == EventKind.StepCompleted && e.Summary.Contains("write the report — done", StringComparison.Ordinal));
        Assert.Contains("- wiki/a.md: incomplete", string.Join("\n", worker.Requests.Last().Messages.Select(m => m.Content ?? "")),
            StringComparison.Ordinal);
    }
}
