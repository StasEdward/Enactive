namespace Enactive.Engine.Tests;

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Enactive.Tools;
using Enactive.Tools.Mcp;
using Enactive.Core.Tools;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Diagnostics;
using Enactive.Agents;
using Enactive.Mcp.TestServer;

public sealed class McpTests
{
    private static McpServerConfig Config(string id = "test", int timeout = 10) => new()
    {
        Id = id, Enabled = true, Command = "dotnet",
        Arguments = new() { typeof(Responses).Assembly.Location }, TimeoutSeconds = timeout
    };
    private static ToolContext Context(EngineFixture fx) => new(Guid.NewGuid(), Guid.NewGuid(), fx.Workspace.Id,
        new WorkContext(fx.Workspace.Id, "test", null, null, null, [], []), PermissionPolicy.PermissiveDefault,
        fx.Root, fx.Artifacts, new Services());

    [Fact]
    public async Task Stdio_discovers_all_pages_calls_tools_and_disposes_process()
    {
        using var fx = new EngineFixture();
        var connection = await McpConnection.ConnectAsync(Config(), fx.Root, default);
        int pid = 0;
        try
        {
            Assert.Equal(4, connection.Tools.Count);
            var echo = connection.Tools.Single(t => t.Definition.Name == McpConnection.ToolName("test", "echo"));
            Assert.True(echo.RequiresApproval);
            Assert.Contains("properties", echo.Definition.JsonSchema);
            var result = await echo.InvokeAsync("{\"value\":\"Привет \\\"MCP\\\"\"}", Context(fx), default);
            Assert.True(result.Success, result.Error);
            using var body = JsonDocument.Parse(result.Output!);
            Assert.Equal("Привет \"MCP\"", body.RootElement.GetProperty("value").GetString());
            var fail = connection.Tools.Single(t => t.Definition.Name == McpConnection.ToolName("test", "fail"));
            Assert.False((await fail.InvokeAsync("{}", Context(fx), default)).Success);
            var getPid = connection.Tools.Single(t => t.Definition.Name == McpConnection.ToolName("test", "pid"));
            pid = int.Parse((await getPid.InvokeAsync("{}", Context(fx), default)).Output!);
        }
        finally { await connection.DisposeAsync(); }
        Assert.True(Exited(pid), "MCP child process survived disposal.");
    }

