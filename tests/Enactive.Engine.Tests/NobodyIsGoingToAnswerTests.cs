namespace Enactive.Engine.Tests;

using Enactive.Tools;
using Xunit;

/// <summary>
/// A command that asks for input gets end-of-file, not the timeout.
///
/// <para><b>Found while answering a different question.</b> Asked which of
/// <c>desktop-commander</c>'s tools were worth taking, the honest answer needed a measurement of
/// what our shells actually spend time on — and it turned up <b>32 timeouts</b> across three runs
/// on 2026-09-23/24. Every one that can be identified is the same shape:</para>
///
/// <code>
/// wsl -- bash -c "sudo apt-get install -y -qq smartmontools"   → timed out after 60s
/// wsl -- bash -c "sudo smartctl --version"                     → timed out after 60s
/// </code>
///
/// <para><c>sudo</c> waiting for a password at a terminal that does not exist. At 60 and 90 seconds
/// apiece that is about half an hour of three runs spent waiting for nobody — and it reads as a
/// command that HUNG, which is a different diagnosis from one that could not ask.</para>
///
/// <para>The fix is at the pipe, not in the prompt: stdin is redirected and closed the instant the
/// child starts, so anything that reads from it gets EOF. No instruction can make a model reliably
/// remember <c>sudo -n</c>; the execution layer can make forgetting cheap.</para>
/// </summary>
public sealed class NobodyIsGoingToAnswerTests
{
    /// <summary>
    /// A command that reads stdin finishes instead of waiting. On Windows <c>set /p</c> is the
    /// cheapest thing that asks — with a console it blocks until somebody types, and with a closed
    /// pipe it returns at once.
    /// </summary>
    [Fact]
    public async Task A_command_that_asks_for_input_does_not_wait()
    {
        using var fx = new EngineFixture();

        var started = DateTime.UtcNow;

        var result = await fx.Invoke(
            new RunCommandTool(),
            """{"command":"set /p answer=Password: ","expectedExitCodes":[0,1]}""");

        var took = DateTime.UtcNow - started;

        // THE REAL ASSERTION. If stdin is open, `set /p` waits for a typist who does not exist and
        // the command dies at the 60-second timeout saying so. Nothing else produces that word here.
        Assert.DoesNotContain("timed out", result.Error ?? "", StringComparison.OrdinalIgnoreCase);

        // The clock is a second opinion, in case the timeout is ever raised - and it is deliberately
        // loose. Measured 2026-09-24: with a 35B model saturating the machine, spawning git.exe took
        // about ten seconds and cmd.exe did not finish inside sixty, so this test reports the host
        // being overloaded as well as the pipe being open. Forty separates a closed pipe, which
        // returns in about a hundred milliseconds, from a waiting one, which cannot return before
        // sixty; between those two numbers there is only the machine.
        Assert.True(took < TimeSpan.FromSeconds(40),
                    $"it waited {took.TotalSeconds:N0}s for an answer nobody was going to give "
                    + "(or the machine was too busy to start a process - check the load)");
    }

    /// <summary>
    /// And an ordinary command is untouched — the pipe is closed, not the output. A guard that
    /// broke normal commands to fix a rare hang would be a bad trade, and this is the assertion
    /// that says it did not.
    /// </summary>
    [Fact]
    public async Task An_ordinary_command_still_works()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new RunCommandTool(), """{"command":"echo still here"}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("still here", result.Output, StringComparison.Ordinal);
    }

    /// <summary>PowerShell too — it is the same launcher underneath, and the same pipe.</summary>
    [Fact]
    public async Task Powershell_that_asks_for_input_does_not_wait()
    {
        using var fx = new EngineFixture();

        var started = DateTime.UtcNow;

        await fx.Invoke(
            new RunPowerShellTool(),
            """{"script":"$x = Read-Host 'Password'; Write-Output \"got:$x\"","expectedExitCodes":[0,1]}""");

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30),
                    "Read-Host with a closed stdin must return rather than wait out the timeout");
    }
}
