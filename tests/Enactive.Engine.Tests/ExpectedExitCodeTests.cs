namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Artifacts;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// The second half of the 20:16 log (see <see cref="ReviewScopeTests"/> for the first).
///
/// <para>The agent ran <c>dotnet run --project tests/ParserSmokeTest</c>. It exited 1, because the
/// gap tests inside that smoke test fail — which is the ANSWER, and exactly what the run's third
/// step, "Verify tests fail when behaviour is broken", existed to find out. The engine counted the
/// non-zero exit as an unresolved failure, the step went Incomplete, two steps were skipped, and the
/// run died. That step could never have succeeded.</para>
///
/// <para><see cref="ProcessExec.BuildResult"/> is not wrong to treat a non-zero exit as a failure —
/// "the process started" and "the command succeeded" are different things, and that rule is why a
/// broken build cannot be talked past. It simply cannot tell a broken build from a test runner
/// reporting. <b>The caller can</b>, because it knows what it asked for.</para>
///
/// <para>So the caller declares it, in the arguments, which it writes BEFORE it has seen any exit
/// code or output. That is the whole design: a prediction, not an excuse. Asked afterwards — "was
/// that failure expected?" — anything would be. Written in advance it is a claim, recorded in the
/// evidence with the rest of the arguments, that the reviewer reads next to the output.</para>
///
/// <para>It is a loosening, and these tests pin what stops it becoming a hole: an undeclared
/// non-zero exit still fails, 0 is always success, a malformed declaration is refused before the
/// command runs, and the reviewer is told what a declaration does and does not excuse.</para>
/// </summary>
public sealed class ExpectedExitCodeTests
{
    private static ToolContext Context(EngineFixture fx)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: fx.Artifacts, Services: null!);

    /// <summary>A command that exits with the code we ask it for, on either platform.</summary>
    private static string ExitsWith(int code)
        => OperatingSystem.IsWindows() ? $"exit /b {code}" : $"exit {code}";

    private static Task<ToolResult> Run(EngineFixture fx, string argumentsJson)
        => new RunCommandTool().InvokeAsync(argumentsJson, Context(fx), CancellationToken.None);

    private static IReadOnlyCollection<int>? Declared(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.True(ProcessExec.TryReadExpectedExitCodes(doc.RootElement, out var codes, out var error), error);
        return codes;
    }

    // ── the case from the log ───────────────────────────────────────────────

    /// <summary>
    /// A test runner returning 1 for a failing test, declared in advance: the call succeeded, and
    /// the output — which is the finding — is intact.
    /// </summary>
    [Fact]
    public async Task A_declared_exit_code_is_a_result_not_a_failure()
    {
        using var fx = new EngineFixture();

        var result = await Run(fx, $$"""
            {"command":"{{ExitsWith(1)}}","expectedExitCodes":[0,1]}
            """);

        Assert.True(result.Success, result.Error);
        Assert.Contains("exit code 1", result.Output!, StringComparison.Ordinal);
    }

    /// <summary>And the step it belongs to finishes, which is the whole point.</summary>
    [Fact]
    public async Task A_step_whose_command_reported_failures_can_now_finish()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"verify the tests fail"}"""),
                    Turn.Calls1("run_command", $$"""
                        {"command":"{{ExitsWith(1)}}","expectedExitCodes":[0,1]}
                        """),
                    Turn.Says("Two gap tests fail, as they should when the behaviour is broken.")),
                EngineFixture.Role("developer")),
            "verify the tests fail when the behaviour is broken");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// The declaration is in the evidence. It has to be: the reviewer's only way to tell an honest
    /// prediction from an excuse is to read it next to the output.
    /// </summary>
    [Fact]
    public async Task The_declaration_is_visible_to_the_reviewer()
    {
        using var fx = new EngineFixture();
        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };

        await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"run the tests"}"""),
                    Turn.Calls1("run_command", $$"""
                        {"command":"{{ExitsWith(1)}}","expectedExitCodes":[0,1]}
                        """),
                    Turn.Says("One test fails.")),
                EngineFixture.Role("developer"),
                router: Routers.WithReviewer(), reviewProvider: reviewer),
            "run the tests");

        var prompt = reviewer.Requests.Last().Messages.Last().Content ?? "";
        Assert.Contains("expectedExitCodes", prompt, StringComparison.Ordinal);
        Assert.Contains("exit code 1", prompt, StringComparison.Ordinal);
    }

    // ── what stops it being a hole ──────────────────────────────────────────

    /// <summary>
    /// Undeclared, a non-zero exit is a failure exactly as before. This is the rule the whole change
    /// is a narrow exception to, and it is the one that keeps a broken build from passing.
    /// </summary>
    [Fact]
    public async Task An_undeclared_non_zero_exit_still_fails()
    {
        using var fx = new EngineFixture();

        var result = await Run(fx, $$"""{"command":"{{ExitsWith(1)}}"}""");

        Assert.False(result.Success);
        Assert.Contains("exited with code 1", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>And it still stops the step, which is where it actually bites.</summary>
    [Fact]
    public async Task An_undeclared_failure_still_holds_the_step_open()
    {
        using var fx = new EngineFixture();

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"build it"}"""),
                    Turn.Calls1("run_command", $$"""{"command":"{{ExitsWith(1)}}"}"""),
                    Turn.Says("Build succeeded.")),
                EngineFixture.Role("developer")),
            "build the project");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// A code the caller did NOT declare still fails, even when it declared others. Declaring [0,1]
    /// buys exactly 1 — not "non-zero is fine from now on".
    /// </summary>
    [Fact]
    public async Task A_code_outside_the_declaration_still_fails()
    {
        using var fx = new EngineFixture();

        var result = await Run(fx, $$"""
            {"command":"{{ExitsWith(2)}}","expectedExitCodes":[0,1]}
            """);

        Assert.False(result.Success);
        Assert.Contains("exited with code 2", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// 0 is success whatever was declared. A declaration can widen what counts as done; it can never
    /// narrow it, so no wording can turn a clean run into a failure.
    /// </summary>
    [Fact]
    public async Task Zero_is_success_however_odd_the_declaration()
    {
        using var fx = new EngineFixture();

        var result = await Run(fx, $$"""
            {"command":"{{ExitsWith(0)}}","expectedExitCodes":[3]}
            """);

        Assert.True(result.Success, result.Error);
        Assert.Contains(0, Declared("""{"expectedExitCodes":[3]}""")!);
    }

    [Fact]
    public void An_absent_declaration_leaves_the_default_alone()
        => Assert.Null(Declared("""{"command":"dotnet build"}"""));

    [Fact]
    public void An_empty_declaration_is_just_the_default()
        => Assert.Equal(new[] { 0 }, Declared("""{"expectedExitCodes":[]}""")!.OrderBy(x => x));

    // ── a malformed declaration is refused, not guessed at ──────────────────

    /// <summary>
    /// Refused BEFORE the command runs. A tool that ran the command and then complained about the
    /// arguments would have had its effect already — and the effect is the part that matters.
    /// </summary>
    [Theory]
    [InlineData("""{"command":"echo hi","expectedExitCodes":"0,1"}""")]
    [InlineData("""{"command":"echo hi","expectedExitCodes":1}""")]
    [InlineData("""{"command":"echo hi","expectedExitCodes":["0","1"]}""")]
    [InlineData("""{"command":"echo hi","expectedExitCodes":[0,"one"]}""")]
    public async Task A_declaration_that_is_not_a_list_of_integers_is_refused(string arguments)
    {
        using var fx = new EngineFixture();

        var result = await Run(fx, arguments);

        Assert.False(result.Success);
        Assert.Contains("expectedExitCodes", result.Error!, StringComparison.Ordinal);
        Assert.Contains("integer", result.Error!, StringComparison.OrdinalIgnoreCase);
        // The command never ran, so there is no output to report.
        Assert.True(string.IsNullOrEmpty(result.Output), result.Output);
    }

    [Fact]
    public void Null_is_not_a_malformed_declaration()
        => Assert.Null(Declared("""{"expectedExitCodes":null}"""));

    // ── the model and the reviewer are both told what it means ──────────────

    /// <summary>
    /// The description carries the guidance, not just the field. A model told only that the option
    /// exists will reach for it whenever something comes back red.
    /// </summary>
    [Theory]
    [InlineData("run_command")]
    [InlineData("run_powershell")]
    public void The_tool_says_when_this_is_legitimate_and_when_it_is_not(string toolName)
    {
        var tool = EngineFixture.ShippedTools().Single(t => t.Definition.Name == toolName);
        var described = tool.Definition.Description + "\n" + tool.Definition.JsonSchema;

        Assert.Contains("expectedExitCodes", described, StringComparison.Ordinal);
        Assert.Contains("test runner", described, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never to excuse", described, StringComparison.OrdinalIgnoreCase);
        // And the schema is still valid JSON after being composed from two pieces.
        using var schema = JsonDocument.Parse(tool.Definition.JsonSchema);
        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("expectedExitCodes", out _));
    }

}
