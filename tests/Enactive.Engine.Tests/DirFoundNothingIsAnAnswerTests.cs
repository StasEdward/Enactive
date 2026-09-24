namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Tools;
using Xunit;

/// <summary>
/// cmd's <c>dir</c> answering "nothing there" is an answer, the same as <c>Get-ChildItem</c>'s.
///
/// <para><b>Measured 2026-09-24 11:08, run 5f793c.</b> The step's first act was to ask whether the
/// report already existed:</para>
///
/// <code>
/// run_command {"command": "dir /b Docs\DRIFT*"}
/// exit code 1
/// [stderr]
/// File Not Found
/// </code>
///
/// <para>It did not — the person deletes it before every run. The call was filed as a failure, the
/// step went on to do its work, and two minutes later it ended <c>INCOMPLETE: unresolved tool call:
/// run_command dir /b Docs\DRIFT*</c>, taking the three steps after it down with it. The same
/// question asked of PowerShell has been an answer since 2026-09-20.</para>
/// </summary>
public sealed class DirFoundNothingIsAnAnswerTests
{
    private const string FileNotFound = "[stderr]\nFile Not Found";

    [Fact]
    public void A_pattern_that_matched_nothing_found_nothing()
        => Assert.Equal(ShellVerdict.FoundNothing, ShellOutcome.Of(@"dir /b Docs\DRIFT*", FileNotFound));

    [Fact]
    public void A_folder_that_is_not_there_found_nothing()
        => Assert.Equal(ShellVerdict.FoundNothing,
                        ShellOutcome.Of(@"dir /b Docs\gone\*", "[stderr]\nThe system cannot find the path specified."));

    /// <summary>
    /// THE BOUNDARY the text is there for. <c>dir</c> exits 1 for a switch it does not have too,
    /// and that is a command that went wrong.
    /// </summary>
    [Fact]
    public void An_invalid_switch_is_still_a_failure()
        => Assert.Equal(ShellVerdict.Ran, ShellOutcome.Of("dir /z", "[stderr]\nInvalid switch - \"z\"."));

    /// <summary>
    /// Another program saying the same words is not a lookup. <c>copy</c> that cannot find its
    /// source is a change that did not happen.
    /// </summary>
    [Fact]
    public void The_same_words_from_a_command_that_changes_things_are_a_failure()
        => Assert.Equal(ShellVerdict.Ran,
                        ShellOutcome.Of(@"copy missing.txt Docs\", "[stderr]\nThe system cannot find the file specified."));

    /// <summary>And a <c>dir</c> inside a pipeline is a pipeline, whose other half may have broken.</summary>
    [Fact]
    public void A_dir_with_something_after_it_is_not_just_a_dir()
        => Assert.Equal(ShellVerdict.Ran, ShellOutcome.Of(@"dir /b Docs\DRIFT* & del notes.txt", FileNotFound));

    /// <summary>Through the real tool, against the real cmd.exe.</summary>
    [Fact]
    public async Task The_real_dir_is_answered_not_failed()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new RunCommandTool(), """{"command":"dir /b NOTHING_HERE_*"}""");

        Assert.False(result.Success);
        Assert.True(result.IsAnswer, result.Error);
    }

    /// <summary>
    /// THE ONE THAT MATTERS: the step as it happened. Asking whether the report exists, being told
    /// no, and then writing it is a step that finished.
    /// </summary>
    [Fact]
    public async Task A_step_that_began_by_finding_nothing_can_still_finish()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write the report"}"""),
            Turn.Calls1("run_command", """{"command":"dir /b DRIFT*"}"""),
            Turn.Calls1("write_file", """{"path":"DRIFT.md","content":"# findings"}""", "c2"),
            Turn.Says("Wrote DRIFT.md."));

        var events = await fx.RunAsync(fx.Build(provider), "write the report");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }
}
