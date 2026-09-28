namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Amendment E: the tool definitions are part of every request, and so part of its budget. Run 80c951
/// spent over half a 65,536-token window on the definitions of 66 MCP tools before any work.
/// </summary>
public sealed class ToolDefinitionsAreABudgetTests
{
    private sealed class McpTool(string name, int descriptionChars = 2_000) : ITool
    {
        public int Calls;
        public ToolDefinition Definition => new(name, $"{name}: " + new string('d', descriptionChars),
            """{"type":"object","properties":{"symbol":{"type":"string"}}}""", Kind: ToolKind.Read, WorkspaceEffect: WorkspaceEffect.None);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ToolResult(true, "3 references found", null, [], new Dictionary<string, object?>()));
        }
    }

    private static McpTool[] Server(int count)
        => Enumerable.Range(0, count).Select(i => new McpTool($"mcp__code__{(i == 7 ? "find_references" : $"refactor_{i}")}_0123456789ab")).ToArray();

    private static ToolDefinition Builtin(string name) => new(name, "a built-in tool", "{}");

    [Fact]
    public void Past_their_share_of_the_window_mcp_tools_are_offered_on_request_and_the_engines_are_listed()
    {
        ToolDefinition[] tools = [Builtin("read_file"), .. Server(30).Select(t => t.Definition)];

        var (listed, onRequest, tokens) = ToolBudget.Split(tools, 40_000);

        Assert.NotNull(onRequest);
        Assert.Equal(30, onRequest!.Count);
        Assert.Equal(["read_file", ToolBudget.FindToolName], listed.Select(t => t.Name).ToArray());
        Assert.True(tokens > 8_000);
        Assert.Contains("code: refactor_0,", listed[1].Description, StringComparison.Ordinal);          // named, short, by server
    }

    [Theory]
    [InlineData(1_000_000)]        // they fit
    [InlineData(null)]             // no window stated: nothing to measure against
    public void Within_their_share_or_with_no_window_nothing_changes(int? window)
    {
        ToolDefinition[] tools = [Builtin("read_file"), .. Server(30).Select(t => t.Definition)];
        var (listed, onRequest, _) = ToolBudget.Split(tools, window);
        Assert.Null(onRequest);
        Assert.Equal(31, listed.Length);
    }

    [Fact]
    public void The_engines_own_tools_are_never_taken_off_the_list()
    {
        ToolDefinition[] tools = [.. Enumerable.Range(0, 40).Select(i => new ToolDefinition($"tool_{i}", new string('d', 2_000), "{}"))];
        Assert.Null(ToolBudget.Split(tools, 10_000).OnRequest);
    }

    [Fact]
    public void A_search_finds_by_name_or_word_and_does_not_give_the_same_tool_twice()
    {
        ToolDefinition[] tools = [.. Server(30).Select(t => t.Definition)];
        var onRequest = ToolBudget.Split(tools, 10_000).OnRequest!;

        var (found, reply) = onRequest.Find("""{"query":"find references to a symbol"}""");
        Assert.Equal("mcp__code__find_references_0123456789ab", found[0].Name);
        Assert.StartsWith("Callable from your next turn: mcp__code__find_references_0123456789ab", reply, StringComparison.Ordinal);

        Assert.Equal("refactor_3", onRequest.Find("""{"query":"refactor_3"}""").Found.Single().Name.Split("__")[2][..10]);
        Assert.DoesNotContain(onRequest.Find("""{"query":"find_references"}""").Found, t => t.Name.Contains("find_references"));
    }

    /// <summary>
    /// THE ONE THAT MATTERS: the MCP tools are off the first request, found by a search, listed from the
    /// next turn, and called through the ordinary gate.
    /// </summary>
    [Fact]
    public async Task A_tool_offered_on_request_is_found_listed_and_called()
    {
        using var fx = new EngineFixture();
        var server = Server(30);
        fx.ToolsOverride = [.. EngineFixture.ShippedTools(), .. server];
        var role = EngineFixture.Role("developer");
        role = role with { ToolAllowlist = [.. role.ToolAllowlist, "mcp__*"] };
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"look"}"""),
            Turn.Calls1(ToolBudget.FindToolName, """{"query":"references"}""", "f1"),
            Turn.Calls1("mcp__code__find_references_0123456789ab", """{"symbol":"Planner"}""", "m1"),
            Turn.Says("Planner has 3 references."))
        { Window = 40_000 };

        var events = await fx.RunAsync(fx.Build(worker, role), "how many references does Planner have");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        var first = worker.Requests[1].Tools!.Select(t => t.Name).ToArray();
        Assert.Contains(ToolBudget.FindToolName, first);
        Assert.DoesNotContain(first, n => n.StartsWith("mcp__", StringComparison.Ordinal));
        Assert.Contains("mcp__code__find_references_0123456789ab", worker.Requests[2].Tools!.Select(t => t.Name));
        Assert.Equal(1, server[7].Calls);
        Assert.Contains(events, e => e.Kind == EventKind.ContextAssembled
                                     && e.Summary.Contains("the 30 tools of code are offered through find_tools", StringComparison.Ordinal));
    }
}
