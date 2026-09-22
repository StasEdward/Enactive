namespace Enactive.Tools;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Whether two files say the same thing — as a verdict, not as two files in the prompt.
///
/// <para><b>The call this replaces.</b> "Is this document the one I already have?" was answerable
/// only by reading both and judging, which costs both files and produces an opinion. On 2026-09-12
/// the question decided a whole day's measurement: a run had passed both review gates on a
/// <c>DRIFT_ollama.md</c> it claimed to have written, and the file turned out to be an earlier
/// artifact reproduced character for character. Settling that took one comparison and returned one
/// word. Reading both files would have cost 12k characters and ended in a judgement call
///.</para>
///
/// <para><b>Two modes, because there are two questions.</b> Exact: byte-for-byte by line, answering
/// "did my write land" and "have these drifted". Whitespace-normalised: every run of whitespace in
/// the WHOLE file — newlines included — collapsed to one space, answering "is this the same text,
/// reflowed". The second is not the first with a flag: two files whose only difference is where the
/// lines wrap have different line counts, so a per-line comparison calls them different at line 1
/// and says nothing useful.</para>
///
/// <para>A staged write counts as the file's content, exactly as <c>read_file</c> treats it — a
/// comparison that read past a proposal to the older disk copy would answer a question nobody
/// asked.</para>
/// </summary>
public sealed class CompareFilesTool : ITool
{
    private const int MaxShownChars = 240;

    public ToolDefinition Definition { get; } = new(
        Name: "compare_files",
        Description: "Compare two files and report whether they are identical, or where they first "
                   + "differ — without returning their contents. Set ignore_whitespace to compare "
                   + "the text with all whitespace collapsed, which answers \"is this the same "
                   + "content, reflowed\". Use it instead of reading both files to judge by eye.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Observe;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? pathA, pathB;
        bool normalise;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            pathA = Text(root, "a");
            pathB = Text(root, "b");
            normalise = root.TryGetProperty("ignore_whitespace", out var w)
                        && w.ValueKind == JsonValueKind.True;
        }
        catch (JsonException ex)
        {
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
            return ToolResults.Unreadable("'a' and 'b' are both required — two file paths to compare.");

        var a = await LoadAsync(pathA, ctx, ct);
        if (a.Error is not null) return a.Error;

        var b = await LoadAsync(pathB, ctx, ct);
        if (b.Error is not null) return b.Error;

        // Comparing a file with itself is a question that answers itself, and answering "identical"
        // would let a verification pass that verified nothing. Say what happened instead.
        if (string.Equals(a.Text, b.Text, StringComparison.Ordinal)
            && string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase))
        {
            return ToolResults.Ok(
                output: $"'{pathA}' and '{pathB}' are the same file. Nothing was compared.",
                metadata: new Dictionary<string, object?> { ["identical"] = true, ["samePath"] = true });
        }

