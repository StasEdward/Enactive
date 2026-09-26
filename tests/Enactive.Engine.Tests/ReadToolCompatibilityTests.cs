namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Tools;
using Xunit;

public sealed class ReadToolCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_batch_entry_points_return_identical_text_and_coverage(bool mixedPath)
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "first\r\nsecond\n");
        fx.Write("b.txt", "HEAD\n" + new string('x', 9000) + "\nTAIL");
        var legacy = await fx.Invoke(new ReadFilesTool(),
            """{"paths":["a.txt","b.txt","missing.txt"]}""");
        var unified = await fx.Invoke(new ReadFileTool(), mixedPath
            ? """{"path":"a.txt","paths":["b.txt","missing.txt"]}"""
            : """{"paths":["a.txt","b.txt","missing.txt"]}""");
        Assert.True(legacy.Success, legacy.Error);
        Assert.Equal(legacy.Output, unified.Output);
        Assert.Equal(JsonSerializer.Serialize(legacy.Metadata), JsonSerializer.Serialize(unified.Metadata));
    }

    [Fact]
    public async Task Both_entry_points_validate_all_batch_paths_before_reading()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "must not be shown");
        const string args = """{"paths":["a.txt","../outside.txt"]}""";
        var legacy = await fx.Invoke(new ReadFilesTool(), args);
        var unified = await fx.Invoke(new ReadFileTool(), args);
        Assert.False(legacy.Success);
        Assert.Equal(legacy.Error, unified.Error);
        Assert.Equal(legacy.Output, unified.Output);
        Assert.True(legacy.DidNotRun);
        Assert.True(unified.DidNotRun);
    }
}
