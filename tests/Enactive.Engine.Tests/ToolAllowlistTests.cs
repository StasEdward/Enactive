namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Tools;
using Enactive.Settings;
using Xunit;

/// <summary>
/// What a role's list of tools reaches, asked of the one rule every reader of a role uses.
///
/// <para>Until 2026-10-08 six places answered it, and they disagreed where nobody looked: the engine let
/// a role naming <c>MCP__dotnet__build</c> call the tool while the MCP screen said no role reached the
/// server, and a worker was advised to use tools its role had been refused.</para>
/// </summary>
public sealed class ToolAllowlistTests
{
    [Theory]
    [InlineData(new[] { "*" }, "send_email", true)]
    [InlineData(new[] { "read_file" }, "READ_FILE", true)]
    [InlineData(new[] { "Read_File" }, "read_file", true)]
    [InlineData(new[] { "mcp__dotnet__*" }, "mcp__dotnet__build", true)]
    [InlineData(new[] { "MCP__Dotnet__*" }, "mcp__dotnet__build", true)]
    [InlineData(new[] { "mcp__*" }, "mcp__mail__send", true)]
    [InlineData(new[] { "mcp__other__*" }, "mcp__dotnet__build", false)]
    [InlineData(new string[0], "read_file", false)]
    public void A_list_allows_a_tool_by_name_by_everything_or_by_an_mcp_prefix_whatever_the_case(
        string[] list, string tool, bool allowed)
        => Assert.Equal(allowed, ToolAllowlist.Allows(list, tool));

    /// <summary>
    /// Built-in tools are granted by name, on purpose: <c>write_*</c> would quietly hand a role every tool
    /// of that name added later. Only an MCP server, whose tools are not known until it starts, is named by prefix.
    /// </summary>
    [Fact]
    public void A_star_outside_mcp_is_a_name_and_not_a_pattern()
    {
        Assert.False(ToolAllowlist.Allows(["write_*"], "write_file"));
        Assert.True(ToolAllowlist.Allows(["write_*"], "write_*"));
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("mcp__*", true)]
    [InlineData("mcp__dot*", true)]
    [InlineData("mcp__dotnet__*", true)]
    [InlineData("mcp__dotnet__bu*", true)]
    [InlineData("MCP__dotnet__build", true)]
    [InlineData("mcp__dotnetx__*", false)]
    [InlineData("mcp__dotnet__", false)]
    [InlineData("read_file", false)]
    public void What_reaches_a_server(string entry, bool reaches)
        => Assert.Equal(reaches, ToolAllowlist.ReachesServer([entry], "dotnet"));

    /// <summary>
    /// Reaching a server is asked before it has started, so it has to be the gate's own answer for some tool
    /// that server could have - or the screen promises a role a tool the run will refuse it, or the reverse.
    /// </summary>
    [Fact]
    public void Reaching_a_server_is_allowing_some_tool_it_could_have()
    {
        string[] pool = ["*", "mcp__*", "mcp__dot*", "MCP__DOTNET__*", "mcp__dotnet__bu*", "mcp__dotnet__build",
            "Mcp__Mail__Send", "mcp__dotnetx__*", "mcp__dotnet__", "read_file", "write_*"];
        foreach (var server in new[] { "dotnet", "mail" })
        {
            var prefix = $"mcp__{server}__";
            foreach (var entry in pool)
            {
                // Tools the server could have: any name, and whatever the entry itself names under its prefix.
                var candidates = new List<string> { prefix + "x", prefix + "build", prefix + "send" };
                if (entry.TrimEnd('*').StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(entry.TrimEnd('*') + "x");

                Assert.Equal(candidates.Any(tool => ToolAllowlist.Allows([entry], tool)),
                    ToolAllowlist.ReachesServer([entry], server));
            }
        }
    }

    /// <summary>The MCP screen and the engine's role gate read a role the same way - here they did not, on case.</summary>
    [Fact]
    public void The_mcp_screen_and_the_role_gate_agree_on_a_tool_named_in_another_case()
    {
        string[] tools = ["MCP__dotnet__build"];

        Assert.True(ToolAccess.Allows(EngineFixture.WorkerWith(tools), "mcp__dotnet__build"));
        Assert.True(McpRoles.Reaches(new WorkerConfig { Id = "builder", Tools = [.. tools] }, "dotnet"));
    }

    /// <summary>
    /// A worker is advised only about tools its role can call. The advice used to read the list with the tools
    /// it implies added - which saved lists are given once, by migration - so a role somebody later took
    /// copy_file from was told to copy_file, and refused.
    /// </summary>
    [Fact]
    public void A_worker_is_not_advised_to_use_a_tool_its_role_does_not_carry()
    {
        const string copyAdvice = "copy_file it into place";

        Assert.DoesNotContain(copyAdvice,
            DefaultWorkers.Augment("Check the disks.", tools: ["run_command", "write_file", "read_file"]));
        Assert.Contains(copyAdvice,
            DefaultWorkers.Augment("Check the disks.", tools: ["run_command", "write_file", "read_file", "copy_file"]));
    }
}
