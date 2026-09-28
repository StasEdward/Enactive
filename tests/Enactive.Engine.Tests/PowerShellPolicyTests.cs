namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Execution;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

public sealed class PowerShellPolicyTests
{
    private static TaskActionPolicy Policy => new(["run_powershell"], ["dotnet build", "dotnet run", "Get-Content", "Select-Object", "Write-Output", "Set-Content"],
        "Only the listed command families.", "Explicit command allowlist.");
    private static string Args(string script) => JsonSerializer.Serialize(new { script });

    [WindowsFact]
    public async Task Parser_accepts_static_sequences_pipelines_and_literal_quoted_data()
    {
        foreach (var script in new[] {
            "dotnet run --project 'folder with spaces/app.csproj'",
            "dotnet build; dotnet run",
            "DOTNET RUN\n# a comment\nGet-Content 'a.txt' | Select-Object -First 1",
            "Write-Output '$(Remove-Item a.txt); &|>'",
            "Write-Output \"plain text\"",
            "Get-Content -LiteralPath:'a.txt'"
        })
            Assert.Null(await PowerShellPolicy.ValidateAsync(Args(script), Policy, [], default));
    }

    [WindowsFact]
    public async Task Parser_refuses_unchecked_execution_and_checks_every_pipeline_command()
    {
        foreach (var script in new[] {
            "dotnet runevil", "dotnet test", "dotnet run; Remove-Item a.txt",
            "Get-Content a.txt | Remove-Item",
            "dotnet run $(Remove-Item a.txt)", "Write-Output \"$(Remove-Item a.txt)\"",
            "$x = 'dotnet'; & $x run", "& dotnet run", ". ./script.ps1",
            "Get-Content a.txt | ForEach-Object { Remove-Item a.txt }",
            "[IO.File]::Delete('a.txt')", "dotnet run > a.txt",
            "dotnet --% run & del a.txt", "dotnet run @args",
            "Write-Output (Get-Content a.txt)", "Write-Output 'unterminated",
            "function dotnet { Remove-Item a.txt }; dotnet run",
            "ls", "Microsoft.PowerShell.Management\\Get-Content a.txt",
            "#requires -version 1\nWrite-Output 'x'"
        })
            Assert.NotNull(await PowerShellPolicy.ValidateAsync(Args(script), Policy, [], default));
        var evaluation = Policy with { CommandPrefixes = ["Invoke-Expression", "Invoke-Command", "ls"] };
        foreach (var script in new[] { "Invoke-Expression 'Remove-Item a.txt'", "Invoke-Command 'bad'", "ls" })
            Assert.NotNull(await PowerShellPolicy.ValidateAsync(Args(script), evaluation, [], default));
    }

    [WindowsFact]
    public async Task Registry_refuses_entire_script_before_first_effect_and_dispatches_allowed_script()
    {
        using var fx = new EngineFixture();
        var context = fx.ContextFor() with {
            Context = new WorkContext(null, "workspace", null, null, null, [], []) { ActionPolicy = Policy }
        };
        var registry = new ToolRegistry([new RunPowerShellTool()]);
        var version = registry.WorkspaceVersion(context.WorkspaceId);
        var denied = await registry.InvokeAsync(new("1", "run_powershell",
            Args("Set-Content marker.txt 'should never run'; Remove-Item absent.txt")), context, default);
        Assert.True(denied.DidNotRun);
        Assert.Contains("outside command_prefixes", denied.Error);
        Assert.Equal(version, registry.WorkspaceVersion(context.WorkspaceId));
        Assert.False(File.Exists(Path.Combine(context.WorkspaceRoot, "marker.txt")));
        var allowed = await registry.InvokeAsync(new("2", "run_powershell",
            Args("Write-Output 'policy-ok' | Select-Object -First 1")), context, default);
        Assert.True(allowed.Success, allowed.Error);
        Assert.Contains("policy-ok", allowed.Output);
    }

    [WindowsFact]
    public async Task No_deletion_restriction_overrides_allowed_RemoveItem_inside_sequence()
    {
        var policy = Policy with { CommandPrefixes = ["Write-Output", "Remove-Item"] };
        var error = await PowerShellPolicy.ValidateAsync(Args("Write-Output 'ok'; Remove-Item a.txt"), policy,
            [new(ForbiddenTaskEffect.FileDeletion, "Do not delete files.")], default);
        Assert.Contains("no-deletion", error);
    }

    [Fact]
    public async Task Planner_accepts_PowerShell_capability_without_changing_requested_command_families()
    {
        const string request = "Only local file tools and dotnet build/run commands.";
        var answer = JsonSerializer.Serialize(new {
            sources = new[] { new { id = "O001", assessment = "Explicit allowlist" } },
            checks = Array.Empty<object>(), forbidden_effects = Array.Empty<object>(), unresolved = (string?)null,
            action_policy = new { allowed_tools = new[] { "run_command", "run_powershell" },
                command_prefixes = new[] { "dotnet build", "dotnet run" }, source_quote = request, reason = "Two shells, same command families." }
        });
        var provider = new FakeChatProvider(Turn.Says(answer));
        var result = await PlanCheckReview.RunAsync(new(IntentDisposition.QuickAction, "work", null),
            request, new WorkContext(null, "workspace", null, null, null, [], []), provider, "model",
            new(ExecutionLimits.None, DateTimeOffset.UtcNow), 4096, default,
            tools: EngineFixture.ShippedTools().Select(t => t.Definition).ToArray());
        Assert.Null(result.IncompleteReason);
        Assert.Contains("run_powershell", result.ActionPolicy!.AllowedTools);
        Assert.Equal(new[] { "dotnet build", "dotnet run" }, result.ActionPolicy.CommandPrefixes);
        Assert.Contains("\"commandPolicy\":\"PowerShell\"", provider.Requests[0].Messages[1].Content);
    }
}
