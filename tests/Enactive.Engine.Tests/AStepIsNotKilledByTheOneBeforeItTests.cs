namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Xunit;

/// <summary>
/// Run 80c951, 2026-09-28 13:29: one step filled a shared conversation, and eleven steps after it ended
/// "the context window is full ... nothing left to trim" in three seconds without making a call; the
/// step's own handover was refused as "no room" at 61,413 of 65,536 real tokens; and the planner's
/// criteria, stated inside its steps, were lost without a word.
/// </summary>
public sealed class AStepIsNotKilledByTheOneBeforeItTests
{
    /// <summary>
    /// THE ONE THAT MATTERS: the step before it left the shared conversation too full to send, and this
    /// step, which has done nothing of its own, starts from its own instruction instead of ending.
    /// </summary>
    [Fact]
    public async Task A_step_that_inherits_a_full_conversation_starts_from_its_own_instruction()
    {
        using var fx = new EngineFixture();
        const string plan = """
            {"disposition":"task","title":"write then check",
             "steps":[{"title":"write it all out","dependsOn":[]},{"title":"check it","dependsOn":[0]}]}
            """;
        var worker = new FakeChatProvider(
            Turn.Says(plan),
            Turn.Says("Everything I found: " + new string('x', 35_000)),     // an answer, not a tool result: nothing trims it
            Turn.Says("Checked."))
        { Window = 12_000 };

        var events = await fx.RunAsync(fx.Build(worker), "write and check");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.True(events.Any(e => e.Kind == EventKind.ContextTrimmed
                                     && e.Summary.Contains("inherited from earlier steps leaves it no room", StringComparison.Ordinal)), events.Text());
        var check = worker.Requests.Last();
        Assert.DoesNotContain(check.Messages, m => (m.Content ?? "").Contains(new string('x', 1_000), StringComparison.Ordinal));
        Assert.True(check.Messages.Any(m => (m.Content ?? "").Contains("Earlier steps of this plan are already finished", StringComparison.Ordinal)), events.Text());
        Assert.Contains(check.Messages, m => (m.Content ?? "").Contains("Proceed with this step of the plan: check it", StringComparison.Ordinal));
    }

    /// <summary>A step's own writing is its work: a step that has replied is not started again, whatever it inherited.</summary>
    [Fact]
    public async Task A_step_that_has_written_is_not_started_again()
    {
        using var fx = new EngineFixture();
        const string plan = """
            {"disposition":"task","title":"write then check",
             "steps":[{"title":"write it all out","dependsOn":[]},{"title":"check it","dependsOn":[0]}]}
            """;
        var worker = new FakeChatProvider(
            Turn.Says(plan),
            Turn.Says("Everything I found: " + new string('x', 60_000)),     // cut at the turn's limit, and the step goes on
            Turn.Says("Checked."), Turn.Says("Checked."))
        { Window = 16_000 };

        var events = await fx.RunAsync(fx.Build(worker), "write and check");

        Assert.DoesNotContain(events, e => e.Kind == EventKind.ContextTrimmed && e.StepNo() == 1
                                           && e.Summary.Contains("inherited from earlier steps", StringComparison.Ordinal));
    }

    /// <summary>
    /// A step for one item has a conversation of its own even at one step at a time: what the step for
    /// another item read is not in it (and so cannot fill it).
    /// </summary>
    [Fact]
    public async Task A_step_for_one_item_does_not_carry_another_items_reading()
    {
        using var fx = new EngineFixture { StepOutputs = true, DynamicSteps = true };
        fx.Write("wiki/a.md", "# A\nonly page a says this\n");
        fx.Write("wiki/b.md", "# B\nabout b\n");
        const string plan = """
            {"disposition":"task","title":"review",
             "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                      {"title":"review page","dependsOn":[0],"forEach":{"step":0,"field":"pages"}}]}
            """;
        var worker = new FakeChatProvider(
            Turn.Says(plan),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md","wiki/b.md"]}""", "s0"), Turn.Says("Found two."),
            Turn.Calls1("read_file", """{"path":"wiki/a.md"}""", "ra"), Turn.Says("Reviewed a."),
            Turn.Calls1("read_file", """{"path":"wiki/b.md"}""", "rb"), Turn.Says("Reviewed b."));

        var events = await fx.RunAsync(fx.Build(worker), "review every page");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        var forB = worker.Requests.Where(r => r.Messages.Any(m => (m.Content ?? "").Contains(
            "Proceed with this step of the plan: review page: wiki/b.md", StringComparison.Ordinal))).ToArray();
        Assert.NotEmpty(forB);
        Assert.All(forB, r => Assert.DoesNotContain(r.Messages, m => (m.Content ?? "").Contains("only page a says this", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A handover is fitted on the size the loop measured, not on a fixed three characters a token: a
    /// conversation whose characters would "estimate" past the window, and whose real count leaves room,
    /// gets its note.
    /// </summary>
    [Fact]
    public async Task A_handover_is_fitted_on_the_measured_size()
    {
        var provider = new FakeChatProvider(Turn.Says("note"), Turn.Says("note")) { Window = 10_000 };
        var request = new ChatRequest("model", [ChatMessage.User(new string('y', 33_000))], OutputTokenLimit: 1024);

        var estimated = await new Handover().GenerateAsync(provider, request, RunBudget.Unlimited(), default);
        Assert.Equal(HandoverFailure.NoRoom, estimated.Failure);
        Assert.Contains("estimated at", estimated.Detail, StringComparison.Ordinal);

        var measured = await new Handover().GenerateAsync(provider, request, RunBudget.Unlimited(), default, promptTokens: 7_000);
        Assert.Equal("note", measured.Note);
    }

    /// <summary>Criteria stated inside steps are read like those at the top; one stated twice is one.</summary>
    [Fact]
    public void Criteria_stated_inside_steps_are_read_and_a_repeated_one_counts_once()
    {
        using var doc = JsonDocument.Parse("""
            {"steps":[{"title":"a","criteria":[{"kind":"file_exists","path":"r.md"}]},
                      {"title":"b","criteria":[{"kind":"file_exists","path":"r.md"},{"kind":"file_contains","path":"r.md","text":"Total"}]}],
             "criteria":[{"kind":"tests_pass"}]}
            """);

        var read = TypedCriteria.Read(doc.RootElement);

        Assert.Equal(["tests_pass", "file_exists", "file_contains"], read.Select(c => c.Kind).ToArray());
    }
}
