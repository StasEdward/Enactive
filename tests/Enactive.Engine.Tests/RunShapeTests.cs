namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// The exact sequence of events a run emits, for a handful of representative runs.
///
/// <para><b>Why this file exists.</b> <c>FIX_PLAN.md</c> §9d states the problem with the Orchestrator
/// cuts plainly: <i>"A refactor cannot be verified differentially. Every fix in this repository was
/// proven by making it fail with its own fix reverted; moving code has no such proof, and its whole
/// safety rests on the suite."</i> That is true and it is not the end of the matter. What CAN be done
/// is to pin the observable: a run's event stream is the contract the UI, the history, the Inbox and
/// the reviewer all read, and if a pure move changes it, the move was not pure.</para>
///
/// <para>These are characterization tests, and they are honest about being that. They do not say the
/// sequence below is RIGHT — the rest of the suite argues about that, test by test. They say it is
/// what the engine did before the code was moved, in kind and in order, including the events other
/// tests never look at.</para>
///
/// <para>The payload is deliberately not compared. What is pinned is the shape: which kinds of event,
/// in which order, at which step. A summary's wording is somebody's sentence and changes for good
/// reasons; the shape changing is a behaviour change wearing a refactor's clothes.</para>
///
/// <para>Every expectation here was RECORDED from a run, not written from memory. The first draft
/// was written from memory and every one of the five was wrong — a per-step <c>Routed</c> event that
/// no other test looks at, and a first turn that calls a tool rather than speaking. A characterization
/// test written from what the author expected would pin the author's expectations, which is the one
/// thing it must not do.</para>
/// </summary>
public sealed class RunShapeTests
{
    /// <summary>Kind, and the step it belongs to — the two things a reader of a run navigates by.</summary>
    private static string[] Shape(IEnumerable<WorkEvent> events)
        => events.Select(e => e.StepNo() is { } step ? $"{e.Kind}#{step}" : e.Kind.ToString()).ToArray();

