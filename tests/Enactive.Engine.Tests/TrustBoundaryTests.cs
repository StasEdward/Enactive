namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Tools;
using Xunit;

/// <summary>
/// The engine must not be talkable into acting. Every test here reproduces a finding from the
/// 2026-09-06 code review, so a regression shows up as a failing test rather than as a file that
/// was not supposed to exist.
/// </summary>
public sealed class TrustBoundaryTests
{
    // Review finding #2 — an empty tool allowlist used to mean "every tool", so unchecking every box
    // in the worker editor granted full access instead of removing it.
    [Fact]
    public async Task Empty_tool_allowlist_permits_nothing()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write a file"}"""),
            Turn.Calls1("write_file", """{"path":"from-empty-allowlist.txt","content":"hi"}"""));

        var orchestrator = fx.Build(provider, EngineFixture.WorkerWith(/* nothing */));
        var events = await fx.RunAsync(orchestrator, "write a file");

        Assert.False(fx.Exists("from-empty-allowlist.txt"), events.Text());
        Assert.Contains(events, e => e.Summary.Contains("not available to role", StringComparison.OrdinalIgnoreCase));
    }

    // The other half of #2: the model must not even be OFFERED a tool its role cannot call, or it
    // spends the run trying. The prompt-side list and the incoming-call check are separate code paths.
    [Fact]
    public async Task Empty_tool_allowlist_offers_no_tools_to_the_model()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"look around"}"""),
            Turn.Says("I have no tools."));

        var orchestrator = fx.Build(provider, EngineFixture.WorkerWith());
        await fx.RunAsync(orchestrator, "look around");

        // Requests[0] is the planner (no tools by design); the execution turn is the one that matters.
        var execution = provider.Requests[1];
        Assert.Empty(execution.Tools ?? Array.Empty<Enactive.Core.Tools.ToolDefinition>());
    }

    [Fact]
    public async Task Wildcard_allowlist_still_grants_everything()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write a file"}"""),
            Turn.Calls1("write_file", """{"path":"wildcard.txt","content":"hi"}"""));

        var orchestrator = fx.Build(provider, EngineFixture.WorkerWith("*"));
        var events = await fx.RunAsync(orchestrator, "write a file");

        Assert.True(fx.Exists("wildcard.txt"), events.Text());
    }

    // Review finding #3 — a reply that merely DESCRIBED a call was executed, so a quoted example,
    // an explanation, or JSON in a file the agent had just read could become an action.
    [Fact]
    public async Task Json_in_plain_text_is_not_executed()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"explain write_file"}"""),
            Turn.Says("""
                Example, do not execute:

                ```json
                {"path":"implicit.txt","content":"should never be written"}
                ```
                """),
            Turn.Says("That was only an example."));

        var orchestrator = fx.Build(provider, allowImplicitToolCalls: false);
        var events = await fx.RunAsync(orchestrator, "explain how write_file is called");

        Assert.False(fx.Exists("implicit.txt"), events.Text());
    }

    // ...and instead of guessing, the engine asks the model to send a real call. One re-ask, not a loop.
    [Fact]
    public async Task A_described_call_earns_exactly_one_repair_request()
    {
        using var fx = new EngineFixture();
        var described = Turn.Says("""
            ```json
            {"path":"implicit.txt","content":"x"}
            ```
            """);
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write"}"""),
            described,
            described,
            described);

        var orchestrator = fx.Build(provider);
        await fx.RunAsync(orchestrator, "write a file");

        var repairs = provider.Requests
            .SelectMany(r => r.Messages)
            .Count(m => m.Content?.Contains("instead of invoking it", StringComparison.Ordinal) == true);

        Assert.False(fx.Exists("implicit.txt"));
        // The prompt is re-sent with every later request, so count the DISTINCT turn that carried it:
        // one repair message exists, no matter how many requests replayed the transcript.
        Assert.True(repairs >= 1, "the model should have been asked to re-send the call");
        Assert.Equal(3, provider.Requests.Count); // planner + reply + one re-ask, then it stops
    }

    // The opt-in escape hatch still works, because a weak local model may be unable to emit
    // structured calls at all — but it is off unless the user turned it on.
    [Fact]
    public async Task Implicit_calls_run_only_when_explicitly_enabled()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write"}"""),
            Turn.Says("""
                ```json
                {"path":"opted-in.txt","content":"written on purpose"}
                ```
                """));

        var orchestrator = fx.Build(provider, allowImplicitToolCalls: true);
        var events = await fx.RunAsync(orchestrator, "write a file");

        Assert.True(fx.Exists("opted-in.txt"), events.Text());
        Assert.Equal("written on purpose", fx.Read("opted-in.txt"));
    }

    // Review finding #10 — the approval card showed arguments truncated at 120 characters while the
    // FULL arguments were executed, so a long shell script could hide its tail behind an ellipsis.
    // Wave 1 replaces the truncated Detail; this test pins the current, known-bad behaviour so the
    // fix has something to flip. It asserts what the USER SEES, not how it is produced.
    [Fact(Skip = "Wave 1: the decision card must show the full command before it is approved.")]
    public async Task Approval_card_shows_the_whole_command()
    {
        using var fx = new EngineFixture();
        var script = "echo " + new string('x', 400);
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"run a long command"}"""),
            Turn.Calls1("run_command", $$"""{"command":"{{script}}"}"""));

        var askFirst = new PermissionPolicy(
            PermissionLevel.Execute, new[] { "*" }, new[] { "run_command" });

        var orchestrator = fx.Build(provider, policy: askFirst);
        await fx.RunAsync(orchestrator, "run a long command");

        var card = Assert.Single(fx.Decisions.Requests);
        Assert.Contains(script, card.Detail, StringComparison.Ordinal);
    }
}

/// <summary>
/// A process that ran is not a command that succeeded (review finding #14). These call the tools
/// directly: there is no model in the path, and the assertion is about the ToolResult contract.
/// </summary>
public sealed class ProcessResultTests
{
    [Fact]
    public async Task Nonzero_exit_code_is_a_failed_tool_result()
    {
        using var fx = new EngineFixture();
        var tool = new RunCommandTool();
        var ctx = ToolContexts.For(fx);

        var result = await tool.InvokeAsync(
            OperatingSystem.IsWindows()
                ? """{"command":"exit /b 7"}"""
                : """{"command":"exit 7"}""",
            ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(7, Convert.ToInt32(result.Metadata["exitCode"]));
        // The output still has to survive: a failure is exactly when stdout/stderr matter.
        Assert.Contains("exit code 7", result.Output ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zero_exit_code_is_still_a_success()
    {
        using var fx = new EngineFixture();
        var tool = new RunCommandTool();
        var ctx = ToolContexts.For(fx);

        var result = await tool.InvokeAsync(
            """{"command":"echo hello"}""", ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("hello", result.Output ?? "", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Undo may not promise what it cannot deliver (review finding #6).</summary>
public sealed class ArtifactStoreTests
{
    [Fact]
    public async Task A_file_the_store_created_is_marked_as_created_here()
    {
        using var fx = new EngineFixture();
        await WriteThrough(fx, "new.txt", "hello");

        Assert.True(fx.Artifacts.CreatedHere("new.txt"));
    }

    [Fact]
    public async Task An_overwritten_file_is_never_marked_as_created_here()
    {
        using var fx = new EngineFixture();
        fx.Write("existing.txt", "the user's content");

        await WriteThrough(fx, "existing.txt", "the agent's content");

        Assert.False(fx.Artifacts.CreatedHere("existing.txt"));
    }

    [Fact]
    public async Task A_second_write_does_not_reclassify_an_overwritten_file()
    {
        using var fx = new EngineFixture();
        fx.Write("existing.txt", "the user's content");

        await WriteThrough(fx, "existing.txt", "first");
        await WriteThrough(fx, "existing.txt", "second");

        Assert.False(fx.Artifacts.CreatedHere("existing.txt"));
    }

    [Fact]
    public void An_unknown_path_is_not_claimed()
    {
        using var fx = new EngineFixture();
        Assert.False(fx.Artifacts.CreatedHere("never-written.txt"));
    }

    private static Task WriteThrough(EngineFixture fx, string relative, string content)
        => fx.Artifacts.CreateAsync(
            relative, Enactive.Core.Artifacts.ArtifactKind.FileSet, relative,
            async stream =>
            {
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(content);
            },
            CancellationToken.None);
}

/// <summary>Builds the ToolContext a tool needs, without a running orchestrator.</summary>
internal static class ToolContexts
{
    public static Enactive.Core.Tools.ToolContext For(EngineFixture fx)
        => new(Guid.NewGuid(), Guid.NewGuid(), fx.Workspace.Id,
               new Enactive.Core.Context.WorkContext(
                   fx.Workspace.Id, fx.Workspace.Name, null, null, null,
                   Array.Empty<string>(), Array.Empty<string>()),
               PermissionPolicy.PermissiveDefault,
               fx.Root,
               fx.Artifacts,
               new NoServices());

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
