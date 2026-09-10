namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Tools;
using Xunit;

/// <summary>
/// What a shell command leaves behind when it is over.
///
/// <para><c>process.Kill(entireProcessTree: true)</c> reads like a kill-tree guarantee and is not
/// one: it walks LIVE parents, so a process whose parent has already exited is not in the tree it
/// can see. SANDBOX_PLAN §3 step 1 named the differential - "a command that spawns a detached
/// grandchild and then times out; today the grandchild outlives the run" - and these are it.</para>
///
/// <para>The pair reads as one rule with two halves: a CANCELLED command leaves nothing behind, and
/// a FINISHED command leaves running whatever it was asked to start. Either alone is a plausible
/// policy and the wrong one - the second was learned by shipping the first.</para>
///
/// <para>Both assert on an EFFECT: a file the survivor writes. Not on a process id and not on which
/// API was called. A survivor that is dying but not yet dead passes a liveness check and still
/// writes the file, and the file is what would have reached somebody's workspace.</para>
///
/// <para><b>Every quote lives in a .cmd file, and the command sent to the tool has none.</b> Two
/// earlier versions of this file put <c>start "" /b ...</c> in the command string. It never ran:
/// the tool passed the command through <c>ProcessStartInfo.ArgumentList</c>, which .NET joins with
/// the C RUNTIME's rules - escaping each inner quote as <c>\"</c> - and cmd.exe does not speak that
/// language. START received a mangled line, tried to launch an empty path, and said so in a MODAL
/// DIALOG on the developer's screen instead of on stderr. The grandchild never started, so one of
/// these tests passed while proving nothing. (The tool has since been fixed - see RunCommandTool -
/// but the discipline stays: a test whose setup can fail silently is not a test.)
///
/// Both facts are worth keeping: a command containing a quote can be mangled on the way to cmd
/// (its own finding, not this one), and a test whose SETUP silently fails is green for the wrong
/// reason. Bare tokens here mean neither can happen again.</para>
///
/// <para><b>Windows only.</b> Job objects are a Windows mechanism and START is a cmd builtin. The
/// engine suite runs on windows-latest for exactly this class of test - see build.yml.</para>
/// </summary>
public sealed class ShellContainmentTests
{
    /// <summary>
    /// Marks that it started, waits about two seconds, marks that it finished.
    ///
    /// <para>TWO marks, because one cannot tell "the process was killed" from "the process never
    /// ran". Earlier drafts of this file were green for the second reason twice - once from a
    /// mangled command line, once from a scenario that did not detach anything - and a test that
    /// passes because its own setup failed is worse than no test. <c>started.txt</c> is asserted
    /// PRESENT and <c>survivor.txt</c> ABSENT, so the pair says: it ran, and it was stopped.</para>
    /// </summary>
    private const string Survivor =
        "@echo off\r\necho started> started.txt\r\nping -n 3 127.0.0.1 >nul\r\necho survived> survivor.txt\r\n";

    /// <summary>
    /// Starts the survivor and EXITS AT ONCE. This is the whole mechanism: when this process is
    /// gone, the survivor's parent is gone, and a walk of live parents can no longer reach it.
    /// </summary>
    private const string Detach =
        "@echo off\r\npowershell -NoProfile -Command \"Start-Process -FilePath survivor.cmd -WindowStyle Hidden\"\r\n";

    /// <summary>
    /// Detaches, then goes on running so there is something to cancel.
    ///
    /// <para>THREE levels, not two, and that is the point. With only two, the cmd that started the
    /// survivor is still alive when the cancellation arrives, so the old Kill(entireProcessTree)
    /// reaches it and the test passes without the fix - which is what the first draft of this file
    /// did. The middle level exists so the survivor is genuinely orphaned before anything is
    /// cancelled.</para>
    /// </summary>
    private const string DetachThenWait =
        "@echo off\r\nstart \"\" /b detach.cmd\r\nping -n 30 127.0.0.1 >nul\r\n";

    private static void WriteScripts(EngineFixture fx)
    {
        fx.Write("survivor.cmd", Survivor);
        fx.Write("detach.cmd", Detach);
        fx.Write("launcher.cmd", DetachThenWait);
    }

    private static string Arguments(string command)
        => $$"""{"command": {{JsonSerializer.Serialize(command)}} }""";

