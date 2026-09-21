namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Xunit;

/// <summary>
/// A check may answer an open question. It may not make missing work have happened.
///
/// <para><b>A regression from the promotion that shipped earlier the same day.</b> Measured
/// 2026-09-21 20:57: step 1 of a three-step plan ended Incomplete, steps 2 and 3 were skipped
/// behind it, and the run was reported <c>Completed</c> — because the planner's check
/// <c>Docs/DRIFT_ollama.md exists and is not empty</c> passed against the scaffold step 1 had
/// written before it stopped. A third of the work, called done.</para>
///
/// <para>The baseline could not catch this and was never going to: the file was absent before the
/// run, so the check really did fail then and really did count as proof. Proof of a write, which
/// is all it ever claimed to be.</para>
///
/// <para>So the two kinds of Incomplete are separated. "A tool call was never closed" is an
/// absence of EVIDENCE, and an exit code is the cure. "Two steps never ran" is an absence of
/// WORK, and nothing a command prints can supply it.</para>
/// </summary>
public sealed class SkippedWorkIsNotAnOpenQuestionTests
{
    /// <summary>
    /// The reported shape: a step that fails, dependants skipped behind it, and a check that
    /// passes anyway. Before this, Completed.
    /// </summary>
    [Fact]
    public async Task A_run_with_skipped_steps_is_not_promoted_by_a_passing_check()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    // Two steps, the second waiting on the first.
                    Turn.Says("""
                        {"disposition":"task","title":"two things","steps":[
                          {"title":"write the notes","dependsOn":[],"complexity":"normal"},
                          {"title":"check the notes","dependsOn":[0],"complexity":"normal"}],
                         "checks":[{"name":"notes exist","command":"dir notes.md"}]}
                        """),
                    // Step 1 writes the file - which satisfies the check - and then leaves a
                    // command unresolved, so it cannot be called done and step 2 never starts.
                    Turn.Calls1("write_file", """{"path":"notes.md","content":"# scaffold\n"}"""),
                    Turn.Calls1("run_command", """{"command":"exit /b 1"}""", "c2"),
                    Turn.Says("Done."))
                { WhenExhausted = Turn.Says("Done.") },
                EngineFixture.Role("developer")),
            "write and check the notes");

        Assert.True(fx.Exists("notes.md"), "the check's subject really was written");
        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// And the case the promotion exists for is untouched: everything ran, one call was left
    /// unclosed, the check says the work is there.
    /// </summary>
    [Fact]
    public async Task A_run_where_everything_ran_is_still_answered_by_its_check()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""
                        {"disposition":"quick_action","title":"write the notes","steps":[],
                         "checks":[{"name":"notes exist","command":"dir notes.md"}]}
                        """),
                    Turn.Calls1("run_command", """{"command":"exit /b 1"}"""),
                    Turn.Calls1("write_file", """{"path":"notes.md","content":"# notes\n"}""", "c2"),
                    Turn.Says("Done."))
                { WhenExhausted = Turn.Says("Done.") },
                EngineFixture.Role("developer")),
            "write the notes");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }
}
