namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Enactive.Settings;
using Enactive.Tools.Mcp;
using Xunit;

/// <summary>
/// A cloned Worker, Provider or MCP server - and its own nested lists - is independent of the
/// original: editing the clone, or the clone's list of them, must not reach the live settings.
///
/// <para><b>Why the reflection test in SettingsSurviveTheEditorTests does not catch this.</b> It
/// compares clone against original BY VALUE, recursing into lists and objects - so a <c>Clone()</c>
/// that shared the SAME list (<c>Workers = Workers</c> instead of
/// <c>Workers = Workers.Select(x => x.Clone()).ToList()</c>) produces a clone whose values are
/// identical to the original's, because it IS the original's list. No value ever differs, so that
/// test finds nothing wrong.</para>
///
/// <para><b>Confirmed 2026-09-24</b>:
/// in a copy of AppSettings.Clone with `Workers = Workers.Select(x => x.Clone()).ToList()` replaced by
/// `Workers = Workers`, both existing tests in that file still passed. The shipped code already
/// clones correctly - Workers, Providers and McpServers all deep-copy their own nested lists too
/// (Tools, Models/Headers, Arguments/Environment/Headers) - these tests are what was missing to say
/// so, and what would catch it if that ever regressed.</para>
/// </summary>
public sealed class ClonedSettingsAreIndependentTests
{
    private static WorkerConfig Worker() => new()
    {
        Id = "w1", Role = "developer", Instructions = "be helpful",
        Tools = new List<string> { "read_file" }, Level = PermissionLevel.Execute,
        Model = "openai/gpt", Fallback = "openai/gpt-mini"
    };

    [Fact]
    public void Editing_a_cloned_worker_does_not_reach_the_original()
    {
        var settings = new AppSettings { Workers = new List<WorkerConfig> { Worker() } };

        var copy = settings.Clone();

        // Not the same list, and not the same worker object.
        Assert.NotSame(settings.Workers, copy.Workers);
        Assert.NotSame(settings.Workers[0], copy.Workers[0]);
        Assert.NotSame(settings.Workers[0].Tools, copy.Workers[0].Tools);

        // Editing a scalar property of the clone's worker...
        copy.Workers[0].Role = "reviewer";
        Assert.Equal("developer", settings.Workers[0].Role);

        // ...and mutating its Tools list...
        copy.Workers[0].Tools.Add("write_file");
        Assert.Equal(new[] { "read_file" }, settings.Workers[0].Tools);

        // ...and adding a worker to the clone's list...
        copy.Workers.Add(Worker());
        Assert.Single(settings.Workers);

        // ...none of it reaches the live settings.
        Assert.Equal("developer", settings.Workers[0].Role);
    }

    /// <summary>Removing a worker from the clone must not remove it from the original either.</summary>
    [Fact]
    public void Removing_a_cloned_worker_does_not_remove_it_from_the_original()
    {
        var settings = new AppSettings { Workers = new List<WorkerConfig> { Worker(), Worker() } };

        var copy = settings.Clone();
        copy.Workers.RemoveAt(0);

        Assert.Equal(2, settings.Workers.Count);
    }

    [Fact]
    public void Editing_a_cloned_provider_does_not_reach_the_original()
    {
        var settings = new AppSettings
        {
            Providers = new List<ProviderConfig>
            {
                new() { Id = "p1", Models = new List<string> { "m1" }, Headers = new Dictionary<string, string> { ["X"] = "1" } }
            }
        };

        var copy = settings.Clone();

        Assert.NotSame(settings.Providers[0], copy.Providers[0]);
        Assert.NotSame(settings.Providers[0].Models, copy.Providers[0].Models);
        Assert.NotSame(settings.Providers[0].Headers, copy.Providers[0].Headers);

        copy.Providers[0].Models.Add("m2");
        copy.Providers[0].Headers["Y"] = "2";
        copy.Providers[0].BaseUrl = "https://changed";

        Assert.Equal(new[] { "m1" }, settings.Providers[0].Models);
        Assert.DoesNotContain("Y", settings.Providers[0].Headers.Keys);
        Assert.NotEqual("https://changed", settings.Providers[0].BaseUrl);
    }

    [Fact]
    public void Editing_a_cloned_mcp_server_does_not_reach_the_original()
    {
        var settings = new AppSettings
        {
            McpServers = new List<McpServerConfig>
            {
                new() { Id = "srv", Arguments = new List<string> { "--flag" }, Environment = new Dictionary<string, string> { ["K"] = "v" } }
            }
        };

        var copy = settings.Clone();

        Assert.NotSame(settings.McpServers[0], copy.McpServers[0]);
        Assert.NotSame(settings.McpServers[0].Arguments, copy.McpServers[0].Arguments);
        Assert.NotSame(settings.McpServers[0].Environment, copy.McpServers[0].Environment);

        copy.McpServers[0].Arguments.Add("--other");
        copy.McpServers[0].Environment["NEW"] = "1";

        Assert.Equal(new[] { "--flag" }, settings.McpServers[0].Arguments);
        Assert.DoesNotContain("NEW", settings.McpServers[0].Environment.Keys);
    }
}
