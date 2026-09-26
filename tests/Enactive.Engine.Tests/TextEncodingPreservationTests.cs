namespace Enactive.Engine.Tests;

using System.Text;
using System.Text.Json;
using Enactive.Core.Artifacts;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

public sealed class TextEncodingPreservationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Malformed_text_is_not_silently_reencoded(bool staged, bool append)
    {
        using var fx = new EngineFixture();
        byte[] bytes = [0xef, 0xbb, 0xbf, (byte)'o', (byte)'l', (byte)'d', 0xff];
        File.WriteAllBytes(fx.PathOf("bad.txt"), bytes);
        var proposals = new StagingArtifactStore(fx.Root);
        IArtifactStore store = staged ? proposals.BeginStep() : fx.Artifacts;
        if (staged)
            await store.CreateAsync("bad.txt", ArtifactKind.FileSet, "text/plain", async stream => await stream.WriteAsync(bytes), default);
        var before = proposals.Changes.Count;
        var result = append
            ? await fx.Invoke(new WriteFileTool(), """{"path":"bad.txt","content":"extra","append":true}""", store)
            : await fx.Invoke(new EditFileTool(), """{"path":"bad.txt","old_string":"old","new_string":"new"}""", store);
        Assert.False(result.Success);
        Assert.Equal(bytes, File.ReadAllBytes(fx.PathOf("bad.txt")));
        Assert.Equal(before, proposals.Changes.Count);
    }

    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_shell_does_not_start_a_process(bool powershell)
    {
        using var fx = new EngineFixture();
        var ct = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (powershell) await new RunPowerShellTool().InvokeAsync("""{"script":"Set-Content marker.txt started"}""", fx.ContextFor(), ct);
            else await new RunCommandTool().InvokeAsync("""{"command":"echo started > marker.txt"}""", fx.ContextFor(), ct);
        });
        Assert.False(fx.Exists("marker.txt"));
    }

    [Theory]
    [InlineData("utf8", false)]
    [InlineData("utf16", false)]
    [InlineData("utf16be", false)]
    [InlineData("utf32", false)]
    [InlineData("utf8", true)]
    [InlineData("utf16", true)]
    public async Task Edit_then_append_preserve_original_encoding_and_preamble(string kind, bool staged)
    {
        Encoding encoding = kind switch { "utf16" => Encoding.Unicode, "utf16be" => Encoding.BigEndianUnicode,
            "utf32" => Encoding.UTF32, _ => new UTF8Encoding(true) };
        using var fx = new EngineFixture();
        byte[] Encoded(string text) => [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
        File.WriteAllBytes(fx.PathOf("text.txt"), Encoded("Привет old\r\n"));
        var proposals = new StagingArtifactStore(fx.Root);
        IArtifactStore store = staged ? proposals.BeginStep() : fx.Artifacts;
        await fx.Invoke(new EditFileTool(), """{"path":"text.txt","old_string":"old","new_string":"new"}""", store);
        await fx.Invoke(new WriteFileTool(), JsonSerializer.Serialize(new { path = "text.txt", content = "ещё\n", append = true }), store);
        if (staged)
        {
            Assert.Equal(2, proposals.Changes.Count);
            foreach (var change in proposals.Changes) Assert.True(proposals.Apply(change.Id).Applied);
        }
        Assert.Equal(Encoded("Привет new\r\nещё\r\n"), File.ReadAllBytes(fx.PathOf("text.txt")));
    }

    [WindowsFact]
    public async Task Shell_standard_input_is_closed()
    {
        using var fx = new EngineFixture();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var script = "if ($null -eq [Console]::ReadLine()) { [Console]::WriteLine('EOF'); exit 0 }; exit 1";
        var result = await new RunPowerShellTool().InvokeAsync(JsonSerializer.Serialize(new { script }), fx.ContextFor(), limit.Token);
        Assert.Contains("EOF", result.Output);
    }
}
