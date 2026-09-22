namespace Enactive.Engine.Tests;

using Enactive.Tools;
using Xunit;

/// <summary>
/// A shell tool called with the OTHER shell tool's argument name.
///
/// <para>There are two, and their argument is named after the shell: <c>run_command</c> takes
/// <c>command</c>, <c>run_powershell</c> takes <c>script</c>. A model reaching for PowerShell
/// while holding the other tool's shape sends <c>run_powershell {"command": …}</c> and was told
/// <i>'script' is required</i> — true, and no help at all, since it did send one. Nine such calls
/// over two days, three of them inside one fifteen-minute run.</para>
///
/// <para>The same answer <c>list_dir</c> gives for a path that is a file: the call did not work,
/// the reason is not what the plain message says, and the right thing to do is named.</para>
/// </summary>
public sealed class TheOtherShellsArgumentTests
{
    [Fact]
    public async Task PowerShell_told_the_model_which_argument_it_takes()
    {
        using var fx = new EngineFixture();

        var error = (await fx.Invoke(new RunPowerShellTool(), """{"command":"Get-ChildItem"}""")).Error ?? "";

        Assert.Contains("'script'", error, StringComparison.Ordinal);
        Assert.Contains("run_command", error, StringComparison.Ordinal);
        Assert.Contains("PowerShell", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task And_the_other_way_round()
    {
        using var fx = new EngineFixture();

        var error = (await fx.Invoke(new RunCommandTool(), """{"script":"dir /b"}""")).Error ?? "";

        Assert.Contains("'command'", error, StringComparison.Ordinal);
        Assert.Contains("run_powershell", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// It REFUSES rather than reading one as the other. The key is evidence of which SHELL was
    /// meant, and cmd syntax run through PowerShell is a different command — so a wasted turn is
    /// the cheaper mistake.
    /// </summary>
    [Fact]
    public async Task It_does_not_quietly_run_it_anyway()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new RunPowerShellTool(), """{"command":"Write-Output hi"}""");

        Assert.False(result.Success);
        Assert.Null(result.Output);
    }

    /// <summary>
    /// And it is still a sentence that did not parse, not work that did not happen: nothing ran,
    /// so it must not hold the step open. See ToolResults.Unreadable.
    /// </summary>
    [Fact]
    public async Task Nothing_ran_so_nothing_is_left_unfinished()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new RunPowerShellTool(), """{"command":"Write-Output hi"}""");

        Assert.True(result.DidNotRun);
    }

    /// <summary>With neither argument there is no other tool to point at, and it says the plain thing.</summary>
    [Fact]
    public async Task With_no_command_at_all_it_says_the_plain_thing()
    {
        using var fx = new EngineFixture();

        var error = (await fx.Invoke(new RunPowerShellTool(), """{"expectedExitCodes":[0]}""")).Error ?? "";

        Assert.Equal("'script' is required.", error);
    }

    /// <summary>An empty one is not one: it is the same silence spelled differently.</summary>
    [Fact]
    public async Task An_empty_other_argument_is_not_a_mix_up()
    {
        using var fx = new EngineFixture();

        var error = (await fx.Invoke(new RunPowerShellTool(), """{"command":"   "}""")).Error ?? "";

        Assert.Equal("'script' is required.", error);
    }
}
