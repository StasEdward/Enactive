namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Run 9c1a061b, 2026-09-28: "Program.cs:223 sets Timeout = InfiniteTimeSpan" could be neither confirmed nor
/// contradicted - the read of line 223 was among the oldest calls, not shown to the reviewer. What a line of a
/// file says is not a judgement: the engine opens the places a step cites, and checks a quote beside one.
/// Run 16d57849: the report the step wrote was ignored by git, so the review saw none of it.
/// </summary>
public sealed class ACitedPlaceIsOpenedByTheEngineTests
{
    private static EngineFixture Workspace()
    {
        var fx = new EngineFixture();
        fx.Write("src/Console/Program.cs", "using System;\nclass P\n{\n    var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };\n}\n");
        fx.Write("src/Other/Program.cs", "class Q {}\n");
        fx.Write("src/Settings.cs", "class S\n{\n    int Retries = 3;\n}\n");
        return fx;
    }

    private static IReadOnlyList<(string Cited, string Observed)> Observe(EngineFixture fx, string text)
        => CitedPlaces.Observe(text, fx.Root, () => CitedPlaces.Sweep(fx.Root));

    [Fact]
    public void The_lines_at_a_cited_place_are_opened_and_a_quote_beside_it_is_checked()
    {
        using var fx = Workspace();
        var (cited, observed) = Assert.Single(Observe(fx, "D1: src/Console/Program.cs:4 uses `Timeout.InfiniteTimeSpan`, not five minutes."));

        Assert.Equal("src/Console/Program.cs:4", cited);
        Assert.Contains("Opened by the engine when this step was reviewed - not a call of the step's: src/Console/Program.cs, lines 2-5 of 5", observed, StringComparison.Ordinal);
        Assert.Contains("4:     var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };", observed, StringComparison.Ordinal);
        Assert.Contains("Quoted beside it: `Timeout.InfiniteTimeSpan` - at line 4.", observed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quote_that_is_elsewhere_or_nowhere_is_said_to_be()
    {
        using var fx = Workspace();
        // Line 1 is shown with the two lines either side of it; the quote is on line 4.
        var elsewhere = Assert.Single(Observe(fx, "src/Console/Program.cs:1 sets `Timeout.InfiniteTimeSpan`")).Observed;
        Assert.Contains("Quoted beside it: `Timeout.InfiniteTimeSpan` - NOT at the cited place; it is at line 4.", elsewhere, StringComparison.Ordinal);

        var nowhere = Assert.Single(Observe(fx, "src/Settings.cs:3 sets `Retries = 5`")).Observed;
        Assert.Contains("Quoted beside it: `Retries = 5` - NOT in src/Settings.cs.", nowhere, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_is_resolved_only_when_it_names_one_file()
    {
        using var fx = Workspace();
        var one = Assert.Single(Observe(fx, "Settings.cs:3")).Observed;
        Assert.Contains("src/Settings.cs, lines 1-4 of 4 (cited as 'Settings.cs')", one, StringComparison.Ordinal);

        var two = Assert.Single(Observe(fx, "Program.cs:4")).Observed;
        Assert.Contains("'Program.cs' names more than one file", two, StringComparison.Ordinal);
        Assert.Contains("opened none of them", two, StringComparison.Ordinal);

        Assert.Contains("No file 'Missing.cs' in the workspace", Assert.Single(Observe(fx, "Missing.cs:1")).Observed, StringComparison.Ordinal);
        Assert.Contains("has 4 line(s): line 40 does not exist", Assert.Single(Observe(fx, "src/Settings.cs:40")).Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_place_cited_twice_is_opened_once_and_a_range_is_shown_whole()
    {
        using var fx = Workspace();
        var found = Observe(fx, "src/Settings.cs:2-3 and again src/Settings.cs:2-3");
        var (_, observed) = Assert.Single(found);
        Assert.Contains("lines 1-4 of 4", observed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_range_and_a_long_line_say_they_were_cut()
    {
        using var fx = new EngineFixture();
        fx.Write("big.txt", string.Join("\n", Enumerable.Range(1, 100).Select(i => i == 5 ? new string('x', 400) : $"line {i}")));

        var observed = Assert.Single(Observe(fx, "big.txt:3-80")).Observed;

        Assert.Contains("[the cited range runs to line 80; the engine opened its first 30 lines]", observed, StringComparison.Ordinal);
        Assert.Contains("… [line cut at 300 of its 400 characters]", observed, StringComparison.Ordinal);
        Assert.DoesNotContain("40: line 40", observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reviewer_is_shown_what_the_engine_found_at_the_places_the_report_cites()
    {
        using var fx = Workspace();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"check the timeout"}"""),
            Turn.Says("D1: src/Console/Program.cs:4 uses `Timeout.InfiniteTimeSpan`, while the page says five minutes."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "check the timeout claim");

        Assert.Contains(events, e => e.Summary.Contains("Opened 1 place(s) the step's report and result cite, for the review: src/Console/Program.cs:4", StringComparison.Ordinal));
        var shown = string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content));
        Assert.Contains("engine_opened_cited_place", shown, StringComparison.Ordinal);
        Assert.Contains("Timeout = Timeout.InfiniteTimeSpan", shown, StringComparison.Ordinal);
        Assert.Contains("Quoted beside it: `Timeout.InfiniteTimeSpan` - at line 4.", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_step_wrote_where_the_comparison_does_not_look_is_shown_to_the_reviewer()
    {
        using var fx = Workspace();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write the report"}"""),
            Turn.Calls1("write_file", """{"path":"bin/report.md","content":"# Report\n| page | finding |\n| a | TABLE-ROW-EIGHT |\n"}""", "w1"),
            Turn.Says("Wrote the report with its table."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer),
            "write the report");

        var shown = string.Join("\n", reviewer.Requests[0].Messages.Select(m => m.Content));
        Assert.Contains("TABLE-ROW-EIGHT", shown, StringComparison.Ordinal);
        Assert.Contains("where the workspace comparison does not look", shown, StringComparison.Ordinal);
    }
}
