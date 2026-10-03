namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Run bf24a5, 2026-10-04: the request said "run the tests with THAT command and no other". The review of the
/// final checks answered with a policy naming the one command tool and three command families, and wrote beside
/// it that "editing/reading files inside the workspace is unaffected by this policy". The engine read the same
/// list as every tool the run may use: listing a folder, reading a file and writing one were all refused, at full
/// autonomy, and a task that was to write files ran for a quarter of an hour unable to open one. A list that
/// names only command tools is a statement about commands. Deliberately not tests: a ledger checked with a tool.
/// </summary>
public sealed class AListOfCommandToolsBoundsCommandsTests
{
    private const string Request = "Correct the ledger. Check it with ledger-check and no other command.";

    private static readonly PlanCheckTool[] Inventory =
    [
        new("read_file", ToolKind.Read, CommandPolicySyntax.None), new("write_file", ToolKind.Write, CommandPolicySyntax.None),
        new("delete_file", ToolKind.Relocate, CommandPolicySyntax.None), new("run_command", ToolKind.Command, CommandPolicySyntax.SimpleCommand),
        new("run_powershell", ToolKind.Command, CommandPolicySyntax.PowerShell), new("send_email", ToolKind.Unknown, CommandPolicySyntax.None)
    ];

    private static TaskActionPolicy Agreed(params string[] allowedTools)
    {
        var answer = System.Text.Json.JsonSerializer.Serialize(new
        {
            sources = new[] { new { id = "O001", assessment = "one command family" } },
            checks = Array.Empty<object>(), forbidden_effects = Array.Empty<object>(), unresolved = (string?)null,
            action_policy = new { allowed_tools = allowedTools, command_prefixes = new[] { "ledger-check" },
                source_quote = "Check it with ledger-check and no other command.", reason = "one command family" }
        });
        return PlanCheckContract.Validate(answer, complete: true, new PlanCheckInputs(Request, [], [], null, Inventory, false)).ActionPolicy!;
    }

    private static async Task<bool> Refused(TaskActionPolicy policy, string tool, string arguments)
    {
        using var fx = new EngineFixture();
        fx.Write("ledger.txt", "total: 10");
        var ctx = fx.ContextFor() with { Context = new WorkContext(null, "workspace", null, null, null, [], []) { ActionPolicy = policy } };
        var result = await new ToolRegistry(EngineFixture.ShippedTools()).InvokeAsync(new ToolCall("1", tool, arguments), ctx, default);
        return result.Metadata.ContainsKey("taskConstraintRefusal");
    }

    [Theory]
    [InlineData("read_file", """{"path":"ledger.txt"}""")]
    [InlineData("list_dir", """{"path":""}""")]
    [InlineData("write_file", """{"path":"ledger.txt","content":"total: 12"}""")]
    public async Task The_tools_that_read_and_change_the_workspaces_files_stay(string tool, string arguments)
        => Assert.False(await Refused(Agreed("run_command"), tool, arguments));

    [Theory]
    [InlineData("run_command", """{"command":"other-check"}""")]         // a command outside the families
    [InlineData("run_powershell", """{"script":"Get-ChildItem"}""")]     // a command tool that was not named
    [InlineData("send_email", """{"subject":"s","body":"b"}""")]         // not a file tool: it was not named either
    public async Task Everything_else_that_was_not_named_is_still_refused(string tool, string arguments)
        => Assert.True(await Refused(Agreed("run_command"), tool, arguments));

    [Fact]
    public async Task A_list_that_names_a_file_tool_is_the_whole_list_as_before()
    {
        var policy = Agreed("run_command", "read_file");

        Assert.False(await Refused(policy, "read_file", """{"path":"ledger.txt"}"""));
        Assert.True(await Refused(policy, "write_file", """{"path":"ledger.txt","content":"total: 12"}"""));
    }

    [Fact]
    public void A_policy_made_without_the_inventory_names_every_tool_it_allows()
        => Assert.False(new TaskActionPolicy(["run_command"], ["ledger-check"], "q", "r")
            .Names(new ToolDefinition("read_file", "reads", "{}", Kind: ToolKind.Read)));
}
