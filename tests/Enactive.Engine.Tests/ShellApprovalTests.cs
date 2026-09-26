namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// A shell is a different kind of permission, and was granted by the same button as reading a file.
///
/// <para>The workspace root is the working DIRECTORY of <c>run_command</c>, not a boundary. Every
/// file tool and both artifact stores resolve through <c>WorkspaceGuard</c>, which refuses a path
/// that leaves the root and follows links to find out; a shell is handed a string and the operating
/// system does the rest. That is what a shell is, and inspecting the command text for escapes is
/// the guard that is stepped around by writing <c>./x</c> instead of <c>x</c> — this file does not
/// pretend to close it.</para>
///
/// <para>What it does close is the approval. "Allow (workspace)" for <c>run_command</c> was
/// unlimited command execution on that machine, from one click, for as long as the workspace
/// exists — and afterwards the timeline showed a decision requested and allowed for every call, as
/// though somebody were answering. Both halves are here: the grant cannot outlive the process, and
/// the event says who answered.</para>
/// </summary>
public sealed class ShellApprovalTests
{
    private const string QuickPlan = """{"disposition":"quick_action","title":"run it","steps":[]}""";

    private static PermissionPolicy AsksBefore(params string[] tools)
        => new(PermissionLevel.Execute, new[] { "*" }, tools);

    // -- what counts as a shell ----------------------------------------------

    [Theory]
    [InlineData("run_command", true)]
    [InlineData("run_powershell", true)]
    [InlineData("RUN_COMMAND", true)]
    [InlineData("read_file", false)]
    [InlineData("write_file", false)]
    // Git arguments can invoke external programs, including without an explicit -c argument.
    [InlineData("git", true)]
    [InlineData("GIT", true)]
    [InlineData("docker", true)]
    public void The_shells_are_the_tools_handed_a_command_line(string tool, bool isShell)
        => Assert.Equal(isShell, ShellTools.IsShell(tool));

    // -- the grant cannot outlive the process --------------------------------

    /// <summary>
    /// The request itself says so, so a UI cannot offer the button by forgetting to ask. Everything
    /// else keeps the option it always had.
    /// </summary>
    [Fact]
    public async Task A_shell_asks_for_an_approval_that_may_not_be_remembered()
    {
        using var fx = new EngineFixture();

        var worker = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", """{"command":"echo hello"}""", "c1"))
        {
            WhenExhausted = Turn.Says("ran it")
        };

        var decisions = new ScriptedDecisionHandler("allow");

        await fx.RunAsync(
            fx.Build(worker,
                     worker: EngineFixture.WorkerWith("run_command", "read_file"),
                     policy: AsksBefore("run_command"),
                     decisions: decisions),
            "run the thing");

        var request = Assert.Single(decisions.Requests);
        Assert.True(request.SessionOnly);
        Assert.False(request.MayBeRemembered);
    }

    /// <summary>And a tool that is not a shell is unchanged — the button is still there for it.</summary>
    [Fact]
    public async Task A_tool_that_is_not_a_shell_may_still_be_remembered()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "hello");

        var worker = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("read_file", """{"path":"notes.md"}""", "r1"))
        {
            WhenExhausted = Turn.Says("read it")
        };

        var decisions = new ScriptedDecisionHandler("allow");

        await fx.RunAsync(
            fx.Build(worker,
                     worker: EngineFixture.WorkerWith("read_file"),
                     policy: AsksBefore("read_file"),
                     decisions: decisions),
            "read the notes");

        var request = Assert.Single(decisions.Requests);
        Assert.False(request.SessionOnly);
        Assert.True(request.MayBeRemembered);
    }

    // -- the event says who answered -----------------------------------------

    /// <summary>
    /// A standing approval and a person clicking Allow produced the same line, separated only by
    /// how long it took. On 2026-09-08 that cost a wrong diagnosis: a run was believed to have
    /// failed for want of permissions, and every one of its decisions had in fact been answered by
    /// an approval granted minutes earlier.
    /// </summary>
    [Fact]
    public async Task The_timeline_says_when_a_decision_was_answered_by_a_memory()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "hello");

        var worker = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("read_file", """{"path":"notes.md"}""", "r1"))
        {
            WhenExhausted = Turn.Says("read it")
        };

        var decisions = new ScriptedDecisionHandler("allow")
        {
            Because = "remembered for this workspace"
        };

        var events = await fx.RunAsync(
            fx.Build(worker,
                     worker: EngineFixture.WorkerWith("read_file"),
                     policy: AsksBefore("read_file"),
                     decisions: decisions),
            "read the notes");

        var resolved = Assert.Single(events, e => e.Kind == EventKind.DecisionResolved);
        Assert.Contains("allowed (remembered for this workspace)", resolved.Summary, StringComparison.Ordinal);
    }

    /// <summary>A live answer says nothing extra: there is nothing to explain about a person clicking.</summary>
    [Fact]
    public async Task A_live_answer_adds_no_explanation()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "hello");

        var worker = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("read_file", """{"path":"notes.md"}""", "r1"))
        {
            WhenExhausted = Turn.Says("read it")
        };

        var events = await fx.RunAsync(
            fx.Build(worker,
                     worker: EngineFixture.WorkerWith("read_file"),
                     policy: AsksBefore("read_file"),
                     decisions: new ScriptedDecisionHandler("allow")),
            "read the notes");

        var resolved = Assert.Single(events, e => e.Kind == EventKind.DecisionResolved);
        Assert.Equal("read_file: allowed", resolved.Summary);
    }
}
