namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Run fba4d6, 2026-09-29, a local model behind a server that caches the prompt: the tool list changed between steps
/// (a hand-over for one step, none for the next) and the server read 46,000 tokens again; the command history was
/// taken out of the middle of the conversation and put back at the end, and it read 5,000 to 30,000 again for each
/// new command; and a step with nothing to hand on said "done - all 133 tests passed" four turns running with the same
/// test run attached, looking for the hand-over it had used before, and was stopped as stuck. The conversation now
/// only grows: one tool list for the run, a history that is continued, and a step that says it is done and repeats
/// itself is done. Deliberately not code: a wiki's pages, listed, checked and summed up.
/// </summary>
public sealed class TheConversationOnlyGrowsTests
{
    private const string Plan = """
        {"disposition":"task","title":"wiki",
         "steps":[{"title":"List the pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"Check the links","dependsOn":[0]},
                  {"title":"Sum it up","dependsOn":[1],"output":{"summary":{"type":"text","description":"the summary"}}}]}
        """;

    [Fact]
    public async Task Every_step_of_a_run_is_offered_the_same_tools_and_one_with_nothing_to_hand_on_is_told_so()
    {
        using var fx = new EngineFixture { StepOutputs = true };
        fx.Write("wiki/home.md", "# Home");
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/home.md"]}""", "s0"), Turn.Says("Listed one page."),
            Turn.Calls1(StepOutputContract.ToolName, """{"note":"links fine"}""", "s1"), Turn.Says("The links work."),
            Turn.Calls1(StepOutputContract.ToolName, """{"summary":"one page, links fine"}""", "s2"), Turn.Says("Summed up."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "check the wiki");

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        var steps = worker.Requests.Skip(1).ToArray();
        string Tools(ChatRequest r) => string.Join("|", r.Tools!.Select(t => t.Name + t.Description + t.JsonSchema));
        Assert.Single(steps.Select(Tools).Distinct());                                       // one list, the same bytes
        var submit = steps[0].Tools!.Single(t => t.Name == StepOutputContract.ToolName);
        Assert.Contains("pages (path[]", submit.Description, StringComparison.Ordinal);
        Assert.Contains("summary (text", submit.Description, StringComparison.Ordinal);

        var middle = string.Join("\n", steps.First(r => r.Messages.Any(m => m.Content?.Contains("Proceed with this step of the plan: Check the links") == true))
            .Messages.Select(m => m.Content));
        Assert.Contains(StepOutputContract.NothingToHandOn.Trim(), middle, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Kind == EventKind.ToolResult && e.Summary.Contains("Nothing was stored: this step hands nothing on", StringComparison.Ordinal));
        var first = string.Join("\n", steps[0].Messages.Select(m => m.Content));
        Assert.Contains("This step hands its result on with submit_step_output: pages (path[]", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_that_says_it_is_done_and_repeats_a_call_is_done_not_stuck()
    {
        using var fx = new EngineFixture();
        fx.Write("wiki/home.md", "# Home");
        var read = new ToolCall("r", "read_file", """{"path":"wiki/home.md"}""");
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"read the page"}"""),
            new Turn(null, [read]),
            new Turn("Done: the page's title is Home.", [read with { Id = "r2" }]),
            new Turn("Done: the page's title is Home.", [read with { Id = "r3" }]));

        var events = await fx.RunAsync(fx.Build(worker), "what is the home page's title");

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Single(events, e => e.Kind == EventKind.ToolInvoked);                          // the repeat did not run
        Assert.Contains(events, e => e.Summary.StartsWith("The step said it was done and repeated read_file", StringComparison.Ordinal));
        Assert.Equal(3, worker.Requests.Count);
    }

    [Fact]
    public async Task The_command_history_is_continued_and_what_was_there_stays_where_it_was()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"count"}"""),
            Turn.Calls1("run_command", """{"command":"echo one"}""", "c1"),
            Turn.Calls1("run_command", """{"command":"echo two"}""", "c2"),
            Turn.Says("Counted to two."));

        await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "count to two");

        var second = worker.Requests[2].Messages;
        var third = worker.Requests[3].Messages;
        Assert.Equal(second.Select(m => m.Content), third.Take(second.Count).Select(m => m.Content));   // nothing moved
        var continued = third.Last(m => m.Content?.StartsWith("Engine-owned command history", StringComparison.Ordinal) == true).Content!;
        Assert.StartsWith("Engine-owned command history, continued", continued, StringComparison.Ordinal);
        Assert.Contains("echo two", continued, StringComparison.Ordinal);
        Assert.DoesNotContain("echo one", continued, StringComparison.Ordinal);
    }
}
