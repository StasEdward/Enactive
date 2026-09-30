namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;

public sealed class ToolPurposeTests
{
    [Fact]
    public async Task Registry_decorators_preserve_metadata_and_unknown_stays_unknown()
    {
        using var fx = new EngineFixture();
        var definition = new RunCommandTool().Definition with { Name = "custom_check" };
        var registry = new ToolRegistry([new Stub(definition)]);
        var logged = new LoggingToolRegistry(registry, Enactive.Core.Diagnostics.NullLogSink.Instance);
        await using var combined = await Enactive.Tools.Mcp.McpRunTools.ConnectAsync(logged, [], fx.Root, default);
        Assert.Same(definition, combined.DefinitionOf("CUSTOM_CHECK"));
        Assert.Null(combined.DefinitionOf("run_command"));
        Assert.Null(combined.DefinitionOf("mcp__unknown"));
        var unspecified = new ToolDefinition("run_command", "test", "{}");
        Assert.Equal(ToolKind.Unknown, unspecified.Kind);
        Assert.False(unspecified.RunsSuccessChecks);
    }

    [Fact]
    public void Renamed_file_tools_keep_coverage_move_and_delete_behavior()
    {
        var ledger = new ReadLedger();
        var read = new ReadFileTool().Definition with { Name = "peek" };
        var write = new WriteFileTool().Definition with { Name = "replace" };
        ledger.Saw(new("r", "peek", "{}"), ToolResults.Ok(metadata: new Dictionary<string, object?>
            { ["path"] = "a", ["firstLine"] = 1, ["lastLine"] = 2, ["totalLines"] = 10 }), read);
        Assert.NotNull(ledger.Refuse(new("w", "replace", "{}"), "a", write));
        ledger.Saw(new("m", "rename", """{"from":"a","to":"b"}"""), ToolResults.Ok(),
            new MoveFileTool().Definition with { Name = "rename" });
        Assert.Null(ledger.Refuse(new("w", "replace", "{}"), "a", write));
        Assert.NotNull(ledger.Refuse(new("w", "replace", "{}"), "b", write));
        ledger.Saw(new("d", "remove", """{"path":"b"}"""), ToolResults.Ok(),
            new DeleteFileTool().Definition with { Name = "remove" });
        Assert.Null(ledger.Refuse(new("w", "replace", "{}"), "b", write));
    }

    [Theory]
    [InlineData(ToolKind.Command, "custom_check", 1)]
    [InlineData(ToolKind.Unknown, "run_command", 2)]
    public async Task Repeat_gate_uses_kind_not_spelling(ToolKind kind, string name, int executions)
    {
        using var fx = new EngineFixture();
        var tool = new Stub(new(name, "test", "{}", WorkspaceEffect.None, Kind: kind));
        fx.ToolsOverride = [tool];
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"check"}"""),
            Turn.Calls1(name, "{}", "a"), Turn.Calls1(name, "{}", "b"), Turn.Says("done"));
        await fx.RunAsync(fx.Build(provider, EngineFixture.WorkerWith(name)), "check");
        Assert.Equal(executions, tool.Calls);
        Assert.Equal(ProgressIdentity.Observation, tool.Definition.ProgressIdentity);
        Assert.False(tool.Definition.ParallelRead);
    }

    [Theory]
    [InlineData(ToolKind.Relocate, true)]
    [InlineData(ToolKind.Unknown, false)]
    [InlineData(ToolKind.Command, true)]
    public async Task Review_mode_uses_declared_purpose_for_renamed_tool(ToolKind kind, bool executionReview)
    {
        using var fx = new EngineFixture { ShortReview = false };
        var inner = new WriteFileTool();
        fx.ToolsOverride = [new Alias(inner, inner.Definition with { Name = "custom_action", Kind = kind })];
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"work"}"""),
            Turn.Calls1("custom_action", """{"path":"result.txt","content":"hello"}"""), Turn.Says("done"));
        var reviewer = new FakeChatProvider() { WhenExhausted = Turn.Says("""{"verdict":"PASS","notes":"ok"}""") };
        await fx.RunAsync(fx.Build(provider, EngineFixture.WorkerWith("custom_action"), reviewProvider: reviewer, router: Routers.WithReviewer()), "work");
        Assert.NotEmpty(reviewer.Requests);
        Assert.Equal(executionReview ? Reviewer.ExecutionSystemPrompt : Reviewer.ContentSystemPrompt,
            reviewer.Requests[0].Messages[0].Content);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Success_check_requires_one_explicit_command_adapter(int count)
    {
        using var fx = new EngineFixture();
        var tools = Enumerable.Range(0, count).Select(i => new Stub(new("check_" + i, "test", "{}",
            WorkspaceEffect.None, Kind: ToolKind.Command, RunsSuccessChecks: true))).ToArray();
        fx.ToolsOverride = tools;
        var provider = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"done"}"""), Turn.Says("done"));
        await fx.RunAsync(fx.Build(provider, successCriteria: [new("criterion", "some command", 0, true)]), "work");
        Assert.Equal(count == 1 ? 1 : 0, tools.Sum(t => t.Calls));
        if (count == 1) Assert.Contains("some command", tools[0].LastArguments);
    }

    private sealed class Stub(ToolDefinition definition) : ITool
    {
        public ToolDefinition Definition => definition;
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public int Calls { get; private set; }
        public string LastArguments { get; private set; } = "";
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            Calls++;
            LastArguments = argumentsJson;
            return Task.FromResult(ToolResults.Ok("ok", metadata: new Dictionary<string, object?> { ["exitCode"] = 0 }));
        }
    }

    private sealed class Alias(ITool inner, ToolDefinition definition) : ITool
    {
        public ToolDefinition Definition => definition;
        public PermissionLevel RequiredLevel => inner.RequiredLevel;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
            => inner.InvokeAsync(argumentsJson, ctx, ct);
    }
}