    /// <summary>Past the survivor's own wait, so its absence means it is gone and not merely slow.</summary>
    private static async Task<bool> SurvivorAppeared(EngineFixture fx)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        return File.Exists(Path.Combine(fx.Root, "survivor.txt"));
    }

    /// <summary>Waits for the detached process to say it exists, so the test knows its own setup worked.</summary>
    private static async Task<bool> SurvivorStarted(EngineFixture fx)
    {
        var marker = Path.Combine(fx.Root, "started.txt");
        for (var i = 0; i < 50 && !File.Exists(marker); i++)
            await Task.Delay(100);
        return File.Exists(marker);
    }

    /// <summary>
    /// The plan's own differential: cancel a run whose command left a detached grandchild behind.
    ///
    /// <para>Before the job object this failed - the run was in the history as cancelled while the
    /// grandchild went on writing into the workspace.</para>
    /// </summary>
    [Fact]
    public async Task A_detached_grandchild_does_not_outlive_a_cancelled_command()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fx = new EngineFixture();
        WriteScripts(fx);

        using var cts = new CancellationTokenSource();

        var run = new RunCommandTool().InvokeAsync(
            Arguments("launcher.cmd"), fx.ContextFor(), cts.Token);

        // Waited FOR, not slept past: the cancellation must arrive while the survivor is genuinely
        // running and orphaned, and this is the only thing that can say it is.
        Assert.True(await SurvivorStarted(fx),
            "The detached process never started - this test would then pass without proving anything.");

        cts.Cancel();

        var result = await run;
        Assert.False(result.Success);

        Assert.False(await SurvivorAppeared(fx),
            "A process started by a cancelled command wrote to the workspace after the run was over.");
    }

    /// <summary>
    /// Starts the survivor and exits at once, in the same console, so the survivor inherits this
    /// command's redirected stdout and stderr. That inheritance IS the defect below; a launcher
    /// that did not hand the pipes on would not reproduce it.
    /// </summary>
    private const string LaunchAndReturn =
        "@echo off\r\nstart \"\" /b survivor.cmd\r\necho launched\r\n";

    /// <summary>
    /// The other half, and the one a real task asked for: "find Total Commander and start it".
    ///
    /// <para>Reported 2026-09-10 from a real run. Total Commander opened, and then the run sat
    /// there and died on its 60-second timeout - on a command that had exited in milliseconds -
    /// after which the job object killed the application it had just been asked to launch. Two
    /// faults, one scenario:</para>
    ///
    /// <list type="number">
    /// <item><b>The wait was on the wrong thing.</b> <c>WaitForExitAsync</c> waits for the
    /// redirected output to reach end-of-file as well as for the process to exit, and the launched
    /// application holds that pipe for as long as it lives. Now the wait is on
    /// <c>Process.Exited</c>, with a bounded grace for output already in flight.</item>
    /// <item><b>The containment was too eager.</b> KILL_ON_JOB_CLOSE meant nothing survived the
    /// call at all. A shell is asked to start programs; the job is now a handle used on
    /// cancellation, not a leash. See ProcessJob.</item>
    /// </list>
    ///
    /// <para>Three assertions, because each fault has its own: it came back QUICKLY, it came back
    /// SUCCESSFULLY, and what it started is STILL THERE. Asserting only the last would go green on
    /// a version that took a minute to say so.</para>
    ///
    /// <para>The exact inverse of the test above, deliberately. Cancelled: nothing survives.
    /// Finished: what it was asked to start does. Neither one alone is the rule.</para>
    /// </summary>
    [Fact]
    public async Task A_command_that_launches_a_program_returns_at_once_and_leaves_it_running()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fx = new EngineFixture();
        WriteScripts(fx);
        fx.Write("launch.cmd", LaunchAndReturn);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await new RunCommandTool().InvokeAsync(
            Arguments("launch.cmd"), fx.ContextFor(), CancellationToken.None);
        started.Stop();

        // Well under run_command's own 60s, and far enough under it that a slow machine cannot make
        // a hang look like a pass. The honest figure is about two seconds - the output grace.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20),
            $"The command exited in milliseconds but the tool took {started.Elapsed.TotalSeconds:F1}s "
            + "to say so - it is waiting on the launched program's pipe, not on the command.");

        Assert.True(result.Success, $"A command that launched a program was reported failed: {result.Error}");

        // The point of the whole reversal: the program is still there.
        Assert.True(await SurvivorAppeared(fx),
            "The program the command was asked to launch was killed when the command returned.");
    }

    /// <summary>
    /// Holds the output pipe for far longer than the grace period, and writes nothing.
    ///
    /// <para><c>ping</c> rather than a marker file because this test is about the OUTPUT, not about
    /// what survived; there is then nothing to clean up, and the child ends on its own.</para>
    ///
    /// <para>About seven seconds against a two-second grace: enough margin that no machine decides
    /// the outcome, short enough that the child is gone quickly. It does OUTLIVE the test - that is
    /// the behaviour under test - and a rebuild started in those seconds can find the test assembly
    /// locked. That happened while writing this, and the rebuild then silently ran the PREVIOUS
    /// binary, which is a contaminated measurement of exactly the kind this file keeps catching.</para>
    /// </summary>
    private const string HoldsThePipe =
        "@echo off\r\nstart \"\" /b ping -n 8 127.0.0.1\r\necho launched\r\n";

    /// <summary>
    /// A command whose child still holds the output says so, instead of returning a short output
    /// that reads like a complete one.
    ///
    /// <para><b>This assertion used to live in the test above, and it was an assertion about a
    /// race.</b> The note appears when the grace period expires with the pipe still held; that
    /// launcher's child lived about two seconds and the grace is two seconds, so which way it went
    /// depended on the machine. It went one way on the developer's and the other on CI, where it
    /// failed on first contact — the third time in this file that a test has been green for a
    /// reason other than the one in its name.</para>
    ///
    /// <para>So the timing is no longer incidental: the child holds the pipe for about twenty
    /// seconds against a two-second grace. And it is a separate test, because "what survives" and
    /// "what the output admits" are two different claims and a test should fail for one reason.</para>
    /// </summary>
    [Fact]
    public async Task Output_cut_short_by_a_child_still_holding_the_pipe_says_so()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fx = new EngineFixture();
        fx.Write("holds.cmd", HoldsThePipe);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await new RunCommandTool().InvokeAsync(
            Arguments("holds.cmd"), fx.ContextFor(), CancellationToken.None);
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20),
            $"The tool waited {started.Elapsed.TotalSeconds:F1}s for a pipe it should have stopped waiting for.");

        Assert.True(result.Success, $"The command itself succeeded but was reported failed: {result.Error}");

        Assert.Contains("still running", result.Output ?? "", StringComparison.Ordinal);
    }
}
