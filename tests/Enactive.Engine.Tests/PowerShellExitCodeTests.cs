namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Tools;
using Xunit;

[Collection("processes")]
public sealed class PowerShellExitCodeTests
{
    [WindowsTheory]
    [InlineData(0, false, true)]
    [InlineData(1, false, false)]
    [InlineData(3, false, false)]
    [InlineData(8, false, false)]
    [InlineData(1, true, true)]
    [InlineData(3, true, true)]
    [InlineData(8, true, true)]
    public async Task Silent_explicit_exit_obeys_the_declared_contract(int code, bool declared, bool success)
    {
        using var fx = new EngineFixture();
        var result = await fx.Invoke(new RunPowerShellTool(), JsonSerializer.Serialize(new
        {
            script = $"exit {code}", expectedExitCodes = declared ? new[] { code } : new[] { 0 }
        }));
        Assert.Equal(success, result.Success);
        Assert.Equal(code, result.Metadata!["exitCode"]);
        Assert.Contains($"exit code {code}", result.Output);
    }

    [WindowsTheory]
    [InlineData("Write-Output 'tests failed'; exit 3", 3)]
    [InlineData("cmd.exe /d /c 'exit /b 8'; exit $LASTEXITCODE", 8)]
    [InlineData("cmd.exe /d /c 'exit /b 8'", 1)]
    [InlineData("Get-Item './absent.txt' -ErrorAction SilentlyContinue", 1)]
    public async Task Missing_stderr_does_not_prove_success(string script, int code)
    {
        using var fx = new EngineFixture();
        var result = await fx.Invoke(new RunPowerShellTool(), JsonSerializer.Serialize(new { script }));
        Assert.False(result.Success);
        Assert.Equal(code, result.Metadata!["exitCode"]);
    }

    [WindowsFact]
    public async Task An_expected_code_does_not_allow_a_different_failure()
    {
        using var fx = new EngineFixture();
        var result = await fx.Invoke(new RunPowerShellTool(),
            """{"script":"exit 8","expectedExitCodes":[3]}""");
        Assert.False(result.Success);
        Assert.Equal(8, result.Metadata!["exitCode"]);
    }

    [WindowsFact]
    public async Task Intentional_absence_can_be_declared_before_execution()
    {
        using var fx = new EngineFixture();
        var result = await fx.Invoke(new RunPowerShellTool(),
            """{"script":"Get-Item './absent.txt' -ErrorAction SilentlyContinue","expectedExitCodes":[1]}""");
        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.Metadata!["exitCode"]);
    }
}
