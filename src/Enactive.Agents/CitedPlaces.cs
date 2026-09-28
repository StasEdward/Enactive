namespace Enactive.Agents;

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Enactive.Core.Context;

/// <summary>
/// The places a step's report and result point at - "Program.cs:223", "src/X.cs:40-52" - opened by the
/// engine itself when the step is reviewed, and a quote written beside such a place checked against the
/// lines it names.
///
/// <para><b>Why.</b> The plan's rule is that what code can check, code checks, and a reviewer judges meaning
/// with evidence the engine recorded. Claims about code were being judged by a reviewer from what the step
/// happened to have read, cut to fit: run 9c1a061b ended Incomplete over "Program.cs:223 sets Timeout =
/// InfiniteTimeSpan" because "the shown reads of Program.cs do not include line 223" - the read was among the
/// 44 oldest calls, not shown. Whether line 223 says that is not a matter of judgement.</para>
///
/// <para><b>What it is not.</b> Not proof the step read the place, and not a verdict on the claim: it is the
/// workspace as it stands when the review is asked, recorded by the engine and said to be so. A path that
/// names several files is not guessed; a place past the end of its file says so.</para>
/// </summary>
internal static partial class CitedPlaces
{
    /// <summary>The tool name these observations are recorded under - no tool the model can call.</summary>
    internal const string ToolName = "engine_opened_cited_place";

    internal const int MaxPlaces = 16;
    private const int Around = 2;
    private const int MaxLines = 30;
    private const int MaxLineChars = 300;

    [GeneratedRegex(@"(?<path>[A-Za-z0-9_.\-/\\]*[A-Za-z0-9_\-]\.[A-Za-z0-9]{1,8}):(?<line>\d{1,6})(?:\s*[-–]\s*(?<end>\d{1,6}))?")]
    private static partial Regex Cite();

    [GeneratedRegex(@"`(?<quote>[^`\r\n]{4,200})`")]
    private static partial Regex Quote();

