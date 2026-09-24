namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// <c>git</c> and <c>docker</c> get an operation, like the shells have had since 2026-09-20.
///
/// <para><b>The hole.</b> An open failure is closed by DOING the thing, and <c>OpenFailures</c>
/// files each one under the file the call names, the shell operation it is, or the kind of write it
/// was. A <c>git</c> call matches none of the three: it carries no <c>path</c>, <c>ShellTools.All</c>
/// is the two shells only, and it changes no file the journal tracks. So it landed in
/// <c>_byCall</c> alone, where the one thing that could close it was re-sending the byte-identical
/// call.</para>
///
/// <para>A <c>git push</c> rejected as non-fast-forward, followed by a pull and a push that worked,
/// left the step holding the first one for good. §9ap solved exactly this for the shells; the same
/// argument applies, and §9bn recorded it as open because "any later git closes an earlier git" is
/// too loose and the right key needed thought. The right key is the one the shells already use.</para>
/// </summary>
public sealed class GitHasAnOperationTooTests
{
    private static string? Op(string tool, string argsJson)
        => ShellOperation.For(tool, argsJson);

    /// <summary>
    /// The operation is the tool, its subcommand and its first real operand — the same three parts
    /// a command line keeps. Flags are plumbing here as much as there.
    /// </summary>
    [Fact]
    public void The_same_operation_however_it_is_flagged()
    {
        Assert.Equal(
            Op("git", """{"args":["push","origin","main"]}"""),
            Op("git", """{"args":["push","--force-with-lease","origin","main"]}"""));

        // And written as one string, which both tools also accept.
        Assert.Equal(
            Op("git", """{"args":["status","--short"]}"""),
            Op("git", """{"args":"status --short"}"""));
    }

    /// <summary>
    /// THE BOUNDARY, and it is the reason the operand is kept. Two reads of different things are
    /// different things: a successful <c>git show</c> of one file must not clear a failed
    /// <c>git show</c> of another.
    /// </summary>
    [Fact]
    public void Different_operands_are_different_operations()
    {
        Assert.NotEqual(
            Op("git", """{"args":["show","HEAD:a.md"]}"""),
            Op("git", """{"args":["show","HEAD:b.md"]}"""));

        Assert.NotEqual(
            Op("git", """{"args":["push"]}"""),
            Op("git", """{"args":["status"]}"""));

        Assert.NotEqual(
            Op("git", """{"args":["ps"]}"""),
            Op("docker", """{"args":["ps"]}"""));
    }

    /// <summary>
    /// A pipe in a commit message is part of the message. Nothing splits an argument list, so the
    /// plumbing rule a command line needs would only corrupt the key here.
    /// </summary>
    [Fact]
    public void A_pipe_inside_an_argument_is_not_plumbing()
        => Assert.Equal(
            Op("git", """{"args":["commit","-m","fix a|b"]}"""),
            Op("git", """{"args":["commit","-m","fix a|b"]}"""));

    /// <summary>Tools that are not given a command or an argument list still say nothing.</summary>
    [Theory]
    [InlineData("write_file", """{"path":"a.txt","content":"x"}""")]
    [InlineData("git", """{"args":[]}""")]
    [InlineData("git", "not json")]
    public void Nothing_to_identify_is_null(string tool, string argsJson)
        => Assert.Null(Op(tool, argsJson));

    /// <summary>
    /// End to end, and the whole point: a git call that RAN and failed, then the same operation
    /// run again once the cause was fixed. Before this the step carried the first one to the end,
    /// because only the byte-identical call could close it and the model had no reason to repeat
    /// a call it had already been told was wrong.
    /// </summary>
    [Fact]
    public async Task The_same_operation_working_later_closes_the_earlier_failure()
    {
        using var fx = new EngineFixture();
        GitInit(fx.Root);

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"stage the note"}"""),
                    // Fails: the file is not there yet.
                    Turn.Calls1("git", """{"args":["add","note.txt"]}"""),
                    Turn.Calls1("write_file",
                                """{"path":"note.txt","content":"hello"}""", "c2"),
                    // The cause is fixed and the SAME operation now works.
                    Turn.Calls1("git", """{"args":["add","--verbose","note.txt"]}""", "c3"),
                    Turn.Says("Staged note.txt.")),
                EngineFixture.Role("developer")),
            "stage the note");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// A real repository in the fixture root: outside one, git refuses before it parses anything
    /// and the test would be measuring the wrong failure.
    /// </summary>
    private static void GitInit(string root)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", "init -q")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(10_000);
    }
}
