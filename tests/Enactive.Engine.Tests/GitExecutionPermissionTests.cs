namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Settings;
using Enactive.Tools;
using Xunit;

public sealed class GitExecutionPermissionTests
{
    private const string Plan = """{"disposition":"quick_action","title":"git check"}""";
    private static readonly string Alias = JsonSerializer.Serialize(new
        { args = new[] { "-c", "alias.probe=!echo executed > marker.txt", "probe" } });

    [Theory]
    [InlineData("off", false)]
    [InlineData("remote", false)]
    [InlineData("deny", false)]
    [InlineData("allow", false)]
    [InlineData("off", true)]
    [InlineData("remote", true)]
    [InlineData("deny", true)]
    [InlineData("allow", true)]
    public async Task Shell_policy_controls_real_git_command_execution(string mode, bool configuredAlias)
    {
        using var fx = new EngineFixture();
        var init = await fx.Invoke(new GitTool(), """{"args":["init","-q"]}""");
        Assert.True(init.Success, init.Error);
        if (configuredAlias)
        {
            var config = await fx.Invoke(new GitTool(), JsonSerializer.Serialize(new
                { args = new[] { "config", "--local", "alias.probe", "!echo executed > marker.txt" } }));
            Assert.True(config.Success, config.Error);
        }
        var policy = mode switch
        {
            "remote" => RemotePolicy.ForRemoteRun(PermissionPolicy.PermissiveDefault),
            "off" => EngineComposition.WithShells(PermissionPolicy.PermissiveDefault, ShellCommandPolicy.Off),
            _ => EngineComposition.WithShells(PermissionPolicy.PermissiveDefault, ShellCommandPolicy.Ask)
        };
        var decisions = new ScriptedDecisionHandler(mode == "allow" ? "allow" : "deny");
        var provider = new FakeChatProvider(Turn.Says(Plan), Turn.Calls1("git",
            configuredAlias ? """{"args":["probe"]}""" : Alias))
            { WhenExhausted = Turn.Says("done") };
        await fx.RunAsync(fx.Build(provider, worker: EngineFixture.WorkerWith("git"),
            policy: policy, decisions: decisions), "git check");
        Assert.Equal(mode == "allow", fx.Exists("marker.txt"));
        if (mode is "off" or "remote") Assert.Empty(decisions.Requests);
        else
        {
            var request = Assert.Single(decisions.Requests);
            Assert.True(request.SessionOnly);
            Assert.False(request.MayBeRemembered);
            Assert.Contains("probe", request.FullDetail);
        }
    }

    [Theory]
    [InlineData("off")]
    [InlineData("remote")]
    [InlineData("ask")]
    public async Task Docker_cannot_bypass_the_shell_policy(string mode)
    {
        using var fx = new EngineFixture();
        var policy = mode == "remote" ? RemotePolicy.ForRemoteRun(PermissionPolicy.PermissiveDefault)
            : EngineComposition.WithShells(PermissionPolicy.PermissiveDefault,
                mode == "off" ? ShellCommandPolicy.Off : ShellCommandPolicy.Ask);
        var decisions = new ScriptedDecisionHandler("deny");
        var provider = new FakeChatProvider(Turn.Says(Plan), Turn.Calls1("docker", """{"args":["version"]}"""), Turn.Says("done"));
        await fx.RunAsync(fx.Build(provider, worker: EngineFixture.WorkerWith("docker"), policy: policy,
            decisions: decisions), "check docker");
        if (mode == "ask")
        {
            Assert.True(Assert.Single(decisions.Requests).SessionOnly);
            Assert.False(decisions.Requests[0].MayBeRemembered);
        }
        else Assert.Empty(decisions.Requests);
    }

    [Theory]
    [InlineData("git")]
    [InlineData("docker")]
    public void Legacy_git_workspace_approval_is_ignored_without_removing_other_approvals(string tool)
    {
        using var fx = new EngineFixture();
        var file = fx.PathOf("approvals.json");
        var key = WorkspaceInfo.IdFor(fx.Root).ToString("N");
        var json = JsonSerializer.Serialize(new Dictionary<string, string[]> { [key] = [tool, "read_file"] });
        File.WriteAllText(file, json);
        var store = new ApprovalStore(file);
        Assert.False(store.Approves(fx.Root, tool));
        Assert.True(store.Approves(fx.Root, "read_file"));
        Assert.Equal(json, File.ReadAllText(file));
    }

    [Fact]
    public async Task Ordinary_local_git_operations_still_work()
    {
        using var fx = new EngineFixture();
        foreach (var args in new[] { new[] { "init", "-q" }, new[] { "status", "--short" } })
        {
            var result = await fx.Invoke(new GitTool(), JsonSerializer.Serialize(new { args }));
            Assert.True(result.Success, result.Error);
        }
    }
}
