using System.Text;

namespace Enactive.Core.Execution;

/// <summary>
/// Minimal LCS-based line diff - the staging preview's (Workspace.TextDiff), here so the engine can show a reviewer what a
/// staged step proposes to change in a protected file (Plan Phase 3.4): a staged run's change is a proposal in memory, and
/// no git diff can see it.
///
/// <para>LCS costs one <c>int</c> per pair of lines, in time and in memory, and the preview runs on the UI
/// thread while a proposal card is being built. Two files of ten thousand lines each is a hundred
/// million cells: four hundred megabytes and a frozen window, decided entirely by a file the agent
/// chose to write. Past <see cref="MaxCells"/> it does not attempt the comparison and says so — a
/// diff nobody could read is not worth the machine it would take to produce.</para>
/// </summary>
public static class LineDiff
{
    /// <summary>
    /// Line pairs the comparison may cost. 4 million is a 2000×2000 file pair at 16 MB of matrix —
    /// well past any diff a person reads in a preview pane, and far short of what makes the window
    /// stop responding.
    /// </summary>
    private const long MaxCells = 4_000_000;

    /// <param name="tooLarge">What to tell the reader to do when the files are too large to compare - the preview's and a
    /// review's advice differ.</param>
    public static string Unified(string? oldText, string newText, string path, string tooLarge = "Open the file to review it.")
    {
        var oldLines = oldText is null
            ? Array.Empty<string>()
            : oldText.Replace("\r\n", "\n").Split('\n');
        var newLines = newText.Replace("\r\n", "\n").Split('\n');

        int n = oldLines.Length, m = newLines.Length;

        if ((long)(n + 1) * (m + 1) > MaxCells)
            return TooLarge(oldText, path, n, m, tooLarge);

        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = oldLines[i] == newLines[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var sb = new StringBuilder();
        sb.AppendLine(oldText is null ? $"# new file: {path}" : $"# modified: {path}");
        sb.AppendLine();

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (oldLines[x] == newLines[y]) { sb.AppendLine("  " + oldLines[x]); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { sb.AppendLine("- " + oldLines[x]); x++; }
            else { sb.AppendLine("+ " + newLines[y]); y++; }
        }
        while (x < n) { sb.AppendLine("- " + oldLines[x]); x++; }
        while (y < m) { sb.AppendLine("+ " + newLines[y]); y++; }

        return sb.ToString();
    }

    /// <summary>
    /// What is said instead of a diff nobody would read. The counts are the honest part: something
    /// this size is reviewed by opening the file, not by scrolling a preview.
    /// </summary>
    private static string TooLarge(string? oldText, string path, int oldLines, int newLines, string advice)
    {
        var sb = new StringBuilder();
        sb.AppendLine(oldText is null ? $"# new file: {path}" : $"# modified: {path}");
        sb.AppendLine();
        sb.AppendLine(oldText is null
            ? $"  {newLines} lines — too large to show line by line."
            : $"  {oldLines} lines → {newLines} lines — too large to compare line by line.");
        sb.AppendLine();
        sb.AppendLine("  " + advice);
        return sb.ToString();
    }
}
