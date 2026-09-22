namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Xunit;

/// <summary>
/// A helper built in the worker's own working area must not read as a write outside the workspace.
///
/// <para>The worker is told to put helper scripts in <c>.enactive/scratch/</c>, and the natural
/// PowerShell way to build one is to name the root once. Measured 2026-09-20: an agent wrote
/// <c>$root = ".enactive\scratch\probe"; New-Item -Path "$root\Calc"</c>, every token after the
/// assignment was a variable, and every one was reported as a write whose place is unknown. The
/// question was answered conservatively and the agent was refused the working area it had been
/// instructed to use — friction created by the scratch area itself.</para>
/// </summary>
public sealed class ScratchGeographyTests
{
    private const string Root = @"C:\ws";

    [Fact]
    public void A_scratch_path_built_from_a_literal_variable_asks_nothing()
    {
        var writes = ShellGeography.WritesOutside(
            "$root = \".enactive\\scratch\\probe\"; New-Item -ItemType Directory -Force -Path \"$root\\Calc\"",
            Root);

        Assert.Empty(writes);
    }

    /// <summary>The same path written out longhand already asked nothing; it still does not.</summary>
    [Fact]
    public void The_same_path_written_out_longhand_is_unchanged()
    {
        Assert.Empty(ShellGeography.WritesOutside(
            @"New-Item -ItemType Directory -Force -Path "".enactive\scratch\probe\Calc""", Root));
    }

    /// <summary>
    /// The guard: resolving a variable makes the guess better informed, not blinder. A literal
    /// that points OUT of the workspace is now seen, where before it was merely unknown.
    /// </summary>
    [Fact]
    public void A_variable_holding_a_path_outside_is_still_reported()
    {
        var writes = ShellGeography.WritesOutside(
            "$out = \"C:\\builds\\drop\"; Copy-Item a.txt \"$out\\a.txt\"", Root);

        var write = Assert.Single(writes);
        Assert.True(write.Known, "a resolved path should be reported as known, not as a variable");
        Assert.Contains("builds", write.Path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And a variable it cannot resolve still asks. This only ever turns "I cannot see" into a
    /// real answer; it never turns it into silence.
    /// </summary>
    [Fact]
    public void A_variable_with_no_literal_value_still_asks()
    {
        var writes = ShellGeography.WritesOutside(
            "$out = Join-Path $env:TEMP 'drop'; Copy-Item a.txt \"$out\\a.txt\"", Root);

        Assert.NotEmpty(writes);
        Assert.Contains(writes, w => !w.Known);
    }

    /// <summary>
    /// Measured 2026-09-20, the SECOND time the working area asked a question about itself.
    ///
    /// <para>Verbatim from the log: <c>cd TicTacToe</c>, then the test output piped into
    /// <c>..\.enactive\scratch\list_tests.txt</c>. The agent was right — after the cd, <c>..</c>
    /// is the workspace root — and the file landed in <c>Game\.enactive\scratch\</c>, which was
    /// checked on disk. This class resolved the token against the root instead, produced
    /// <c>…\AI\.enactive\scratch\</c>, one level ABOVE the workspace, and put the question to
    /// the person. <c>…\AI\.enactive</c> does not exist and never did.</para>
    /// </summary>
    [Fact]
    public void A_scratch_path_reached_by_going_up_after_a_cd_asks_nothing()
        => Assert.Empty(ShellGeography.WritesOutside(
            """
            cd TicTacToe
            dotnet test T.csproj --list-tests | Set-Content ..\.enactive\scratch\list_tests.txt
            """,
            Root));

    /// <summary>
    /// The same in the other direction, which is the half that makes following a <c>cd</c> honest
    /// rather than merely permissive: a script that moves OUT and then writes relatively used to
    /// be judged against the root and look innocent. Now it is seen.
    /// </summary>
    [Fact]
    public void Moving_outside_and_then_writing_relatively_is_now_reported()
    {
        var writes = ShellGeography.WritesOutside(
            """
            cd ..\..\elsewhere
            copy a.txt drop\a.txt
            """, Root);

        Assert.NotEmpty(writes);
        Assert.Contains(writes, w => w.Path.Contains("elsewhere", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A cd it cannot read leaves the guess exactly where it was.</summary>
    [Fact]
    public void A_cd_into_a_variable_changes_nothing()
        => Assert.Empty(ShellGeography.WritesOutside(
            """
            cd $somewhere
            Set-Content .enactive\scratch\out.txt 'x'
            """, Root));

    /// <summary>The assignment itself is not a write, and must not become one once substituted.</summary>
    [Fact]
    public void The_assignment_is_not_itself_reported()
    {
        Assert.Empty(ShellGeography.WritesOutside("$root = \"sub/dir\"", Root));
    }
}
