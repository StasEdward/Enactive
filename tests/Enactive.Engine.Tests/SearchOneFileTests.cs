namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Tools;
using Xunit;

/// <summary>
/// <c>search_files</c> pointed at a single file searches it.
///
/// <para>From a real run on 2026-09-20 22:24: the model asked for
/// <c>error|Error|failed|Passed!|Failed!</c> in <c>build_output.txt</c> and was told <i>"Not a
/// folder in this workspace: build_output.txt"</i> — about a 257-byte file in the workspace root
/// that it had listed one call earlier. It then spent a turn on
/// <c>for %f in (build_output.txt) do @echo %~zf bytes &amp; findstr /n /i "error passed failed"
/// %f</c> to get the thing it had just asked for.</para>
///
/// <para>The same shape as <see cref="ListDirOnAFileTests"/>, a day apart and in the sibling tool:
/// a message that says "wrong path" about a path that is right. There the answer was to name the
/// right tool, because <c>list_dir</c> cannot list a file. Here there is nothing to redirect to —
/// searching one named file is a perfectly good question, so it is answered.</para>
/// </summary>
public sealed class SearchOneFileTests
{
    private static string Args(string pattern, string path)
        => $$"""{"pattern": {{JsonSerializer.Serialize(pattern)}}, "path": {{JsonSerializer.Serialize(path)}} }""";

    [Fact]
    public async Task A_named_file_is_searched_rather_than_refused()
    {
        using var fx = new EngineFixture();
        fx.Write("build_output.txt", "Determining projects to restore...\n0 Error(s)\nBuild succeeded.\n");

        var result = await fx.Invoke(new SearchFilesTool(), Args("Error", "build_output.txt"));

        Assert.True(result.Success, result.Error);
        Assert.Contains("build_output.txt:2", result.Output ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// "Nothing in that file" is an answer too, and it must not read as "that file is not there".
    /// </summary>
    [Fact]
    public async Task A_named_file_with_no_match_says_so_about_one_file()
    {
        using var fx = new EngineFixture();
        fx.Write("build_output.txt", "Build succeeded.\n");

        var result = await fx.Invoke(new SearchFilesTool(), Args("Failed!", "build_output.txt"));

        Assert.True(result.Success, result.Error);
        Assert.Contains("in 1 file(s)", result.Output ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A file named outright is searched even where a whole-workspace sweep would walk past it.
    /// The skip list is about where a WALK wanders; naming the file is the model saying it wants
    /// this one, and <c>.enactive/scratch/</c> is exactly where it was told to put long output.
    /// </summary>
    [Fact]
    public async Task A_file_in_the_working_area_is_searched_when_it_is_named()
    {
        using var fx = new EngineFixture();
        fx.Write(".enactive/scratch/test_run.txt", "Passed!  - Failed: 0, Passed: 94\n");

        var result = await fx.Invoke(
            new SearchFilesTool(), Args("Passed!", ".enactive/scratch/test_run.txt"));

        Assert.True(result.Success, result.Error);
        Assert.Contains("Passed: 94", result.Output ?? "", StringComparison.Ordinal);
    }

    /// <summary>A path that is neither still answers, and still as an answer rather than a fault.</summary>
    [Fact]
    public async Task A_path_that_is_nothing_is_still_answered()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new SearchFilesTool(), Args("x", "nowhere.txt"));

        Assert.False(result.Success);
        Assert.True(result.IsAnswer, "a lookup that found nothing must not hold the step open");
    }
}
