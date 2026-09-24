namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Tools;
using Xunit;

/// <summary>
/// A search that matched nothing is an ANSWER, however it was asked.
///
/// <para><b>The measurement, and it is one turn wide.</b> 2026-09-22 23:56:43, run <c>bba2df</c>,
/// two tool calls 55 milliseconds apart asking the same question of the same folder:</para>
///
/// <code>
/// search_files {"pattern":"ENACTIVE_OWNER_KEY|…"}  -> "No matches in 1 file(s)."   ok
/// run_command  {"command":"findstr /s /i /n …"}     -> exit 1, no output           FAILED
/// </code>
///
/// <para>The second ended the run: step 2 Incomplete, steps 3 and 4 skipped. The engine decided on
/// 2026-09-20 that a lookup told "not there" is an answer — <see cref="ShellVerdict.FoundNothing"/>
/// exists for exactly that — but it reads the verdict out of the error TEXT, and a search that
/// matches nothing does not produce any. It reports with its exit code and stays silent, the one
/// shape that reading could not see.</para>
///
/// <para>These run the real programs. The convention being relied on is theirs.</para>
/// </summary>
public sealed class ASearchThatMatchedNothingTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"look for it"}""";

    /// <summary>
    /// The reported turn, near enough verbatim: the step reads a file, then asks the same
    /// question twice - once of findstr, which matches nothing and says so with an exit code,
    /// and once of search_files, which matches nothing and says so in words. Before this, one of
    /// them was an answer and the other ended the run.
    /// </summary>
    [Fact]
    public async Task Findstr_matching_nothing_does_not_fail_the_run()
    {
        using var fx = new EngineFixture();
        fx.Write("Program.cs", "class Program { }");

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("read_file", """{"path":"Program.cs"}"""),
                    Turn.Calls1("run_command",
                                """{"command":"findstr /s /i /n \"zzznotthereatall\" *.cs"}""", "c2"),
                    Turn.Calls1("search_files",
                                """{"path":".","pattern":"zzznotthereatall"}""", "c3"),
                    Turn.Says("Program.cs does not mention it, and neither does anything else.")),
                EngineFixture.Role("developer")),
            "does anything mention zzznotthereatall");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// And the model is told which of the two it is. "Exited with code 1" invites a retry, and on
    /// 2026-09-20 a step spent eight turns doing exactly that with a command whose output it could
    /// not see.
    /// </summary>
    [Fact]
    public void The_answer_says_it_is_an_answer()
    {
        var result = ProcessExec.BuildResult(
            "Command", exitCode: 1, stdout: "", stderr: "",
            commandLine: "findstr /s /i /n \"nope\" *.cs");

        Assert.False(result.Success);
        Assert.Contains("matched nothing", result.Error, StringComparison.Ordinal);
        Assert.Contains("ANSWER", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("expectedExitCodes", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE BOUNDARY. Trouble is always printed — <c>FINDSTR: Cannot open x</c>, <c>grep: x: No such
    /// file</c> — so any output at all disqualifies the reading, and the call stays the failure it
    /// was. Silence is the whole signal.
    /// </summary>
    [Theory]
    // command, exit code, output, is it a search that found nothing?
    [InlineData("findstr /s \"x\" *.cs", 1, "", true)]
    [InlineData("grep -r x src", 1, "", true)]
    [InlineData("rg x", 1, "", true)]
    [InlineData("findstr /s \"x\" *.cs", 1, "FINDSTR: Cannot open foo.cs", false)]  // it complained
    [InlineData("grep -r x src", 2, "", false)]                                     // 2 is trouble
    [InlineData("dotnet build", 1, "", false)]                                      // not a search
    [InlineData("findstr x *.cs | sort", 1, "", false)]                             // sort's code
    [InlineData("findstr x *.cs > out.txt", 1, "", false)]                          // redirected
    [InlineData("git diff --exit-code", 1, "", false)]                              // 1 means found
    public void Only_a_silent_one_counts(string command, int exitCode, string output, bool expected)
        => Assert.Equal(expected, ShellOutcome.SearchFoundNothing(command, output, exitCode));

    /// <summary>
    /// And a step whose ONLY record is misses is still not a success — that rule predates this and
    /// is what stops "I looked and found nothing" being a way to finish without doing the work.
    /// </summary>
    [Fact]
    public async Task A_step_that_only_ever_missed_is_still_not_done()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""{"disposition":"task","title":"find and fix","steps":[{"title":"Find the setting and change it","dependsOn":[],"complexity":"normal"}],"checks":[]}"""),
                    Turn.Calls1("run_command",
                                """{"command":"findstr /s /i /n \"zzznotthereatall\" *.cs"}"""),
                    Turn.Says("I could not find it, so I changed nothing.")),
                EngineFixture.Role("developer")),
            "find where the timeout is set and change it to 30");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }
}
