namespace Enactive.Engine.Tests;

using System.Text.RegularExpressions;
using Enactive.Tools;
using Xunit;

/// <summary>
/// A command's output reaches the model as its start and its end; the middle was gone, and the only way to see it was to
/// run the command again - which printed the same thing and was cut the same way. The whole output is now kept in the
/// worker's own area, and the cut names the lines not shown and the read_file call that shows them. The idea is Unsloth
/// Studio's tool-result spill (2026-09-30 review of it); nothing of its code. Deliberately not code: a link check's log.
/// </summary>
public sealed class AKeptOutputIsReadNotRunAgainTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("enactive-kept-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string Log(int lines) => string.Join("\n", Enumerable.Range(1, lines).Select(i => $"link {i:D5}: /wiki/page-{i} -> 200 OK"));

    private static readonly Regex Call = new(@"read_file \{""path"":""(?<path>[^""]+)"",""offset"":(?<offset>\d+),""limit"":(?<limit>\d+)\}");

    /// <summary>THE ONE THAT MATTERS: every line the model was not shown is in the range the cut names, in the file it names.</summary>
    [Fact]
    public void What_was_not_shown_is_kept_and_the_cut_says_how_to_read_it()
    {
        var log = Log(3_000);
        var result = ProcessExec.BuildResult("Command", 0, log, "", workspaceRoot: _root);

        var shown = result.Output!;
        var call = Call.Match(shown);
        Assert.True(call.Success, shown[^400..]);
        var path = call.Groups["path"].Value;
        Assert.StartsWith(ProcessExec.KeptOutputFolder + "/", path, StringComparison.Ordinal);
        Assert.Contains("the whole output, 3000 lines, is kept in", shown, StringComparison.Ordinal);

        var kept = File.ReadAllText(Path.Combine(_root, path));
        Assert.Equal(log, kept);

        var first = int.Parse(call.Groups["offset"].Value);
        var last = first + int.Parse(call.Groups["limit"].Value) - 1;
        var keptLines = kept.Split('\n');
        for (var n = 1; n <= keptLines.Length; n++)
            if (!shown.Contains(keptLines[n - 1], StringComparison.Ordinal))
                Assert.InRange(n, first, last);
        Assert.True(last - first + 1 < keptLines.Length);
    }

    [Fact]
    public void An_output_that_fits_is_not_kept()
    {
        var result = ProcessExec.BuildResult("Command", 0, Log(20), "", workspaceRoot: _root);

        Assert.DoesNotContain("is kept in", result.Output!, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, ProcessExec.KeptOutputFolder)));
    }

    /// <summary>With nowhere to keep it, the cut stands as it was.</summary>
    [Fact]
    public void Without_a_workspace_the_cut_stands_as_it_was()
    {
        var result = ProcessExec.BuildResult("Command", 0, Log(3_000), "");

        Assert.Contains("characters not shown here", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("is kept in", result.Output!, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_newest_kept_outputs_stay()
    {
        for (var i = 0; i < 53; i++)
            ProcessExec.BuildResult("Command", 0, Log(3_000), "", workspaceRoot: _root);

        Assert.Equal(50, Directory.GetFiles(Path.Combine(_root, ProcessExec.KeptOutputFolder), "*.txt").Length);
    }
}
