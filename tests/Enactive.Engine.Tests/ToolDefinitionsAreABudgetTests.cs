namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Amendment E: the tool definitions are part of every request, and so part of its budget. Run 80c951
/// spent over half a 65,536-token window on the definitions of 66 MCP tools before any work.
///
/// <para>Tool catalog (2026-10): the first cure measured the definitions against the window, and a provider that
/// states no window - an OpenAI-compatible endpoint with the field left empty, which is what a local model
/// server is - was never measured at all: every schema of every connected server went out with every turn.
/// So MCP tools are always offered on request: named in a catalog, loaded by name, bounded per step.</para>
/// </summary>
public sealed class ToolDefinitionsAreABudgetTests
{
    private sealed class McpTool(string name, string? description = null, string argument = "symbol") : ITool
    {
        public int Calls;
        public ToolDefinition Definition => new(name, description ?? $"{name}: finds things. " + new string('d', 2_000),
            "{\"type\":\"object\",\"properties\":{\"" + argument + "\":{\"type\":\"string\"}},\"required\":[\"" + argument + "\"]}",
            Kind: ToolKind.Read, WorkspaceEffect: WorkspaceEffect.None);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ToolResult(true, "3 references found", null, [], new Dictionary<string, object?>()));
        }
    }

    private const string References = "mcp__code__find_references_0123456789ab";

    private static McpTool[] Server(int count, string server = "code")
        => Enumerable.Range(0, count).Select(i => i == 7
            ? new McpTool($"mcp__{server}__find_references_0123456789ab")
            : new McpTool($"mcp__{server}__refactor_{i}_0123456789ab", argument: "target")).ToArray();

    private static ToolDefinition Builtin(string name) => new(name, "a built-in tool", "{}");

    private static string Names(params string[] names) => JsonSerializer.Serialize(new { names });

    // ── what is listed ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Mcp_tools_are_offered_on_request_whatever_the_window_and_the_engines_are_listed()
    {
        ToolDefinition[] tools = [Builtin("read_file"), .. Server(3).Select(t => t.Definition), Builtin("list_dir")];

        var (listed, onRequest) = ToolBudget.Split(tools);

        Assert.NotNull(onRequest);
        Assert.Equal(3, onRequest!.Count);
        Assert.Equal(["read_file", "list_dir", ToolBudget.LoadToolName], listed.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void With_no_mcp_tool_nothing_changes_and_there_is_nothing_to_load()
    {
        ToolDefinition[] tools = [.. Enumerable.Range(0, 40).Select(i => new ToolDefinition($"tool_{i}", new string('d', 2_000), "{}"))];

        var (listed, onRequest) = ToolBudget.Split(tools);

        Assert.Null(onRequest);
        Assert.Equal(40, listed.Length);
        Assert.DoesNotContain(listed, t => t.Name == ToolBudget.LoadToolName);
    }

    [Fact]
    public void The_catalog_names_each_tool_in_one_line_with_the_first_sentence_of_what_it_does()
    {
        ToolDefinition[] tools =
        [
            new McpTool("mcp__gh__create_issue_0123456789ab", "Create an issue in a repository. Needs a token with repo scope, and " + new string('x', 400)).Definition,
            new McpTool("mcp__gh__nameless_0123456789ab", "").Definition
        ];

        var load = ToolBudget.Split(tools).Listed.Single();

        Assert.Contains("mcp__gh__create_issue_0123456789ab - Create an issue in a repository.", load.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Needs a token", load.Description, StringComparison.Ordinal);
        Assert.Contains("mcp__gh__nameless_0123456789ab\n", load.Description + "\n", StringComparison.Ordinal);   // its name only
        Assert.DoesNotContain("\"symbol\"", load.Description, StringComparison.Ordinal);                             // no schema
    }

    [Fact]
    public void A_first_sentence_too_long_for_a_catalog_line_is_cut_and_says_so()
    {
        var tool = new McpTool("mcp__gh__long_0123456789ab", new string('w', 300) + ". And more.").Definition;

        var summary = ToolBudget.Summary(tool);

        Assert.EndsWith("...", summary, StringComparison.Ordinal);
        Assert.True(summary.Length < 100, $"{summary.Length} characters in one catalog line.");
    }

    [Fact]
    public void Past_the_threshold_the_catalog_is_one_line_per_server_and_a_search_is_offered_beside_it()
    {
        ToolDefinition[] tools = [.. Server(ToolBudget.CollapseThreshold + 1).Select(t => t.Definition), .. Server(2, "gh").Select(t => t.Definition)];

        var listed = ToolBudget.Split(tools).Listed;

        Assert.Equal([ToolBudget.LoadToolName, ToolBudget.FindToolName], listed.Select(t => t.Name).ToArray());
        Assert.Contains($"code ({ToolBudget.CollapseThreshold + 1} tools)", listed[0].Description, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp__code__refactor_3_0123456789ab", listed[0].Description, StringComparison.Ordinal);
    }

    // ── loading ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_name_that_is_not_in_the_catalog_is_refused_by_name_and_the_rest_are_loaded()
    {
        var onRequest = ToolBudget.Split([Builtin("read_file"), .. Server(10).Select(t => t.Definition)]).OnRequest!;

        var (accepted, reply) = onRequest.Load(Names(References, "read_file", "mcp__code__no_such_tool"));

        Assert.Equal([References], accepted.Select(t => t.Name).ToArray());
        Assert.Contains("read_file", reply, StringComparison.Ordinal);
        Assert.Contains("mcp__code__no_such_tool", reply, StringComparison.Ordinal);
        Assert.Contains("\"symbol\"", reply, StringComparison.Ordinal);       // the definition, for a model that calls in text
    }

    [Fact]
    public void A_tool_is_loaded_once_and_is_no_longer_waiting()
    {
        var onRequest = ToolBudget.Split([.. Server(10).Select(t => t.Definition)]).OnRequest!;
        Assert.True(onRequest.IsWaiting(References));

        Assert.Single(onRequest.Load(Names(References)).Accepted);
        Assert.Empty(onRequest.Load(Names(References)).Accepted);

        Assert.False(onRequest.IsWaiting(References));
        Assert.False(onRequest.IsWaiting("read_file"));                          // not a tool offered on request at all
    }

    [Fact]
    public void No_more_than_the_steps_limit_are_loaded_and_the_one_too_many_is_named()
    {
        var onRequest = ToolBudget.Split([.. Server(10).Select(t => t.Definition)], maxLoaded: 2).OnRequest!;

        var (accepted, reply) = onRequest.Load(Names("mcp__code__refactor_0_0123456789ab", "mcp__code__refactor_1_0123456789ab", "mcp__code__refactor_2_0123456789ab"));

        Assert.Equal(2, accepted.Count);
        Assert.Contains("mcp__code__refactor_2_0123456789ab", reply, StringComparison.Ordinal);
        Assert.Contains("2", reply, StringComparison.Ordinal);
        Assert.Empty(onRequest.Find("""{"query":"references"}""").Found);       // a search does not get round the limit
    }

    [Fact]
    public void What_an_earlier_attempt_at_the_step_loaded_is_listed_again_at_the_end()
    {
        ToolDefinition[] tools = [Builtin("read_file"), .. Server(10).Select(t => t.Definition)];

        var (listed, onRequest) = ToolBudget.Split(tools, loaded: [References]);

        Assert.Equal(["read_file", ToolBudget.LoadToolName, References], listed.Select(t => t.Name).ToArray());
        Assert.False(onRequest!.IsWaiting(References));
    }

    [Fact]
    public void A_search_finds_by_name_or_word_and_does_not_give_the_same_tool_twice()
    {
        var onRequest = ToolBudget.Split([.. Server(30).Select(t => t.Definition)]).OnRequest!;

        var (found, reply) = onRequest.Find("""{"query":"find references to a symbol"}""");
        Assert.Equal(References, found[0].Name);
        Assert.StartsWith("Callable from your next turn: " + References, reply, StringComparison.Ordinal);

        Assert.Equal("refactor_3", onRequest.Find("""{"query":"refactor_3"}""").Found.Single().Name.Split("__")[2][..10]);
        Assert.DoesNotContain(onRequest.Find("""{"query":"find_references"}""").Found, t => t.Name.Contains("find_references"));
    }

    // ── through the engine ───────────────────────────────────────────────────────────────

    private static (EngineFixture Fx, McpTool[] Server, Enactive.Core.Workers.Worker Role) Fixture(int tools = 10)
    {
        var fx = new EngineFixture();
        var server = Server(tools);
        fx.ToolsOverride = [.. EngineFixture.ShippedTools(), .. server];
        var role = EngineFixture.Role("developer");
        return (fx, server, role with { ToolAllowlist = [.. role.ToolAllowlist, "mcp__*"] });
    }

    /// <summary>
    /// THE ONE THAT MATTERS: with no window stated at all, the MCP tools are off the first request, loaded by
    /// name, listed at the end from the next turn, and called through the ordinary gate.
    /// </summary>
    [Fact]
    public async Task A_tool_offered_on_request_is_loaded_listed_and_called()
    {
        var (fx, server, role) = Fixture();
        using var _ = fx;
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"look"}"""),
            Turn.Calls1(ToolBudget.LoadToolName, Names(References), "l1"),
            Turn.Calls1(References, """{"symbol":"Planner"}""", "m1"),
            Turn.Says("Planner has 3 references."));

        var events = await fx.RunAsync(fx.Build(worker, role), "how many references does Planner have");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        var first = worker.Requests[1].Tools!.Select(t => t.Name).ToArray();
        Assert.Contains(ToolBudget.LoadToolName, first);
        Assert.DoesNotContain(first, n => n.StartsWith("mcp__", StringComparison.Ordinal));
        var second = worker.Requests[2].Tools!.Select(t => t.Name).ToArray();
        Assert.Equal(References, second[^1]);                                   // appended at the END
        Assert.Equal(first, second[..^1]);                                      // and nothing before it moved
        Assert.Equal(1, server[7].Calls);
    }

    [Fact]
    public async Task The_catalog_holds_no_tool_the_role_was_not_given()
    {
        var (fx, _, role) = Fixture();
        using var __ = fx;
        role = role with { ToolAllowlist = [.. role.ToolAllowlist.Where(t => t != "mcp__*"), References] };
        var worker = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"look"}"""), Turn.Says("Nothing to do."));

        await fx.RunAsync(fx.Build(worker, role), "say hello");

        var load = worker.Requests[1].Tools!.Single(t => t.Name == ToolBudget.LoadToolName);
        Assert.Contains(References, load.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp__code__refactor_3_0123456789ab", load.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hole: a call was checked against the registry and the role, not against what the model had been
    /// shown. A tool still waiting in the catalog ran when it was named - in a native call, or in text.
    /// </summary>
    [Fact]
    public async Task A_tool_still_waiting_in_the_catalog_is_not_run_when_called_and_the_step_is_told_to_load_it()
    {
        var (fx, server, role) = Fixture();
        using var _ = fx;
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"look"}"""),
            Turn.Calls1(References, """{"symbol":"Planner"}""", "m1"),
            Turn.Says("Could not look."));

        var events = await fx.RunAsync(fx.Build(worker, role), "how many references does Planner have");

        Assert.Equal(0, server[7].Calls);
        Assert.Contains(events, e => e.Kind == EventKind.ToolResult && e.Summary.Contains(ToolBudget.LoadToolName, StringComparison.Ordinal)
                                     && e.Summary.Contains("not loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_call_written_in_text_to_a_tool_still_in_the_catalog_is_answered_with_how_to_load_it()
    {
        var (fx, server, role) = Fixture();
        using var _ = fx;
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"look"}"""),
            Turn.Says("```json\n{\"symbol\":\"Planner\"}\n```"),      // only the references tool takes a symbol
            Turn.Says("Could not look."));

        var events = await fx.RunAsync(fx.Build(worker, role, allowImplicitToolCalls: true), "how many references does Planner have");

        Assert.Equal(0, server[7].Calls);
        Assert.Contains(events, e => e.Kind == EventKind.ToolResult && e.Summary.Contains("not loaded", StringComparison.Ordinal));
    }

    /// <summary>
    /// A reply is read for a call by its arguments: the tool whose required arguments it gives. It was read against
    /// everything registered, so arguments that fitted a tool the model had never been sent were taken for a call
    /// to it. It is read against what the step was shown and the tools named in its catalog.
    /// </summary>
    [Fact]
    public async Task A_reply_that_fits_a_tool_the_step_was_never_shown_is_not_taken_for_a_call()
    {
        var (fx, _, role) = Fixture();
        using var __ = fx;
        var hidden = new McpTool("not_for_this_role", argument: "secret");
        fx.ToolsOverride = [.. fx.ToolsOverride!, hidden];
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"look"}"""),
            Turn.Says("```json\n{\"secret\":\"Planner\"}\n```"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(worker, role, allowImplicitToolCalls: true), "how many references does Planner have");

        Assert.Equal(0, hidden.Calls);
        Assert.DoesNotContain(events, e => e.Kind is EventKind.ToolInvoked or EventKind.ToolResult or EventKind.DecisionResolved
                                           && e.Summary.Contains("not_for_this_role", StringComparison.Ordinal));
    }

    [Fact]
    public async Task What_a_step_was_shown_is_recorded_at_its_start_and_after_each_load()
    {
        var (fx, _, role) = Fixture();
        using var __ = fx;
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"look"}"""),
            Turn.Calls1(ToolBudget.LoadToolName, Names(References), "l1"),
            Turn.Says("Loaded."));

        var events = await fx.RunAsync(fx.Build(worker, role), "load the references tool");

        var shown = events.Where(e => e.Kind == EventKind.ToolsExposed).ToArray();
        Assert.Equal(2, shown.Length);
        using var first = JsonDocument.Parse(shown[0].PayloadJson!);
        Assert.Equal(10, first.RootElement.GetProperty("catalog").GetArrayLength());
        Assert.Equal(0, first.RootElement.GetProperty("loaded").GetArrayLength());
        Assert.Contains("read_file", first.RootElement.GetProperty("core").EnumerateArray().Select(e => e.GetString()));
        using var second = JsonDocument.Parse(shown[1].PayloadJson!);
        Assert.Equal([References], second.RootElement.GetProperty("loaded").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task A_new_step_starts_with_nothing_loaded()
    {
        var (fx, _, role) = Fixture();
        using var __ = fx;
        var worker = new FakeChatProvider(
            Turn.Says("""{"disposition":"task","title":"two","steps":[{"title":"Look up references","dependsOn":[]},{"title":"Say what was found","dependsOn":[0]}]}"""),
            Turn.Calls1(ToolBudget.LoadToolName, Names(References), "l1"),
            Turn.Says("Loaded and looked."),
            Turn.Says("Found three."));

        await fx.RunAsync(fx.Build(worker, role), "look up the references and say what was found");

        var lastStep = worker.Requests[^1].Tools!.Select(t => t.Name).ToArray();
        Assert.Contains(ToolBudget.LoadToolName, lastStep);
        Assert.DoesNotContain(References, lastStep);
    }
}
