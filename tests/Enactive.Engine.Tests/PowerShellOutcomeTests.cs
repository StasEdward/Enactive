namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Whether a PowerShell script SUCCEEDED, which is not the same question as what powershell.exe
/// returned.
///
/// <para>From a real run on 2026-09-10: "find where Total Commander is and start it". The agent
/// made six searches. Five were reported as failures. Every one of them had done exactly what it
/// was asked - <c>powershell.exe</c> returns 1 when the last statement left <c>$?</c> false, and
/// <c>-ErrorAction SilentlyContinue</c> silences the DISPLAY of an error, not the error. A recursive
/// search that meets one unreadable folder, or a registry lookup for a key that is not there,
/// therefore "fails". The one search that passed was the one with nothing to silence.</para>
///
/// <para>What the model was shown in place of a cause was the CLIXML PowerShell writes when its
/// error stream is redirected - two progress records saying "Preparing modules for first use" -
/// under a line reading "the output above says what went wrong". So it could not tell "found
/// nothing" from "broke", spent five calls and a 34-second scan of the whole C: drive, and the run
/// was recorded Incomplete although the agent had finished and reported honestly.</para>
///
/// <para>Windows only: powershell.exe. The engine suite runs on windows-latest.</para>
/// </summary>
public sealed class PowerShellOutcomeTests
{
    /// <summary>
    /// Exit 1, and nothing wrong: the key is absent, which was the question, and the caller said so
    /// with -ErrorAction SilentlyContinue. The registry rather than the filesystem so the result
    /// does not depend on which folders this machine lets a test read.
    /// </summary>
    private const string AsksAboutSomethingAbsent =
        @"Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\App Paths\NoSuchThing.exe' -ErrorAction SilentlyContinue";

    /// <summary>Exit 1, and something IS wrong, said in words.</summary>
    private const string ReallyFails =
        @"Get-Content 'C:\definitely\not\here\at\all.txt'";

    private static async Task<ToolResult> Run(EngineFixture fx, string script)
        => await new RunPowerShellTool().InvokeAsync(
            $$"""{"script": {{JsonSerializer.Serialize(script)}} }""",
            fx.ContextFor(),
            CancellationToken.None);

    /// <summary>
    /// The decisive one. A script that answered its question is not a failed script, whatever
    /// number the host process returned.
    /// </summary>
    [Fact]
    public async Task A_script_that_reported_no_error_did_not_fail()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fx = new EngineFixture();

        var result = await Run(fx, AsksAboutSomethingAbsent);

        Assert.True(result.Success,
            "A script whose only fault was a silenced error was reported as a failure: " + result.Error);
    }

    /// <summary>
    /// And the other half, which is what stops the fix from being "call everything success": a
    /// script that really failed still fails, and still says why.
    /// </summary>
    [Fact]
    public async Task A_script_that_really_failed_still_fails_and_says_why()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fx = new EngineFixture();

        var result = await Run(fx, ReallyFails);

        Assert.False(result.Success, "A missing file was reported as success.");
        Assert.Contains("not\\here\\at\\all.txt", result.Output ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An error is words. With the error stream redirected, PowerShell serialises its non-output
    /// streams as CLIXML unless it is told otherwise - so the "output" a model was asked to read for
    /// a cause was an XML progress record. Nothing downstream can recover from that, and no wording
    /// of the failure message can make it legible.
    /// </summary>
    [Fact]
    public async Task What_a_script_says_is_text_and_not_serialised_objects()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fx = new EngineFixture();

        var quiet = await Run(fx, AsksAboutSomethingAbsent);
        var loud = await Run(fx, ReallyFails);

        Assert.DoesNotContain("CLIXML", quiet.Output ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CLIXML", loud.Output ?? "", StringComparison.OrdinalIgnoreCase);

        // And the progress records that came with it, which were two thirds of what the model read.
        Assert.DoesNotContain("Preparing modules", quiet.Output ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
