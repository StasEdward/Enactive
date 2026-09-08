namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Agents;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// The reading and describing of tool calls, tested directly.
///
/// <para>Every one of these could previously be reached only by running a whole engine with a
/// scripted provider, because they were private members of a 2,171-line class. Cut 1 of
/// <c>FIX_PLAN.md</c> §9d moved them out; these are what the move was worth. Several of them exist
/// because of a specific incident, and until now none of those incidents had a test that named it.</para>
/// </summary>
public sealed class ToolCallParsingTests
{
    private static ToolDefinition Tool(string name, string schema) => new(name, name, schema);

    // ── NormalizeToolArgs ───────────────────────────────────────────────────

    /// <summary>
    /// The reason this function exists: small models concatenate two JSON objects into one call's
    /// arguments. Keep the FIRST value so the call still runs; drop the trailing junk.
    /// </summary>
    [Fact]
    public void Two_json_objects_run_together_keep_the_first()
    {
        Assert.Equal("""{"a":1}""", ToolCallParsing.NormalizeToolArgs("""{"a":1}{"b":2}"""));
    }

    [Theory]
    [InlineData("", "{}")]
    [InlineData("   ", "{}")]
    [InlineData(null, "{}")]
    public void Nothing_at_all_is_an_empty_object(string? raw, string expected)
        => Assert.Equal(expected, ToolCallParsing.NormalizeToolArgs(raw!));

    /// <summary>
    /// Unparseable arguments come back UNCHANGED rather than as "{}". The tool reports the real
    /// error itself; turning the mistake into a valid empty object would run the call with no
    /// arguments and report success.
    /// </summary>
    [Fact]
    public void Arguments_that_are_not_json_are_handed_on_as_they_are()
    {
        const string broken = """{"path": "a.txt", """;
        Assert.Equal(broken, ToolCallParsing.NormalizeToolArgs(broken));
    }

    [Fact]
    public void Valid_arguments_survive_intact()
    {
        var normalised = ToolCallParsing.NormalizeToolArgs("""{ "path" : "a.txt" , "n" : 2 }""");
        Assert.Equal("""{ "path" : "a.txt" , "n" : 2 }""".Replace(" ", ""), normalised.Replace(" ", ""));
    }

    // ── TryRecoverImplicitToolCall ──────────────────────────────────────────

    private static readonly ToolDefinition[] Registry =
    {
        Tool("write_file", """{"properties":{"path":{},"content":{}},"required":["path","content"]}"""),
        Tool("read_file", """{"properties":{"path":{},"offset":{}},"required":["path"]}"""),
    };

    /// <summary>A fenced block whose keys satisfy exactly one tool is recovered as that call.</summary>
    [Fact]
    public void A_fenced_block_matching_one_tool_is_recovered()
    {
        const string reply = """
            I will write the file now:

            ```json
            {"path":"notes.md","content":"hello"}
            ```
            """;

        var call = ToolCallParsing.TryRecoverImplicitToolCall(reply, Registry);

        Assert.NotNull(call);
        Assert.Equal("write_file", call!.Name);
        Assert.Contains("notes.md", call.ArgumentsJson);
    }

    /// <summary>
    /// The one that matters most, and the reason the old "outermost braces" fallback was removed:
    /// prose containing a brace is not an action. A reply that says "do not execute" and shows an
    /// example wrote a file, once.
    /// </summary>
    [Fact]
    public void Prose_containing_braces_is_not_an_action()
    {
        const string reply =
            """Example, do not execute: {"path":"danger.txt","content":"x"} — that would overwrite it.""";

        Assert.Null(ToolCallParsing.TryRecoverImplicitToolCall(reply, Registry));
    }

    /// <summary>Ambiguity is refused, not guessed at.</summary>
    [Fact]
    public void A_block_that_fits_two_tools_equally_well_is_refused()
    {
        var ambiguous = new[]
        {
            Tool("alpha", """{"properties":{"path":{}},"required":["path"]}"""),
            Tool("beta", """{"properties":{"path":{}},"required":["path"]}"""),
        };

        const string reply = """
            ```json
            {"path":"a.txt"}
            ```
            """;

        Assert.Null(ToolCallParsing.TryRecoverImplicitToolCall(reply, ambiguous));
    }

    /// <summary>A block that satisfies no tool's required keys is not a call.</summary>
    [Fact]
    public void A_block_that_matches_nothing_is_not_a_call()
    {
        const string reply = """
            ```json
            {"temperature":0.2,"seed":11}
            ```
            """;

        Assert.Null(ToolCallParsing.TryRecoverImplicitToolCall(reply, Registry));
    }

    /// <summary>Plain text with no block at all: nothing to recover, and no exception either.</summary>
    [Fact]
    public void Ordinary_prose_recovers_nothing()
        => Assert.Null(ToolCallParsing.TryRecoverImplicitToolCall("I have finished the task.", Registry));

