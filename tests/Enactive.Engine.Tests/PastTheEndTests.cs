namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Reported with a log, 2026-09-07 21:35 — the run where the template paragraph worked and the model
/// declared <c>"expectedExitCodes":[0,1]</c> on its very first test run. Step 1 was still marked
/// Incomplete, and both remaining steps skipped, for two calls of this shape:
///
/// <para><c>read_file {"offset":800,"path":"tests/ParserSmokeTest/Program.cs"}</c> —
/// <i>'tests/ParserSmokeTest/Program.cs' has 263 line(s); offset 800 is past the end.</i></para>
///
/// <para>Both came immediately after a SUCCESSFUL read of that same file. The model was paging: read
/// the window, then ask whether anything follows it. The answer — "no, the file is 263 lines" — is
/// exactly the information it wanted, and it moved on. Nothing was unfinished and there was nothing
/// to retry.</para>
///
/// <para>This is §9e in a shape it did not cover. A missing file answers; a folder that is not there
/// answers; and so does an offset past the end, which is the only way to learn a file's length
/// without reading it. The failure is kept — the message names the real line count, which is what
/// makes it useful — and only the "this step is unfinished" judgement changes.</para>
/// </summary>
public sealed class PastTheEndTests
{
    private static ToolContext Context(EngineFixture fx)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: fx.Artifacts);

    private static Task<ToolResult> Read(EngineFixture fx, string argumentsJson)
        => new ReadFileTool().InvokeAsync(argumentsJson, Context(fx), CancellationToken.None);

    private static void WriteLines(EngineFixture fx, string name, int lines)
        => fx.Write(name, string.Join("\n", Enumerable.Range(1, lines).Select(i => $"line {i}")));

    // ── the defect ──────────────────────────────────────────────────────────

    /// <summary>An offset past the end answers: it did not succeed, and it is not unfinished.</summary>
    [Fact]
    public async Task Reading_past_the_end_answers()
    {
        using var fx = new EngineFixture();
        WriteLines(fx, "Program.cs", 263);

        var result = await Read(fx, """{"path":"Program.cs","offset":800}""");

        Assert.False(result.Success);
        Assert.True(result.IsAnswer, result.Error);
        // And it says how long the file actually is, which is what the caller was asking.
        Assert.Contains("has 263 line(s)", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reported step: read the file, ask whether there is more, be told there is not, carry on.
    /// That must finish.
    /// </summary>
    [Fact]
    public async Task Paging_off_the_end_of_a_file_does_not_fail_the_step()
    {
        using var fx = new EngineFixture();
        WriteLines(fx, "Program.cs", 263);

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"read the tests"}"""),
                    Turn.Calls1("read_file", """{"path":"Program.cs"}"""),
                    Turn.Calls1("read_file", """{"path":"Program.cs","offset":800}""", "c2"),
                    Turn.Says("Program.cs is 263 lines; I have all of it.")),
                EngineFixture.Role("developer")),
            "read the test file");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    // ── what must not change ────────────────────────────────────────────────

    /// <summary>
    /// A window that simply runs out of file is a SUCCESS, not an answer-shaped failure. Asking for
    /// 400 lines of a 263-line file gets 263 lines.
    /// </summary>
    [Fact]
    public async Task A_window_that_reaches_the_end_still_succeeds()
    {
        using var fx = new EngineFixture();
        WriteLines(fx, "Program.cs", 263);

        var result = await Read(fx, """{"path":"Program.cs","offset":200,"limit":400}""");

        Assert.True(result.Success, result.Error);
        Assert.False(result.IsAnswer);
        Assert.Contains("line 263", result.Output!, StringComparison.Ordinal);
    }

    /// <summary>The last line is inside the file, not past it.</summary>
    [Fact]
    public async Task The_last_line_is_readable()
    {
        using var fx = new EngineFixture();
        WriteLines(fx, "Program.cs", 263);

        var result = await Read(fx, """{"path":"Program.cs","offset":263,"limit":1}""");

        Assert.True(result.Success, result.Error);
    }

    /// <summary>
    /// A step whose ENTIRE record is answers like this one still does not count as finished — the
    /// §9e condition, which is what keeps "read something, be told no, say Done" from passing.
    /// </summary>
    [Fact]
    public async Task A_step_that_only_read_past_the_end_has_not_finished()
    {
        using var fx = new EngineFixture();
        WriteLines(fx, "Program.cs", 263);

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"read the tests"}"""),
                    Turn.Calls1("read_file", """{"path":"Program.cs","offset":800}"""),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "read the test file");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>A read that failed for any other reason is a failure like any other.</summary>
    [Fact]
    public async Task A_read_that_escapes_the_workspace_is_still_a_failure()
    {
        using var fx = new EngineFixture();

        var result = await Read(fx, """{"path":"../../secrets.txt","offset":800}""");

        Assert.False(result.Success);
        Assert.False(result.IsAnswer, "a refused path is not a short file");
    }
}
