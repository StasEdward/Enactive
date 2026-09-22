namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Tools;
using Xunit;

/// <summary>
/// What <c>list_dir</c> says about a path that is a file.
///
/// <para>From a real run on 2026-09-20: the model called <c>list_dir</c> on
/// <c>TicTacToe/TicTacToe.csproj</c>, was told "Directory not found", and called it AGAIN on the
/// same path before working out for itself that it wanted <c>read_file</c>. Two turns and two
/// model calls, spent on a message that described the wrong problem — the path was right, the
/// tool was wrong, and the answer said the opposite.</para>
///
/// <para>The mirror of a care <c>delete_file</c> already takes: it checks for a directory BEFORE
/// checking existence, so a folder is not reported as a file that is not there.</para>
/// </summary>
public sealed class ListDirOnAFileTests
{
    private static string Args(string path)
        => $$"""{"path": {{JsonSerializer.Serialize(path)}} }""";

    [Fact]
    public async Task A_file_is_reported_as_a_file_and_not_as_a_missing_directory()
    {
        using var fx = new EngineFixture();
        fx.Write("project.csproj", "<Project />");

        var result = await fx.Invoke(new ListDirectoryTool(), Args("project.csproj"));

        Assert.Contains("is a file, not a directory", result.Output ?? result.Error ?? "",
                        StringComparison.Ordinal);
        Assert.Contains("read_file", result.Output ?? result.Error ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// And it stays an ANSWER rather than a failure. A lookup that found something, and said what
    /// it found, has not gone wrong — the engine counts a failed call against the step, and
    /// pointing at the right tool is not a fault.
    /// </summary>
    [Fact]
    public async Task Pointing_at_the_right_tool_is_not_a_failure()
    {
        using var fx = new EngineFixture();
        fx.Write("project.csproj", "<Project />");

        var result = await fx.Invoke(new ListDirectoryTool(), Args("project.csproj"));

        // IsAnswer is what the engine reads: a lookup that answered does not count against the
        // step, where an ordinary failure does.
        Assert.True(result.IsAnswer, "pointing at the right tool was recorded as a failure");
    }

    /// <summary>A path that really is not there still says so, or the new message means nothing.</summary>
    [Fact]
    public async Task A_path_that_is_not_there_is_still_a_missing_directory()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new ListDirectoryTool(), Args("nowhere/at/all"));

        var text = result.Output ?? result.Error ?? "";
        Assert.Contains("Directory not found", text, StringComparison.Ordinal);
        Assert.DoesNotContain("is a file", text, StringComparison.Ordinal);
    }

    /// <summary>And a real directory still lists, which is the thing this tool is for.</summary>
    [Fact]
    public async Task A_directory_still_lists()
    {
        using var fx = new EngineFixture();
        fx.Write("src/a.cs", "//");
        fx.Write("src/b.cs", "//");

        var result = await fx.Invoke(new ListDirectoryTool(), Args("src"));

        Assert.True(result.Success, result.Error);
        Assert.Contains("a.cs", result.Output, StringComparison.Ordinal);
        Assert.Contains("b.cs", result.Output, StringComparison.Ordinal);
    }
}