        return normalise
            ? Normalised(pathA!, pathB!, a.Text!, b.Text!)
            : Exact(pathA!, pathB!, a.Text!, b.Text!);
    }

    // ── exact ─────────────────────────────────────────────────────────────────────────

    private static ToolResult Exact(string pathA, string pathB, string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return ToolResults.Ok(
                output: $"Identical: '{pathA}' and '{pathB}' are byte-for-byte the same "
                      + $"({Lines(a).Length} line(s), {a.Length} character(s)).",
                metadata: Meta(true, false, null, 0, Lines(a).Length, Lines(b).Length));

        var linesA = Lines(a);
        var linesB = Lines(b);

        int? firstDiff = null;
        var differing = 0;
        var common = Math.Max(linesA.Length, linesB.Length);

        for (var i = 0; i < common; i++)
        {
            var left = i < linesA.Length ? linesA[i] : null;
            var right = i < linesB.Length ? linesB[i] : null;
            if (string.Equals(left, right, StringComparison.Ordinal)) continue;

            differing++;
            firstDiff ??= i + 1;
        }

        var at = firstDiff!.Value;
        var output = new StringBuilder();
        output.Append("Different. ").Append(differing).Append(" line(s) differ; first at line ")
              .Append(at).AppendLine(".");
        output.Append(pathA).Append(" (").Append(linesA.Length).Append(" lines) line ").Append(at)
              .Append(": ").AppendLine(Show(at <= linesA.Length ? linesA[at - 1] : null));
        output.Append(pathB).Append(" (").Append(linesB.Length).Append(" lines) line ").Append(at)
              .Append(": ").AppendLine(Show(at <= linesB.Length ? linesB[at - 1] : null));

        // Named rather than left to be inferred from the two line counts, because it is the usual
        // reason an exact comparison is unhelpful and the normalised one is what was wanted.
        if (linesA.Length != linesB.Length)
            output.AppendLine("The files have different line counts. If they may be the same text "
                            + "wrapped differently, compare again with ignore_whitespace.");

        return ToolResults.Ok(
            output: output.ToString().TrimEnd(),
            metadata: Meta(false, false, at, differing, linesA.Length, linesB.Length));
    }

    // ── whitespace-normalised ─────────────────────────────────────────────────────────

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static ToolResult Normalised(string pathA, string pathB, string a, string b)
    {
        var na = Whitespace.Replace(a, " ").Trim();
        var nb = Whitespace.Replace(b, " ").Trim();

        if (string.Equals(na, nb, StringComparison.Ordinal))
            return ToolResults.Ok(
                output: $"Identical after whitespace normalisation: '{pathA}' and '{pathB}' are the "
                      + $"same text ({na.Length} character(s)) differing only in spacing or line "
                      + "breaks.",
                metadata: Meta(true, true, null, 0, Lines(a).Length, Lines(b).Length));

        var limit = Math.Min(na.Length, nb.Length);
        var at = limit;
        for (var i = 0; i < limit; i++)
        {
            if (na[i] == nb[i]) continue;
            at = i;
            break;
        }

        var output = new StringBuilder();
        output.Append("Different after whitespace normalisation. ")
              .Append(pathA).Append(' ').Append(na.Length).Append(" chars, ")
              .Append(pathB).Append(' ').Append(nb.Length)
              .Append(" chars; first difference at character ").Append(at + 1).AppendLine(".");
        output.Append(pathA).Append(": …").AppendLine(Excerpt(na, at));
        output.Append(pathB).Append(": …").AppendLine(Excerpt(nb, at));

        return ToolResults.Ok(
            output: output.ToString().TrimEnd(),
            metadata: Meta(false, true, null, 0, Lines(a).Length, Lines(b).Length,
                           charsA: na.Length, charsB: nb.Length, firstDiffChar: at + 1));
    }

    private static string Excerpt(string text, int at)
    {
        var from = Math.Max(0, at - 40);
        var length = Math.Min(text.Length - from, 160);
        var slice = text.Substring(from, length);
        return slice + (from + length < text.Length ? "…" : "");
    }

    // ── loading ───────────────────────────────────────────────────────────────────────

    private readonly record struct Loaded(string? Text, ToolResult? Error);

    private static async Task<Loaded> LoadAsync(string? path, ToolContext ctx, CancellationToken ct)
    {
        string full;
        try { full = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, path); }
        catch (ArgumentException ex) { return new Loaded(null, ToolResults.Fail(ex.Message)); }

        var staged = await ctx.Artifacts.TryReadPendingAsync(path!, ct);
        if (staged is not null)
            return new Loaded(staged, null);

        if (!File.Exists(full))
            return new Loaded(null, ToolResults.NotFound($"File not found: {path}"));

        var size = new FileInfo(full).Length;
        if (size > WorkspaceScan.MaxFileBytes)
            return new Loaded(null, ToolResults.Fail(
                $"'{path}' is {size / (1024 * 1024)} MB, over the "
                + $"{WorkspaceScan.MaxFileBytes / (1024 * 1024)} MB comparison limit. "
                + "Compare a smaller part with read_file, or count what you are looking for with "
                + "count_matches."));

        try { return new Loaded(await File.ReadAllTextAsync(full, ct), null); }
        catch (Exception ex) { return new Loaded(null, ToolResults.Fail($"Could not read '{path}': {ex.Message}")); }
    }

    private static string[] Lines(string text) => text.Split('\n');

    private static string Show(string? line)
    {
        if (line is null) return "(no such line — the file ends before it)";
        var shown = line.TrimEnd('\r');
        return shown.Length > MaxShownChars ? shown[..MaxShownChars] + "…" : shown;
    }

    private static Dictionary<string, object?> Meta(
        bool identical, bool normalised, int? firstDiffLine, int differingLines,
        int linesA, int linesB, int? charsA = null, int? charsB = null, int? firstDiffChar = null)
        => new()
        {
            ["identical"] = identical,
            ["normalised"] = normalised,
            ["firstDifferingLine"] = firstDiffLine,
            ["differingLines"] = differingLines,
            ["linesA"] = linesA,
            ["linesB"] = linesB,
            ["charsA"] = charsA,
            ["charsB"] = charsB,
            ["firstDifferingChar"] = firstDiffChar
        };

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private const string Schema = """
    {
      "type": "object",
      "properties": {
        "a": { "type": "string", "description": "First file, relative to the workspace root." },
        "b": { "type": "string", "description": "Second file, relative to the workspace root." },
        "ignore_whitespace": { "type": "boolean", "description": "Collapse every run of whitespace in both files — newlines included — before comparing. Answers whether the two are the same text wrapped differently. Default false." }
      },
      "required": ["a", "b"]
    }
    """;
}
