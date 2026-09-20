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

    /// <summary>The assignment itself is not a write, and must not become one once substituted.</summary>
    [Fact]
    public void The_assignment_is_not_itself_reported()
    {
        Assert.Empty(ShellGeography.WritesOutside("$root = \"sub/dir\"", Root));
    }
}
