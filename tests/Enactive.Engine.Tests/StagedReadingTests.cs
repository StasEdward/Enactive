namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Tools;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

public sealed class StagedReadingTests
{
    [Theory]
    [InlineData("single")]
    [InlineData("paths")]
    [InlineData("read_files")]
    public async Task Pending_replacements_and_creations_are_read_with_their_own_coverage(string form)
    {
        using var fx = new EngineFixture();
        fx.Write("old.txt", "OLD");
        fx.Write("disk.txt", "DISK");
        var staging = new StagingArtifactStore(fx.Root);
        var contents = new Dictionary<string, string>
        {
            ["old.txt"] = "NEW\nsecond\nthird",
            ["new.txt"] = "CREATED\nsecond",
            ["empty.txt"] = ""
        };
        foreach (var (path, content) in contents)
            Assert.True((await fx.Invoke(new WriteFileTool(),
                JsonSerializer.Serialize(new { path, content }), staging)).Success);

        contents["disk.txt"] = "DISK";
        if (form == "single")
        {
            foreach (var (path, content) in contents)
            {
                var result = await fx.Invoke(new ReadFileTool(),
                    JsonSerializer.Serialize(new { path }), staging);
                Assert.True(result.Success, result.Error);
                Assert.Equal(content, result.Output);
                Assert.Equal(ReadFileTool.LinesIn(content), result.Metadata!["totalLines"]);
                Assert.Equal(ReadFileTool.LinesIn(content), result.Metadata["lastLine"]);
                Assert.Equal(path != "disk.txt", result.Metadata["staged"]);
            }
        }
        else
        {
            ITool tool = form == "paths" ? new ReadFileTool() : new ReadFilesTool();
            var result = await fx.Invoke(tool,
                JsonSerializer.Serialize(new { paths = contents.Keys }), staging);
            Assert.True(result.Success, result.Error);
            Assert.DoesNotContain("OLD", result.Output);
            Assert.DoesNotContain("not there", result.Output);
            var coverage = Assert.IsType<List<FileCoverage>>(result.Metadata!["files"]);
            foreach (var (path, content) in contents)
            {
                Assert.Contains(content, result.Output);
                var file = Assert.Single(coverage, c => c.Path == path);
                Assert.Equal(ReadFileTool.LinesIn(content), file.TotalLines);
                Assert.Equal(file.TotalLines, file.LinesShownWhole);
            }
            Assert.Equal(new[] { "old.txt", "new.txt", "empty.txt" },
                Assert.IsType<List<string>>(result.Metadata["stagedPaths"]));
        }

        Assert.Equal("OLD", fx.Read("old.txt"));
        Assert.False(File.Exists(Path.Combine(fx.Root, "new.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_staged_excerpt_is_not_reported_as_a_whole_file(bool delegated)
    {
        using var fx = new EngineFixture();
        fx.Write("old.txt", "short disk version");
        var staging = new StagingArtifactStore(fx.Root);
        var content = "HEAD\n" + new string('x', 9000) + "\nTAIL";
        await fx.Invoke(new WriteFileTool(),
            JsonSerializer.Serialize(new { path = "old.txt", content }), staging);
        ITool tool = delegated ? new ReadFileTool() : new ReadFilesTool();
        var result = await fx.Invoke(tool, """{"paths":["old.txt"]}""", staging);
        Assert.True(result.Success, result.Error);
        Assert.Contains("HEAD", result.Output);
        Assert.Contains("TAIL", result.Output);
        var file = Assert.Single(Assert.IsType<List<FileCoverage>>(result.Metadata!["files"]));
        Assert.Equal(3, file.TotalLines);
        Assert.Equal(0, file.LinesShownWhole);
    }
}
