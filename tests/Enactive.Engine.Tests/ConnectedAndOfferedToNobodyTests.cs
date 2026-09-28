namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Settings;
using Xunit;

/// <summary>
/// An MCP server that is started for every run and whose tools no role may call.
///
/// <para><b>Found on a live machine, 2026-09-22.</b> Two servers had been enabled for days —
/// <c>desktop-commander</c> and <c>log-analyzer</c> — and no role named any <c>mcp__</c> pattern.
/// Every run spawned two child processes, waited for them to initialise, and offered the model
/// nothing from either. A 139 MB day of logs contains not one <c>mcp__</c> tool name, and nothing
/// anywhere said why: the role gate runs BEFORE <see cref="ToolOffers"/>, so these tools are not
/// withheld — they are simply absent, which looks exactly like a server nobody configured.</para>
///
/// <para>Two places say it now, because they answer two different questions. The settings pane says
/// it while somebody is standing in front of it and can fix it; the run says it once it has already
/// paid to start the server, which is the only place a scheduled run can be heard from.</para>
/// </summary>
public sealed class ConnectedAndOfferedToNobodyTests
{
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static string Tool(string server, string name) => $"{McpReach.Prefix}{server}__{name}_abc123";

    // ── the engine's half ──────────────────────────────────────────────────────────

    [Fact]
    public void A_server_no_role_names_is_reported_with_its_tool_count_and_the_pattern_that_fixes_it()
    {
        var registered = new[]
        {
            "read_file", "write_file",
            Tool("dotnet", "project"), Tool("dotnet", "package"), Tool("dotnet", "ef")
        };

        var said = McpReach.Unreached(registered, _ => false);

        Assert.NotNull(said);
        Assert.Contains("dotnet (3 tool(s))", said, StringComparison.Ordinal);
        Assert.Contains("mcp__dotnet__*", said, StringComparison.Ordinal);
    }

    /// <summary>Nothing to say when the role reaches them — the usual case must stay silent.</summary>
    [Fact]
    public void A_server_the_role_can_call_says_nothing()
        => Assert.Null(McpReach.Unreached(new[] { "read_file", Tool("dotnet", "project") }, _ => true));

    /// <summary>And nothing at all to say when there is no MCP in the run.</summary>
    [Fact]
    public void A_run_without_mcp_says_nothing()
        => Assert.Null(McpReach.Unreached(new[] { "read_file", "run_command" }, _ => false));

    /// <summary>
    /// Per server, not in total: a run may hold one the role names and one it does not, and
    /// "some MCP tools are unreachable" sends somebody to the wrong screen.
    /// </summary>
    [Fact]
    public void One_reachable_server_does_not_cover_for_an_unreachable_one()
    {
        var registered = new[] { Tool("dotnet", "build"), Tool("log-analyzer", "tail") };

        var said = McpReach.Unreached(registered, name => name.Contains("__dotnet__", StringComparison.Ordinal));

        Assert.NotNull(said);
        Assert.Contains("log-analyzer", said, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet (", said, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mcp__dotnet__project_abc123", "dotnet")]
    [InlineData("mcp__log-analyzer__tail_0011ff", "log-analyzer")]
    [InlineData("read_file", null)]
    [InlineData("mcp__nothingafter", null)]
    public void The_server_a_tool_came_from_is_read_off_its_name(string tool, string? server)
        => Assert.Equal(server, McpReach.ServerOf(tool));

    // ── the settings screen's half ─────────────────────────────────────────────────

    private static WorkerConfig Role(params string[] tools)
        => new() { Id = "developer", Role = "Developer", Level = PermissionLevel.Execute, Tools = tools.ToList() };

    [Fact]
    public void The_pane_names_an_enabled_server_no_role_can_reach()
    {
        var note = McpRoles.Note(
            new[] { Role("read_file", "write_file") },
            new[] { ("dotnet", true) });

        Assert.NotNull(note);
        Assert.Contains("dotnet", note, StringComparison.Ordinal);
        Assert.Contains("mcp__dotnet__*", note, StringComparison.Ordinal);
    }

    /// <summary>A server nobody turned on costs nothing and is nobody's problem.</summary>
    [Fact]
    public void A_disabled_server_is_not_complained_about()
        => Assert.Null(McpRoles.Note(new[] { Role("read_file") }, new[] { ("dotnet", false) }));

    /// <summary>The three spellings that reach it, and the one that does not.</summary>
    [Theory]
    [InlineData("mcp__*", true)]
    [InlineData("mcp__dotnet__*", true)]
    [InlineData("*", true)]
    [InlineData("mcp__other__*", false)]
    [InlineData("read_file", false)]
    public void What_counts_as_naming_a_server(string pattern, bool reaches)
        => Assert.Equal(reaches, McpRoles.Reaches(Role(pattern), "dotnet"));

    // ── through a real run, with a real server ────────────────────────────────────

    /// <summary>
    /// THE ONE THAT MATTERS, and the shape the live machine was in: a server that connects, a role
    /// that names <c>mcp__other__*</c>, and four tools nobody may call. Before this, the run did
    /// its work and said nothing at all about the process it had started.
    /// </summary>
    [Fact]
    public async Task A_run_that_started_a_server_it_cannot_use_says_so()
    {
        using var fx = new EngineFixture();

        var config = new Enactive.Tools.Mcp.McpServerConfig
        {
            Id = "test",
            Enabled = true,
            Command = "dotnet",
            Arguments = new() { typeof(Enactive.Mcp.TestServer.Responses).Assembly.Location },
            TimeoutSeconds = 10
        };

        await using var connection = await Enactive.Tools.Mcp.McpConnection.ConnectAsync(config, fx.Root, default);
        var tools = new Enactive.Tools.ToolRegistry(connection.Tools.Append(new Enactive.Tools.ReadFileTool()));

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"no mcp for you"}"""),
            Turn.Says("done"));

        // Names an MCP server, just not THIS one - the commonest way to get this wrong, and
        // indistinguishable from naming none of them until somebody is told.
        var worker = EngineFixture.WorkerWith("read_file", "mcp__other__*");

        var engine = new Orchestrator(new Enactive.Workspace.WorkspaceChangesFactory(),
            new SingleProviderFactory(provider), new ModelResolver(),
            new StaticWorkerProvider(worker), tools, fx.Artifacts, fx.Workspace,
            new Planner(checksAuditEnabled: false), new PermissionEngine(), fx.Decisions,
            PermissionPolicy.PermissiveDefault, new NoServices());

        var events = await fx.RunAsync(engine, "do something without MCP");

        var said = events.Select(e => e.Summary).FirstOrDefault(
            m => m.Contains("offered to nobody", StringComparison.Ordinal));

        Assert.True(said is not null,
            "the run started an MCP server it could not use and said nothing:\n"
            + string.Join("\n", events.Select(e => $"{e.Kind}: {e.Summary}")));

        Assert.Contains("test (4 tool(s))", said!, StringComparison.Ordinal);
        Assert.Contains("mcp__test__*", said!, StringComparison.Ordinal);
    }
}
