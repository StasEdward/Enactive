namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Xunit;

/// <summary>
/// The engine end of <see cref="Enactive.Core.Context.ShellRefusal"/>: a line cmd.exe would not
/// start is a sentence that did not parse, and the model saying the same thing to the shell that
/// HAS the word is the recovery.
///
/// <para>Reported 2026-09-20, the shape that produced this. A run sent
/// <c>run_command {"command": "… | Select-String …"}</c>, cmd.exe answered <i>'Select-String' is
/// not recognized</i> with exit 255, the model immediately re-sent the work as
/// <c>run_powershell</c> and it worked — and the step was still Incomplete, because the open
/// failure could only be closed by "the same tool", read as the same tool NAME.</para>
///
/// <para>These call the real shells. That is the point: the vocabulary being matched is cmd.exe's
/// and PowerShell's own, and a test that typed the message out by hand would only prove that this
/// file agrees with itself.</para>
/// </summary>
public sealed class RefusedByTheShellTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"check the log"}""";

    /// <summary>
    /// A cmdlet sent to cmd.exe, then the same work sent to PowerShell. Nothing ran the first
    /// time, so there is nothing left over for the step to be holding.
    /// </summary>
    [Fact]
    public async Task A_cmdlet_cmd_does_not_have_is_closed_by_the_other_shell()
    {
        using var fx = new EngineFixture();
        fx.Write("log.txt", "all good\nerror: nope\n");

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("run_command",
                                """{"command":"Select-String -Path log.txt -Pattern error"}"""),
                    // A DIFFERENT operation, as the reported run's recovery was. Re-sending the
                    // identical command would be closed by ShellOperation alone and prove nothing
                    // about this change; a model that has just been told a word does not exist
                    // does not reach for that word again.
                    Turn.Calls1("run_powershell",
                                """{"script":"Get-Content log.txt | Where-Object { $_ -match 'error' }"}""", "c2"),
                    Turn.Says("One line matches: \"error: nope\".")),
                EngineFixture.Role("developer")),
            "find the errors in log.txt");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// THE BOUNDARY, and the reason this change is narrow. <c>exit /b 1</c> RAN. Nothing about a
    /// later PowerShell call succeeding says anything about it, and the step stays open — which is
    /// exactly what <c>RecoveredByAnotherRouteTests</c> decided on 2026-09-07 and what the
    /// widening this test guards against would have quietly undone.
    /// </summary>
    [Fact]
    public async Task A_command_that_ran_and_failed_is_not_closed_by_the_other_shell()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("run_command", """{"command":"exit /b 1"}"""),
                    Turn.Calls1("run_powershell", """{"script":"Write-Output 'fine'"}""", "c2"),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "run the build");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }
}