    /// <summary>
    /// A quick action that writes a file: the shortest complete path through the engine, and the one
    /// the planner is told to prefer, so it is the shape most runs actually take.
    /// </summary>
    [Fact]
    public async Task A_quick_action_that_writes_a_file()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write the note"}"""),
            Turn.Calls1("write_file", """{"path":"note.txt","content":"hello"}""", "w1"),
            Turn.Says("Written."));

        var events = await fx.RunAsync(fx.Build(provider), "write a note");

        Assert.Equal(
            new[]
            {
                "IntentReceived",
                "ContextAssembled",
                "Routed",
                "Routed",
                "ToolInvoked",
                "ToolResult",
                "ArtifactProduced",
                "AssistantDelta",
                "TaskCompleted"
            },
            Shape(events));
    }

    /// <summary>
    /// A two-step plan. Every step's events carry its number, which is what a UI attributes them by —
    /// and getting that wrong is a defect this engine has already had once, when the number was a
    /// dispatch counter rather than a plan position.
    /// </summary>
    [Fact]
    public async Task A_two_step_plan_that_writes_one_file_per_step()
    {
        using var fx = new EngineFixture();

        const string plan = """
            {"disposition":"task","title":"two steps",
             "steps":[{"title":"first","dependsOn":[]},{"title":"second","dependsOn":[0]}]}
            """;

        var provider = new FakeChatProvider(
            Turn.Says(plan),
            Turn.Calls1("write_file", """{"path":"one.txt","content":"1"}""", "w1"),
            Turn.Says("First done."),
            Turn.Calls1("write_file", """{"path":"two.txt","content":"2"}""", "w2"),
            Turn.Says("Second done."));

        var events = await fx.RunAsync(fx.Build(provider), "do two things");

        Assert.Equal(
            new[]
            {
                "IntentReceived",
                "ContextAssembled",
                "Routed",
                "PlanCreated",
                "StepStarted#1",
                "Routed#1",
                "ToolInvoked#1",
                "ToolResult#1",
                "ArtifactProduced#1",
                "AssistantDelta#1",
                "StepCompleted#1",
                "StepStarted#2",
                "Routed#2",
                "ToolInvoked#2",
                "ToolResult#2",
                "ArtifactProduced#2",
                "AssistantDelta#2",
                "StepCompleted#2",
                "TaskCompleted"
            },
            Shape(events));
    }

    /// <summary>
    /// A plan whose second step fails: the failure, the cascade, and the terminal event that is
    /// TaskFailed rather than TaskCompleted. The unhappy path has its own shape and it is the one
    /// most likely to be broken quietly by a move.
    /// </summary>
    [Fact]
    public async Task A_plan_whose_middle_step_is_stopped()
    {
        using var fx = new EngineFixture();

        const string plan = """
            {"disposition":"task","title":"three steps",
             "steps":[{"title":"first","dependsOn":[]},
                      {"title":"second","dependsOn":[0]},
                      {"title":"third","dependsOn":[1]}]}
            """;

        var stuck = Turn.Calls1("read_file", """{"path":"nowhere.txt"}""", "r");
        var provider = new FakeChatProvider(Turn.Says(plan), Turn.Says("First done."))
        {
            WhenExhausted = stuck
        };

        var events = await fx.RunAsync(fx.Build(provider), "do three things");

        Assert.Equal(
            new[]
            {
                "IntentReceived",
                "ContextAssembled",
                "Routed",
                "PlanCreated",
                "StepStarted#1",
                "Routed#1",
                "AssistantDelta#1",
                "StepCompleted#1",
                "StepStarted#2",
                "Routed#2",
                "ToolInvoked#2",
                "ToolResult#2",
                "ToolInvoked#2",
                "ToolResult#2",
                "ToolInvoked#2",
                "ToolResult#2",
                "ErrorObserved#2",
                "StepCompleted#2",
                "StepCompleted#3",
                "TaskFailed"
            },
            Shape(events));
    }

    /// <summary>
    /// A run whose success criteria are checked, and whose limit stops it. Two features that live at
    /// the very end and the very edge of the run body, and are therefore the two most likely to be
    /// dropped by a careless split of it.
    /// </summary>
    [Fact]
    public async Task A_run_stopped_by_its_step_limit()
    {
        using var fx = new EngineFixture();

        const string plan = """
            {"disposition":"task","title":"three steps",
             "steps":[{"title":"first","dependsOn":[]},
                      {"title":"second","dependsOn":[0]},
                      {"title":"third","dependsOn":[1]}]}
            """;

        var provider = new FakeChatProvider(Turn.Says(plan)) { WhenExhausted = Turn.Says("done") };

        var events = await fx.RunAsync(
            fx.Build(provider, limits: new ExecutionLimits(MaxSteps: 1)), "do three things");

        Assert.Equal(
            new[]
            {
                "IntentReceived",
                "ContextAssembled",
                "Routed",
                "PlanCreated",
                "StepStarted#1",
                "Routed#1",
                "AssistantDelta#1",
                "StepCompleted#1",
                "ErrorObserved",
                "StepCompleted#2",
                "StepCompleted#3",
                "TaskFailed"
            },
            Shape(events));
    }

    /// <summary>
    /// A reviewed step, with the proof pass behind it. The review path is the one with the most
    /// branches in it and the fewest of them on the happy path.
    /// </summary>
    [Fact]
    public async Task A_reviewed_step_that_passes_both_questions()
    {
        using var fx = new EngineFixture();

        const string plan = """
            {"disposition":"task","title":"one step",
             "steps":[{"title":"only","dependsOn":[]}]}
            """;

        var worker = new FakeChatProvider(
            Turn.Says(plan),
            Turn.Calls1("run_command", """{"command":"echo hi"}""", "c1"),
            Turn.Says("Ran it."));
        var reviewer = new FakeChatProvider(Verdicts.Pass(), Verdicts.Shown("the command succeeded", 1));

        var events = await fx.RunAsync(
            fx.Build(worker, router: Routers.WithReviewer(), reviewProvider: reviewer,
                     reviewRetries: 0, checkSoundness: true),
            "run one command");

        Assert.Equal(
            new[]
            {
                "IntentReceived",
                "ContextAssembled",
                "Routed",
                "Routed",
                "PlanCreated",
                "StepStarted#1",
                "Routed#1",
                "ToolInvoked#1",
                "ToolResult#1",
                "AssistantDelta#1",
                "ReviewRequested#1",
                "ReviewPassed#1",
                "ReviewPassed#1",
                "StepCompleted#1",
                "TaskCompleted"
            },
            Shape(events));
    }
}
