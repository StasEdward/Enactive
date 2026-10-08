namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// A script Windows will not accept, and a process that never started.
///
/// <para><b>What happened, 2026-09-24 01:18.</b> A step was writing a drift report. It rewrote the
/// whole file with <c>write_file</c> ten times; twice the shrink guard refused it, naming
/// <c>edit_file</c> as the way to change part of a file. It did not take that route. It put the
/// whole 12 KB report in a PowerShell here-string instead — and <c>run_powershell</c> sends its
/// script as <c>-EncodedCommand</c>, base64 of UTF-16, about 2.7x the characters. Windows answered:
/// </para>
///
/// <code>
/// An error occurred trying to start process 'powershell.exe' …
/// The filename or extension is too long.
/// </code>
///
/// <para>A message about a filename, for a script with no filename in it. The process never
/// started, and the step was marked INCOMPLETE for it, taking the other three steps with it.</para>
///
/// <para>Two things were wrong and both are fixed here: a process that will not START is a call
/// that never ran (§9ap's rule, which `ProcessExec` applied to shells refusing words and not to
/// itself refusing to launch), and a limit worth naming is better named before it is crossed.</para>
/// </summary>
public sealed class AScriptTooLongToRunTests
{
    private static ToolContext Context(EngineFixture fx)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: fx.Artifacts);

    /// <summary>The reported shape: a document carried inside a command line.</summary>
    [Fact]
    public async Task A_report_sent_as_a_here_string_is_refused_before_windows_sees_it()
    {
        using var fx = new EngineFixture();

        var report = new string('x', 13_000);
        var script = "$c = @'\n# DRIFT report\n" + report + "\n'@\nSet-Content Docs/DRIFT.md $c";

        var result = await new RunPowerShellTool().InvokeAsync(
            System.Text.Json.JsonSerializer.Serialize(new { script }),
            Context(fx), CancellationToken.None);

        Assert.False(result.Success);

        // Nothing ran, so the step is not left holding it.
        Assert.True(result.DidNotRun, result.Error);

        // And the route it should have taken is named, because "too long" on its own sends a model
        // to make the script shorter rather than to stop carrying a document in it.
        Assert.Contains("write_file", result.Error, StringComparison.Ordinal);
        Assert.Contains("Nothing ran", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BOUNDARY. An ordinary script is untouched — the limit is Windows', not ours, and a
    /// check that fires early would be a new failure invented to prevent an old one.
    /// </summary>
    [Fact]
    public async Task An_ordinary_script_still_runs()
    {
        using var fx = new EngineFixture();

        var result = await new RunPowerShellTool().InvokeAsync(
            """{"script":"Write-Output 'hello'"}""", Context(fx), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("hello", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the general rule behind it: a process that cannot be STARTED never ran. A binary that
    /// is not installed is the same thing wearing a different hat - and it used to be a failure
    /// the step carried to the end, which is how a missing tool could sink a run that had worked
    /// around its absence.
    /// </summary>
    [Fact]
    public async Task A_binary_that_is_not_there_never_ran()
    {
        using var fx = new EngineFixture();

        var result = await ProcessExec.RunAsync(
            "no-such-binary-xkcd-927", new[] { "--version" }, fx.Root, 10, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.DidNotRun, result.Error);
        Assert.Contains("could not be STARTED", result.Error, StringComparison.Ordinal);
    }
}