    /// <summary>The text values of a handed-on result, whatever its shape, as one text to look for places in.</summary>
    internal static string TextOf(JsonNode? node)
    {
        var sb = new StringBuilder();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonValue v when v.TryGetValue<string>(out var s): sb.AppendLine(s); break;
                case JsonObject o: foreach (var (key, value) in o) { sb.AppendLine(key); Walk(value); } break;
                case JsonArray a: foreach (var x in a) Walk(x); break;
            }
        }
        Walk(node);
        return sb.ToString();
    }

    /// <summary>
    /// What the engine found at each place <paramref name="text"/> names: (the place as cited, what is there).
    /// At most <see cref="MaxPlaces"/>, each once, in the order first cited.
    /// </summary>
    /// <param name="files">Every workspace-relative file a sweep would find, for a place cited by its name alone.</param>
    internal static IReadOnlyList<(string Cited, string Observed)> Observe(string text, string root, Func<IReadOnlyList<string>> files)
    {
        var found = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string>? all = null;

        foreach (var line in text.Split('\n'))
        foreach (Match m in Cite().Matches(line))
        {
            if (found.Count >= MaxPlaces) return found;
            var cited = m.Value.Trim();
            var path = m.Groups["path"].Value.Replace('\\', '/').TrimStart('.', '/');
            if (!int.TryParse(m.Groups["line"].Value, out var first) || first < 1) continue;
            var last = m.Groups["end"].Success && int.TryParse(m.Groups["end"].Value, out var e) && e >= first ? e : first;
            if (!seen.Add($"{path}:{first}-{last}")) continue;

            var resolved = Resolve(path, root, () => all ??= files());
            if (resolved.Path is null)
            {
                found.Add((cited, resolved.Why!));
                continue;
            }

            string[] lines;
            try { lines = File.ReadAllLines(WorkspaceGuard.ResolveInside(root, resolved.Path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                found.Add((cited, $"{resolved.Path} could not be read: {ex.Message}"));
                continue;
            }
            if (first > lines.Length)
            {
                found.Add((cited, $"{resolved.Path} has {lines.Length} line(s): line {first} does not exist."));
                continue;
            }

            var from = Math.Max(1, first - Around);
            var to = Math.Min(lines.Length, Math.Min(last, first + MaxLines - 1) + Around);
            var sb = new StringBuilder($"Opened by the engine when this step was reviewed - not a call of the step's: {resolved.Path}, "
                                       + $"lines {from}-{to} of {lines.Length}" + (resolved.Path != path ? $" (cited as '{path}')" : "") + ":\n");
            for (var n = from; n <= to; n++)
            {
                var content = lines[n - 1];
                if (content.Length > MaxLineChars)
                    content = content[..MaxLineChars] + $" … [line cut at {MaxLineChars} of its {content.Length} characters]";
                sb.Append(n).Append(": ").AppendLine(content);
            }
            // A range longer than is shown says so: a cut nobody is told about reads as the whole.
            if (last - first + 1 > MaxLines)
                sb.AppendLine($"[the cited range runs to line {last}; the engine opened its first {MaxLines} lines]");

            // A quote written beside the place, checked against it - found there, found elsewhere, or not in the file.
            foreach (Match q in Quote().Matches(line))
            {
                var quote = q.Groups["quote"].Value.Trim();
                if (quote.Contains(':') && Cite().IsMatch(quote)) continue;          // the place itself, in backticks
                var near = FirstLine(lines, quote, from, to);
                var anywhere = near ?? FirstLine(lines, quote, 1, lines.Length);
                sb.Append($"Quoted beside it: `{quote}` - ")
                  .AppendLine(near is { } at ? $"at line {at}."
                      : anywhere is { } other ? $"NOT at the cited place; it is at line {other}."
                      : $"NOT in {resolved.Path}.");
            }
            found.Add((cited, sb.ToString().TrimEnd()));
        }
        return found;
    }

    private static int? FirstLine(string[] lines, string quote, int from, int to)
    {
        var wanted = Squash(quote);
        for (var n = from; n <= to && n <= lines.Length; n++)
            if (Squash(lines[n - 1]).Contains(wanted, StringComparison.Ordinal))
                return n;
        return null;
    }

    /// <summary>
    /// Every workspace-relative file a sweep would find - what every sweep skips and what the root .gitignore
    /// excludes by name left out - for a place cited by its file name alone.
    /// </summary>
    internal static IReadOnlyList<string> Sweep(string root)
    {
        var skipped = new HashSet<string>(WorkspaceGuard.SkippedFolders, StringComparer.OrdinalIgnoreCase);
        var ignored = WorkspaceGuard.IgnoredFolders(root).Select(f => Path.GetFullPath(Path.Combine(root, f)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        var pending = new Stack<string>([root]);
        while (pending.Count > 0 && found.Count < 50_000)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    if (!skipped.Contains(Path.GetFileName(sub)) && !ignored.Contains(Path.GetFullPath(sub)))
                        pending.Push(sub);
                foreach (var file in Directory.EnumerateFiles(dir))
                    found.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    private static string Squash(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The one file a cited path names: as written, or the one file whose path ends with it. Never a guess.</summary>
    private static (string? Path, string? Why) Resolve(string path, string root, Func<IReadOnlyList<string>> files)
    {
        try
        {
            if (File.Exists(WorkspaceGuard.ResolveInside(root, path))) return (path, null);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }

        var ending = "/" + path;
        var matches = files().Where(f => f.EndsWith(ending, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(f, path, StringComparison.OrdinalIgnoreCase)).Take(6).ToArray();
        return matches.Length switch
        {
            1 => (matches[0], null),
            0 => (null, $"No file '{path}' in the workspace."),
            _ => (null, $"'{path}' names more than one file ({string.Join(", ", matches.Take(5))}{(matches.Length > 5 ? ", ..." : "")}), "
                        + "so the engine opened none of them.")
        };
    }
}
