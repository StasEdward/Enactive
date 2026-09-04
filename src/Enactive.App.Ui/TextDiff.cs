using System.Text;

namespace Enactive.App.Ui;

/// <summary>Minimal LCS-based line diff for the staging preview.</summary>
internal static class TextDiff
{
    public static string Unified(string? oldText, string newText, string path)
    {
        var oldLines = oldText is null
            ? Array.Empty<string>()
            : oldText.Replace("\r\n", "\n").Split('\n');
        var newLines = newText.Replace("\r\n", "\n").Split('\n');

        int n = oldLines.Length, m = newLines.Length;
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
}
