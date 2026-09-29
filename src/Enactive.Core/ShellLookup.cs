namespace Enactive.Core.Context;

using System.Text;
using System.Text.Json;
using Enactive.Core.Tools;

/// <summary>
/// A shell command asking whether something is THERE - a file, a line of text - and the paths it asks
/// about.
///
/// <para><b>Why.</b> Such a command's exit code does not say what it found. <c>findstr</c> exits 1 when
/// no line matches, <c>dir</c> when a file is missing - and also when the path is malformed or access
/// is refused. The engine does not guess which: an exit that is not what the call declared stays a
/// failed call (run 4f1d97, 2026-09-28: three steps that had done their work were left Incomplete by
/// such exits, and forgiving "exit 1" wholesale would have hidden real failures with them). What it
/// does is point at the tools that answer the question structurally - "not there" and "no matches" as
/// results, not failures - and accept their answer, for the same paths, as settling the command.</para>
/// </summary>
public static class ShellLookup
{
    private static readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase)
    {
        "dir", "findstr", "find", "where", "grep", "egrep", "rg", "ls", "test", "test-path",
        "get-childitem", "gci", "select-string", "sls", "type", "cat", "get-content", "gc"
    };

    /// <summary>The lookup program a shell call runs, first in its pipeline, or null when it is not one.</summary>
    public static string? Program(string tool, string? argumentsJson)
        => ShellTools.IsShell(tool) && Command(argumentsJson) is { } command ? ProgramOf(command) : null;

    public static string? ProgramOf(string command)
    {
        var words = Words(command);
        if (words.Count == 0) return null;
        var first = words[0].Trim('"', '\'');
        var name = Path.GetFileNameWithoutExtension(first.Replace('\\', '/').Split('/')[^1]);
        return Programs.Contains(name) ? name.ToLowerInvariant() : null;
    }

    /// <summary>
    /// The workspace-relative paths a lookup names: its operands that look like paths, before any pipe
    /// or redirection, without flags. Empty when it names none the engine can recognise.
    /// </summary>
    public static IReadOnlyList<string> Paths(string tool, string? argumentsJson)
    {
        if (Program(tool, argumentsJson) is null || Command(argumentsJson) is not { } command) return [];
        var words = Words(command);
        var paths = new List<string>();
        foreach (var raw in words.Skip(1))
        {
            if (raw is "|" or "||" or "&&" or ";" or "&" || raw.StartsWith('>') || raw.StartsWith('<')) break;
            var word = raw.Trim('"', '\'');
            if (word.Length == 0 || word.StartsWith('-') || (word.StartsWith('/') && !word.Contains('.'))) continue;
            if (word.Contains(':') && !(word.Length > 1 && word[1] == ':')) continue;          // /c:"x", -Pattern:x
            if (Path.IsPathRooted(word) || word.IndexOfAny(['*', '?']) >= 0) continue;
            if (word.Contains('/') || word.Contains('\\') || Path.HasExtension(word))
                paths.Add(Normal(word));
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// How two tools' paths are compared: separators and a leading "./" are spelling. Only "./" and "/" come off the
    /// front - not a dot that is part of a name: it trimmed every leading dot, and ".enactive/scratch" became
    /// "enactive/scratch", a path that is not there (found 2026-09-29, when a read-only step's scratch notes were
    /// refused as a change to the work).
    /// </summary>
    public static string Normal(string path)
    {
        var p = path.Replace('\\', '/').Trim();
        while (true)
        {
            if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
            else if (p.StartsWith('/')) p = p[1..];
            else break;
        }
        return p == "." ? "" : p.TrimEnd('/');
    }

    private static string? Command(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { ToolArguments.Command, ToolArguments.Script })
                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Words, with quoted runs kept whole and pipeline symbols as words of their own.</summary>
    private static List<string> Words(string command)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        void Flush() { if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); } }
        foreach (var c in command)
        {
            if (quote is not null) { current.Append(c); if (c == quote) quote = null; continue; }
            if (c is '"' or '\'') { quote = c; current.Append(c); continue; }
            if (char.IsWhiteSpace(c)) { Flush(); continue; }
            if (c is '|' or ';' or '&' or '>' or '<') { Flush(); words.Add(c.ToString()); continue; }
            current.Append(c);
        }
        Flush();
        return words;
    }
}