    [Fact]
    public async Task Timeout_reports_unknown_outcome_without_retry()
    {
        using var fx = new EngineFixture();
        await using var connection = await McpConnection.ConnectAsync(Config(timeout: 2), fx.Root, default);
        var slow = connection.Tools.Single(t => t.Definition.Name == McpConnection.ToolName("test", "slow"));
        var result = await slow.InvokeAsync("{}", Context(fx), default);
        Assert.False(result.Success);
        Assert.Contains("outcome is unknown", result.Error);
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var fx = new EngineFixture();
        await using var connection = await McpConnection.ConnectAsync(Config(), fx.Root, default);
        using var cancel = new CancellationTokenSource(200);
        var slow = connection.Tools.Single(t => t.Definition.Name == McpConnection.ToolName("test", "slow"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.InvokeAsync("{}", Context(fx), cancel.Token));
    }

    [Fact]
    public async Task Servers_are_namespaced_and_disabled_servers_do_not_start()
    {
        using var fx = new EngineFixture();
        var disabled = Config("off"); disabled.Enabled = false; disabled.Command = "not-a-real-executable";
        await using var registry = await McpRunTools.ConnectAsync(new ToolRegistry([new ReadFileTool()]),
            [Config("one"), Config("two"), disabled], fx.Root, default);
        Assert.Equal(9, registry.Definitions.Count);
        Assert.Equal(9, registry.Definitions.Select(t => t.Name).Distinct().Count());
        Assert.True(registry.RequiresApprovalOf(McpConnection.ToolName("one", "echo")));
        Assert.False(registry.RequiresApprovalOf("read_file"));
    }

    [Theory]
    [InlineData("mcp__*", "allow", true, 1)]
    [InlineData("mcp__test__*", "deny", false, 1)]
    [InlineData("mcp__other__*", "allow", false, 0)]
    [InlineData("read_file", "allow", false, 0)]
    public async Task Engine_respects_role_and_approval(string pattern, string answer, bool called, int decisions)
    {
        using var fx = new EngineFixture();
        await using var connection = await McpConnection.ConnectAsync(Config(), fx.Root, default);
        var tools = new LoggingToolRegistry(new ToolRegistry(connection.Tools), NullLogSink.Instance);
        var name = McpConnection.ToolName("test", "echo");
        var provider = new FakeChatProvider(Turn.Says("{\"disposition\":\"quick_action\",\"title\":\"MCP\"}"),
            Turn.Calls1(name, "{\"value\":\"test\"}"), Turn.Says("done"));
        var handler = new ScriptedDecisionHandler(answer);
        var worker = EngineFixture.WorkerWith(pattern) with { DefaultLevel = PermissionLevel.Autonomous };
        var engine = new Orchestrator(new SingleProviderFactory(provider), new ModelResolver(), new StaticWorkerProvider(worker),
            tools, fx.Artifacts, fx.Workspace, new Planner(), new PermissionEngine(), handler,
            new PermissionPolicy(PermissionLevel.Autonomous, ["*"], []), new Services());
        var events = await fx.RunAsync(engine, "use MCP");
        Assert.Equal(decisions, handler.Requests.Count);
        Assert.Equal(called, events.Any(e => e.Kind == Enactive.Core.Events.EventKind.ToolInvoked));
        if (decisions > 0) Assert.Null(handler.Requests[0].Subject); // no remembered bypass of 'every call'
    }

    [Fact]
    public async Task Http_transport_sends_headers_and_calls_remote_tool()
    {
        using var fx = new EngineFixture();
        var handler = new McpHttpHandler();
        using var http = new HttpClient(handler);
        var config = new McpServerConfig { Id = "http", Transport = McpTransportKind.Http, Url = "https://mcp.invalid/mcp",
            Headers = new() { ["Authorization"] = "Bearer test-secret" } };
        await using var connection = await McpConnection.ConnectAsync(config, fx.Root, default, http);
        Assert.Equal(4, connection.Tools.Count);
        var echo = connection.Tools.Single(t => t.Definition.Name == McpConnection.ToolName("http", "echo"));
        Assert.True((await echo.InvokeAsync("{\"value\":\"http\"}", Context(fx), default)).Success);
        Assert.True(handler.SawAuthorization);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Config_clone_is_independent_and_runtime_secrets_are_not_serialized()
    {
        var config = Config(); config.Headers["Authorization"] = "test-secret"; config.Environment["TOKEN"] = "env-secret";
        var copy = config.Clone(); copy.Headers["Authorization"] = "changed"; copy.Arguments.Clear();
        Assert.Equal("test-secret", config.Headers["Authorization"]);
        Assert.NotEmpty(config.Arguments);
        var json = JsonSerializer.Serialize(config);
        Assert.DoesNotContain("test-secret", json); Assert.DoesNotContain("env-secret", json);
    }

    [Fact]
    public void Names_are_stable_bounded_and_distinguish_sanitized_names()
    {
        var name = McpConnection.ToolName(new string('a', 24), new string('x', 200));
        Assert.True(name.Length <= 64);
        Assert.Matches("^[a-zA-Z0-9_-]+$", name);
        Assert.NotEqual(McpConnection.ToolName("test", "a.b"), McpConnection.ToolName("test", "a/b"));
        Assert.Equal(McpConnection.ToolName("test", "echo"), McpConnection.ToolName("test", "echo"));
    }

    private static bool Exited(int pid)
    { try { using var process = Process.GetProcessById(pid); return process.HasExited || process.WaitForExit(3000); } catch (ArgumentException) { return true; } }
    private sealed class Services : IServiceProvider { public object? GetService(Type type) => null; }
    private sealed class McpHttpHandler : HttpMessageHandler
    {
        public bool SawAuthorization; public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SawAuthorization |= request.Headers.Authorization?.ToString() == "Bearer test-secret";
            if (request.Method != HttpMethod.Post) return new(HttpStatusCode.MethodNotAllowed);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("id", out _)) return new(HttpStatusCode.Accepted);
            if (doc.RootElement.GetProperty("method").GetString() == "tools/call") Calls++;
            return new(HttpStatusCode.OK) { Content = new StringContent(Responses.For(doc.RootElement), Encoding.UTF8, "application/json") };
        }
    }
}
