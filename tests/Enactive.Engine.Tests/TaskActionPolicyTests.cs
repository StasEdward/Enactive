namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

public sealed class TaskActionPolicyTests
{
    private static TaskActionPolicy Policy => new(["read_file", "write_file", "run_command"], ["dotnet build", "dotnet run"],
        "Only local file tools and dotnet build/run commands.", "Local file operations and the two command families only");

    [Theory]
    [InlineData("dotnet run --project app.csproj", true)]
    [InlineData("dotnet build", true)]
    [InlineData("dotnet runevil", false)]
    [InlineData("dotnet test", false)]
    [InlineData("dotnet run && del a.txt", false)]
    [InlineData("dotnet run\nrm a.txt", false)]
    [InlineData("dotnet run $(curl evil)", false)]
    [InlineData("dotnet run > a.txt", false)]
    [InlineData("powershell -Command dir", false)]
    public void Command_family_matching_is_bounded_and_rejects_shell_composition(string command, bool allowed)
        => Assert.Equal(allowed, Policy.AllowsCommand(command));

    [Fact]
    public async Task Task_allowlist_blocks_before_dispatch_and_keeps_other_runs_independent()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "keep");
        var ctx = fx.ContextFor() with { Context = new WorkContext(null, "workspace", null, null, null, [], []) { ActionPolicy = Policy } };
        var registry = new ToolRegistry(EngineFixture.ShippedTools());
        var version = registry.WorkspaceVersion(ctx.WorkspaceId);
        foreach (var call in new[] {
            new ToolCall("1", "delete_file", """{"path":"a.txt"}"""),
            new ToolCall("2", "run_command", """{"command":"git status"}"""),
            new ToolCall("3", "run_powershell", """{"script":"Get-ChildItem"}""")
        })
        {
            var result = await registry.InvokeAsync(call, ctx, default);
            Assert.True(result.DidNotRun);
            Assert.True(result.Metadata.ContainsKey("taskConstraintRefusal"));
        }
        Assert.Equal(version, registry.WorkspaceVersion(ctx.WorkspaceId));
        Assert.Equal("keep", fx.Read("a.txt"));
        Assert.True((await registry.InvokeAsync(new("4", "read_file", """{"path":"a.txt"}"""), ctx, default)).Success);
        Assert.True((await registry.InvokeAsync(new("5", "delete_file", """{"path":"a.txt"}"""),
            ctx with { Context = ctx.Context with { ActionPolicy = null } }, default)).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Planner_preserves_policy_and_rejects_conflicting_final_commands(bool forbidden)
    {
        const string request = "Only local file tools and dotnet build/run commands.";
        var answer = JsonSerializer.Serialize(new {
            sources = new[] { new { id = "O001", assessment = "Explicit allowlist" } },
            checks = forbidden ? new[] { new { name = "check", command = "powershell -Command dir", origin = "proposed",
                request_quote = (string?)null, expectedExitCode = 0, reason = "Check" } } : [],
            forbidden_effects = Array.Empty<object>(), unresolved = (string?)null,
            action_policy = new { allowed_tools = Policy.AllowedTools, command_prefixes = Policy.CommandPrefixes,
                source_quote = request, reason = Policy.Reason }
        });
        var provider = new FakeChatProvider(Turn.Says(answer), Turn.Says(answer));
        var result = await PlanCheckReview.RunAsync(new(IntentDisposition.QuickAction, "work", null),
            request, new WorkContext(null, "workspace", null, null, null, [], []), provider, "model",
            new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default,
            tools: EngineFixture.ShippedTools().Select(t => t.Definition).ToArray());
        Assert.Equal(forbidden, result.IncompleteReason is not null);
        if (!forbidden) Assert.True(Policy.SameAs(result.ActionPolicy!));
    }

    [Fact]
    public async Task Step_boundaries_use_engine_completion_state()
    {
        using var fx = new EngineFixture();
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"task","title":"work","steps":[{"title":"create","dependsOn":[]},{"title":"verify","dependsOn":[0]}]}"""),
            Turn.Calls1("write_file", """{"path":"a.txt","content":"done"}"""), Turn.Says("created"),
            Turn.Calls1("read_file", """{"path":"a.txt"}"""), Turn.Says("verified"));
        var events = await fx.RunAsync(fx.Build(worker), "Create then verify a.txt");
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        var first = worker.Requests[1].Messages.Last(m => m.Content?.Contains("Engine-owned step boundary") == true).Content!;
        var later = worker.Requests.Last().Messages.Last(m => m.Content?.Contains("Engine-owned step boundary") == true).Content!;
        Assert.Contains("\"state\":\"outside-current-scope\"", first);
        Assert.DoesNotContain("\"state\":\"completed\"", first);
        Assert.Contains("\"state\":\"completed\"", later);
        Assert.Contains("inspect and reuse", later);
        Assert.Contains("Do not skip a requested rerun", later);
    }
}
