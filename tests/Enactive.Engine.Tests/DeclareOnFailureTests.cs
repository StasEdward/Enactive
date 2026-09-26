namespace Enactive.Engine.Tests;

using Enactive.Core.Artifacts;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// The first run after <c>expectedExitCodes</c> shipped, 2026-09-07 20:49. It was not used once.
///
/// <para>Step 1 passed — the review-scope fix held, and the reviewer said so: "The report is
/// consistent with the evidence provided." Step 2 wrote the gap tests, ran the test project three
/// times, and every call was a bare <c>{"command":"dotnet run --project …"}</c>. Exit 1 each time,
/// step Incomplete, step 3 skipped, run failed.</para>
///
/// <para>And the model's own closing words were:</para>
///
/// <para><c>"The fallback parsing failure specifically confirms that the behavior I identified as a
/// gap is indeed broken or not working as expected, satisfying the requirement to implement tests
/// that fail if the behavior is broken."</c></para>
///
/// <para><b>It knew the failure was the answer. It said so in prose. It did not say so in the
/// arguments</b> — because nothing asked it to at the moment it wrote them, and a schema property is
/// not read at the moment a command is typed. Shipping the mechanism was not shipping its use.</para>
///
/// <para>Two things were missing, and this file is about both:</para>
///
/// <para>1. <b>The failure has to say how.</b> That is the pattern every other correction here
/// follows — the git argument shape, the CRLF edit, the denied tool: put the fix in the message the
/// model is reading at the moment it is stuck, not only in a schema it read once.</para>
///
/// <para>2. <b>The advice has to actually work.</b> A call's identity was its name plus its
/// arguments verbatim, so running the same command again WITH the declaration made a different call
/// — the first failure stayed unresolved and the step died regardless. The advice would have been a
/// dead end. "expectedExitCodes" says how to read a result, not what to do, so it is not part of
/// what makes a call that call.</para>
/// </summary>
public sealed class DeclareOnFailureTests
{
    private static ToolContext Context(EngineFixture fx)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: fx.Artifacts, Services: null!);

    private static string ExitsWith(int code)
        => OperatingSystem.IsWindows() ? $"exit /b {code}" : $"exit {code}";

    private static Task<ToolResult> Run(EngineFixture fx, string argumentsJson)
        => new RunCommandTool().InvokeAsync(argumentsJson, Context(fx), CancellationToken.None);

    // ── the failure says how ────────────────────────────────────────────────

    /// <summary>
    /// The one that matters. A model that has just been handed "exited with code 1" and nothing
    /// else has no reason to reach for an option it read once in a schema.
    /// </summary>
    [Fact]
    public async Task A_failing_command_says_how_to_declare_the_code_it_returned()
    {
        using var fx = new EngineFixture();

        var result = await Run(fx, $$"""{"command":"{{ExitsWith(1)}}"}""");

        Assert.False(result.Success);
        Assert.Contains("expectedExitCodes", result.Error!, StringComparison.Ordinal);
        // The code it ACTUALLY got, so the advice can be followed without inventing anything.
        Assert.Contains("[0, 1]", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>With the code it actually returned, whatever that was.</summary>
    [Fact]
    public async Task The_advice_names_the_code_that_was_actually_returned()
    {
        using var fx = new EngineFixture();

        var result = await Run(fx, $$"""{"command":"{{ExitsWith(3)}}"}""");

        Assert.Contains("[0, 3]", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it says when NOT to. An option offered on every failure, with no counterweight, is an
    /// invitation to make every failure go away.
    /// </summary>
    [Fact]
    public async Task The_advice_says_when_not_to_take_it()
    {
        using var fx = new EngineFixture();

        var result = await Run(fx, $$"""{"command":"{{ExitsWith(1)}}"}""");

        Assert.Contains("do NOT", result.Error!, StringComparison.Ordinal);
        Assert.Contains("fix the cause", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Not repeated to somebody who already declared and still failed. There it is noise, and worse:
    /// it reads as "declare more".
    /// </summary>
    [Fact]
    public async Task A_caller_that_already_declared_is_not_told_to_declare_again()
    {
        using var fx = new EngineFixture();

        var result = await Run(fx, $$"""
            {"command":"{{ExitsWith(2)}}","expectedExitCodes":[0,1]}
            """);

        Assert.False(result.Success);
        Assert.DoesNotContain("expectedExitCodes", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it is not offered by tools that cannot honour it. git and docker go through the same
    /// result builder and take no such argument; advice they cannot act on is a false trail.
    /// </summary>
    [Fact]
    public void A_tool_that_cannot_take_the_declaration_does_not_advertise_it()
    {
        var result = ProcessExec.BuildResult("git", 1, "", "not a git repository");

        Assert.False(result.Success);
        Assert.DoesNotContain("expectedExitCodes", result.Error!, StringComparison.Ordinal);
    }

    // ── and the advice actually works ───────────────────────────────────────

    /// <summary>
    /// Run, fail, run again with the declaration: the step finishes. Without the identity fix this
    /// is exactly what a model following the advice would do, and the step would die anyway.
    /// </summary>
    [Fact]
    public async Task Following_the_advice_resolves_the_failure_it_was_given_for()
    {
        using var fx = new EngineFixture();
        var command = ExitsWith(1);

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"run the tests"}"""),
                    Turn.Calls1("run_command", $$"""{"command":"{{command}}"}"""),
                    Turn.Calls1("run_command", $$"""
                        {"command":"{{command}}","expectedExitCodes":[0,1]}
                        """, "c2"),
                    Turn.Says("Four gap tests fail; that is the finding.")),
                EngineFixture.Role("developer")),
            "run the gap tests");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// The identity rule underneath it, stated on its own: the declaration is not part of what makes
    /// a call that call.
    /// </summary>
    [Fact]
    public void A_command_and_the_same_command_with_a_declaration_are_one_call()
    {
        var plain = new ToolCall("a", "run_command", """{"command":"dotnet test"}""");
        var declared = new ToolCall("b", "run_command", """{"command":"dotnet test","expectedExitCodes":[0,1]}""");

        Assert.Equal(KeyOf(plain), KeyOf(declared));
    }

    /// <summary>Which is not the same as making everything equal.</summary>
    [Fact]
    public void Two_different_commands_are_still_two_calls()
    {
        var one = new ToolCall("a", "run_command", """{"command":"dotnet test","expectedExitCodes":[0,1]}""");
        var two = new ToolCall("b", "run_command", """{"command":"dotnet build","expectedExitCodes":[0,1]}""");

        Assert.NotEqual(KeyOf(one), KeyOf(two));
    }

    /// <summary>
    /// A free consequence of canonicalising: the same call with its properties written in a
    /// different order was always the same call, and now reads as one.
    /// </summary>
    [Fact]
    public void Property_order_is_not_part_of_a_calls_identity()
    {
        var one = new ToolCall("a", "edit_file", """{"path":"a.cs","old_string":"x","new_string":"y"}""");
        var two = new ToolCall("b", "edit_file", """{"new_string":"y","path":"a.cs","old_string":"x"}""");

        Assert.Equal(KeyOf(one), KeyOf(two));
    }

    /// <summary>Arguments that will not parse are still their own identity, not all one identity.</summary>
    [Fact]
    public void Unparseable_arguments_are_still_told_apart()
    {
        var one = new ToolCall("a", "run_command", "{ not json");
        var two = new ToolCall("b", "run_command", "{ also not json");

        Assert.NotEqual(KeyOf(one), KeyOf(two));
        Assert.Equal(KeyOf(one), KeyOf(new ToolCall("c", "run_command", "{ not json")));
    }

    /// <summary>Different tools are never the same call, whatever their arguments look like.</summary>
    [Fact]
    public void The_tool_name_is_still_part_of_the_identity()
        => Assert.NotEqual(
            KeyOf(new ToolCall("a", "run_command", """{"command":"x"}""")),
            KeyOf(new ToolCall("b", "run_powershell", """{"command":"x"}""")));

    /// <summary>
    /// The engine's spelling of the argument and the tool's are the same string. Two spellings would
    /// mean the stripping silently stops working, and nothing would fail loudly.
    /// </summary>
    [Fact]
    public void The_engine_and_the_tool_agree_on_the_name()
        => Assert.Equal(ToolArguments.ExpectedExitCodes, ProcessExec.ExpectedExitCodes);

    // ── what must not change ────────────────────────────────────────────────

    /// <summary>
    /// A repeated identical call is still a repeat. The stall detector shares this identity, and a
    /// canonicalisation that made every call unique would switch it off.
    /// </summary>
    [Fact]
    public void The_same_call_twice_is_still_the_same_call()
        => Assert.Equal(
            KeyOf(new ToolCall("a", "read_file", """{"path":"x.cs"}""")),
            KeyOf(new ToolCall("b", "read_file", """{"path":"x.cs"}""")));

    /// <summary>
    /// And declaring a code does not resolve a DIFFERENT command's failure. The first call has to be
    /// the one that was re-run.
    /// </summary>
    [Fact]
    public async Task Declaring_on_one_command_does_not_clear_another()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"build and test"}"""),
                    Turn.Calls1("run_command", $$"""{"command":"{{ExitsWith(1)}}"}"""),
                    Turn.Calls1("run_command", $$"""
                        {"command":"{{ExitsWith(0)}}","expectedExitCodes":[0,1]}
                        """, "c2"),
                    Turn.Says("Done.")),
                EngineFixture.Role("developer")),
            "build and test");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }

    private static string KeyOf(ToolCall call) => Enactive.Agents.CallIdentity.Of(call);
}
