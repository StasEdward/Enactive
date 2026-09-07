namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// What a failed command SAID is in the evidence the reviewer judges, not only in the transcript
/// the model reads.
///
/// <para>On 2026-09-07 at 23:42 a "verify the tests fail when the behaviour is broken" step did
/// exactly that: made the parser throw on empty input, ran the tests, and watched the process die
/// with <c>Unhandled exception. System.ArgumentException: HTML cannot be null or empty</c> and exit
/// code <c>-532462766</c>. It reported the crash, faithfully, then restored the file.</para>
///
/// <para>The reviewer failed it: "the reported crash is fabricated". Its evidence for that call
/// read, in full, <c>ERROR: Command exited with code -532462766.</c> — the journal recorded a
/// failure's error line and dropped its output, which is where the exception was. The model had
/// been given both since <see cref="FailedToolFeedbackTests"/>; the reviewer had not. The same
/// defect, one consumer over: the record somebody works from is shortened, and nothing says so.
/// This time the record was the one that decides the outcome.</para>
/// </summary>
public sealed class FailureOutputInEvidenceTests
{
    private const string QuickPlan = """{"disposition":"quick_action","title":"break it and see","steps":[]}""";

    // A process whose exit code says nothing and whose stderr is the whole story - as in the run:
    // a "test run" that crashes while the "parser" throws, and is green once it does not. The same
    // command both times, which is what lets the step reach review at all: a failure left open
    // ends the step before any reviewer is asked, and only the same call succeeding closes it.
    private const string TestRun = """{"command":"testrun.cmd"}""";

    private static void InstallTestRun(EngineFixture fx)
    {
        fx.Write("parser.txt", "throw");
        fx.Write("testrun.cmd", string.Join("\r\n",
            "@echo off",
            "findstr /C:\"throw\" parser.txt >nul",
            "if %errorlevel%==0 (",
            "  echo Unhandled exception. System.ArgumentException: enactive-crash-marker 1>&2",
            "  exit /b 3",
            ")",
            "echo all green again",
            ""));
    }

    private static Turn Restore()
        => Turn.Calls1("write_file", """{"path":"parser.txt","content":"return Empty"}""", "restore");

    /// <summary>The evidence the reviewer was handed, for the one review request in a run.</summary>
    private static string EvidenceIn(FakeChatProvider reviewer)
    {
        var prompt = Assert.Single(reviewer.Requests).Messages.Last().Content ?? "";
        const string opens = "the agent's own words above may be wrong or invented):";
        var from = prompt.IndexOf(opens, StringComparison.Ordinal);
        Assert.True(from >= 0, "not an execution review prompt:\n" + prompt);
        return prompt[(from + opens.Length)..];
    }

    private static Task<List<WorkEvent>> RunAsync(EngineFixture fx, FakeChatProvider agent, FakeChatProvider reviewer)
        => fx.RunAsync(
            fx.Build(agent, EngineFixture.Role("developer"),
                     router: Routers.WithReviewer(), reviewProvider: reviewer),
            "make the tests fail on purpose");

    /// <summary>
    /// The one that matters: the exception text is in the evidence. With the fix reverted the
    /// evidence holds the exit code and nothing else, and this fails.
    /// </summary>
    [Fact]
    public async Task A_failed_command_reaches_the_reviewer_with_its_output()
    {
        using var fx = new EngineFixture();
        InstallTestRun(fx);

        var agent = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", TestRun),
            Restore(),
            Turn.Calls1("run_command", TestRun),
            Turn.Says("The process crashed with System.ArgumentException, as intended; restored and green."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        await RunAsync(fx, agent, reviewer);

        var evidence = EvidenceIn(reviewer);
        Assert.Contains("ERROR:", evidence);
        Assert.Contains("exited with code 3", evidence);
        Assert.Contains("enactive-crash-marker", evidence);
    }

    /// <summary>
    /// And it is the SAME text the model was given - one failure, one account of it. If the two
    /// ever differ again, the reviewer is judging the agent against a record the agent never saw.
    /// </summary>
    [Fact]
    public async Task The_model_and_the_reviewer_are_told_the_same_thing_about_a_failure()
    {
        using var fx = new EngineFixture();
        InstallTestRun(fx);

        var agent = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", TestRun),
            Restore(),
            Turn.Calls1("run_command", TestRun),
            Turn.Says("It crashed, then it was fine."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        await RunAsync(fx, agent, reviewer);

        var toldTheModel = agent.Requests.Last().Messages
            .Where(m => m.Role == ChatRole.Tool)
            .Select(m => m.Content ?? "")
            .Single(m => m.StartsWith("ERROR: ", StringComparison.Ordinal));

        // The transcript copy says "ERROR: <failure>"; the evidence copy says "<- ERROR: <failure>".
        // Strip the prefix each adds and the failure text underneath must be identical.
        var failure = toldTheModel["ERROR: ".Length..].Trim();
        Assert.Contains(failure, EvidenceIn(reviewer).Replace("\r\n", "\n"));
    }

    /// <summary>A failure with nothing to add still says only the error - no blank line under it.</summary>
    [Fact]
    public async Task A_failure_with_no_output_is_still_one_line_in_the_evidence()
    {
        using var fx = new EngineFixture();
        fx.Write("parser.txt", "throw");
        // Exit 3 and not a word of output while the "parser" throws; fine once it does not.
        fx.Write("quiet.cmd", "@echo off\r\nfindstr /C:\"throw\" parser.txt >nul && exit /b 3\r\necho fine\r\n");

        const string quiet = """{"command":"quiet.cmd"}""";
        var agent = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", quiet),
            Restore(),
            Turn.Calls1("run_command", quiet),
            Turn.Says("Failed once, then fine."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        await RunAsync(fx, agent, reviewer);

        var lines = EvidenceIn(reviewer).Replace("\r\n", "\n").Split('\n');
        var at = Array.FindIndex(lines, l => l.Contains("ERROR: Command exited with code 3", StringComparison.Ordinal));
        Assert.True(at >= 0, "the failure is not in the evidence");
        Assert.False(string.IsNullOrWhiteSpace(lines[at + 1]), "a blank line was appended under a failure with no output");
    }
}
