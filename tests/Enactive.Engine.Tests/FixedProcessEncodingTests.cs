namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Tools;
using Xunit;

[Collection("processes")]
public sealed class FixedProcessEncodingTests
{
    [WindowsFact]
    public async Task Fixed_executable_runner_decodes_both_utf8_streams()
    {
        using var fx = new EngineFixture();
        const string marker = "Привет — 日本語";
        var bytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(marker + "\n"));
        var script = "$b=[Convert]::FromBase64String('" + bytes + "');"
            + "[Console]::OpenStandardOutput().Write($b,0,$b.Length);"
            + "[Console]::OpenStandardError().Write($b,0,$b.Length)";
        var result = await ProcessExec.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
            fx.Root, 20, default);
        Assert.True(result.Success, result.Error);
        Assert.Equal(2, (result.Output ?? "").Split(marker).Length - 1);
    }
}