    // ── BuildToolCalls ──────────────────────────────────────────────────────

    /// <summary>
    /// Streamed deltas arrive keyed by index and must come out in that order — a provider is free to
    /// finish the second call's arguments before the first's.
    /// </summary>
    [Fact]
    public void Streamed_calls_come_out_in_index_order()
    {
        var builders = new Dictionary<int, ToolCallParsing.ToolCallBuilder>
        {
            [1] = Builder("id-2", "read_file", """{"path":"b.txt"}"""),
            [0] = Builder("id-1", "write_file", """{"path":"a.txt","content":"x"}"""),
        };

        var calls = ToolCallParsing.BuildToolCalls(builders);

        Assert.NotNull(calls);
        Assert.Equal(new[] { "write_file", "read_file" }, calls!.Select(c => c.Name));
    }

    [Fact]
    public void No_builders_is_no_calls_rather_than_an_empty_list()
        => Assert.Null(ToolCallParsing.BuildToolCalls(new Dictionary<int, ToolCallParsing.ToolCallBuilder>()));

    /// <summary>A call whose arguments never arrived still runs, with an empty object.</summary>
    [Fact]
    public void A_call_with_no_arguments_gets_an_empty_object()
    {
        var calls = ToolCallParsing.BuildToolCalls(new Dictionary<int, ToolCallParsing.ToolCallBuilder>
        {
            [0] = Builder("id", "list_dir", ""),
        });

        Assert.Equal("{}", Assert.Single(calls!).ArgumentsJson);
    }

    private static ToolCallParsing.ToolCallBuilder Builder(string id, string name, string arguments)
    {
        var builder = new ToolCallParsing.ToolCallBuilder { Id = id, Name = name };
        builder.Arguments.Append(arguments);
        return builder;
    }

    // ── Compact and DescribeCall: the two must not be confused ──────────────

    /// <summary>An event line is one line, and bounded.</summary>
    [Fact]
    public void An_event_line_is_flattened_and_capped()
    {
        var long_ = ToolCallParsing.Compact("{\"script\":\"" + new string('x', 400) + "\"}");

        Assert.DoesNotContain('\n', long_);
        Assert.True(long_.Length <= 121, $"an event line grew to {long_.Length} characters");
        Assert.EndsWith("…", long_);
    }

    [Fact]
    public void A_short_line_is_left_alone_apart_from_its_newlines()
        => Assert.Equal("""{"a": 1} {"b": 2}""", ToolCallParsing.Compact("{\"a\": 1}\n{\"b\": 2}"));

    /// <summary>
    /// And the distinction the two exist to keep: what a person is asked to APPROVE is never
    /// shortened. Shortening the thing while running all of it is how a long script gets approved by
    /// its first sentence.
    /// </summary>
    [Fact]
    public void What_a_person_approves_is_never_shortened()
    {
        var script = "line one\n" + new string('y', 400) + "\nline three";
        var call = new ToolCall("id", "run_powershell", System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string> { ["script"] = script }));

        var described = ToolCallParsing.DescribeCall(call);

        Assert.Contains(new string('y', 400), described);
        Assert.Contains("line one", described);
        Assert.Contains("line three", described);
        Assert.DoesNotContain("…", described);
    }

    /// <summary>Newlines survive, because a shell script has to be readable as a script.</summary>
    [Fact]
    public void Newlines_in_an_argument_survive_into_the_card()
    {
        var call = new ToolCall("id", "run_command", """{"command":"one\ntwo"}""");

        Assert.Contains("one\ntwo", ToolCallParsing.DescribeCall(call));
    }

    /// <summary>Arguments that do not parse are shown raw: something odd beats nothing.</summary>
    [Fact]
    public void An_unparseable_argument_is_shown_as_it_is()
    {
        const string broken = """{"command": "rm -rf /", """;

        Assert.Equal(broken, ToolCallParsing.DescribeCall(new ToolCall("id", "run_command", broken)));
    }

    // ── TryReadSchemaKeys ───────────────────────────────────────────────────

    [Fact]
    public void A_schema_gives_up_its_required_and_optional_keys()
    {
        Assert.True(ToolCallParsing.TryReadSchemaKeys(
            """{"properties":{"path":{},"offset":{}},"required":["path"]}""",
            out var required, out var properties));

        Assert.Equal(new[] { "path" }, required);
        Assert.Equal(new[] { "offset", "path" }, properties.OrderBy(p => p));
    }

    [Fact]
    public void A_schema_that_is_not_json_is_refused_rather_than_thrown()
    {
        Assert.False(ToolCallParsing.TryReadSchemaKeys("not a schema", out var required, out var properties));
        Assert.Empty(required);
        Assert.Empty(properties);
    }
}
