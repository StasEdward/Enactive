namespace Enactive.Agents;

using Enactive.Core.Tools;

/// <summary>
/// What a step has actually READ of each file, so that a whole-file write of a file it has only
/// seen part of can be refused.
///
/// <para><c>read_file</c> ends a shortened result with "… showing lines 1–400 of 518. Read on with
/// offset 401." On 2026-09-07 a model ignored that line and rewrote the whole file from the part it
/// had read, and the 118 lines it never saw were gone. Announcing a cut is necessary and, on its
/// own, not sufficient: the sentence is advice, and advice can be skipped. <c>FIX_PLAN.md</c> §9b
/// carried this as open with the fix already named — track what the step has read, and treat a
/// whole-file write of a partially-read file as the dangerous case.</para>
///
/// <para>Only a PARTIAL read is dangerous, and this is deliberately narrow about that:</para>
/// <list type="bullet">
/// <item>A file the step read IN FULL is fine — it can reproduce what it saw.</item>
/// <item>A file the step read in SEVERAL windows that join up is read in full. Reading 1–400 and
/// then 401–518 is exactly what the tool asks for, and refusing it would punish the one behaviour
/// this is trying to encourage.</item>
/// <item>A file the step has NOT read at all is not this guard's business. Creating a file, or
/// deliberately overwriting one, is ordinary work; the shrink guard in <c>write_file</c> is what
/// stands between that and a whole-file rewrite meant as a small change.</item>
/// </list>
///
/// <para>Per STEP, like the execution journal and the artifact scope, and for the same reason: it
/// describes what THIS unit of work knows, and a sibling step's reading tells it nothing.</para>
/// </summary>
internal sealed class ReadLedger
{
    /// <summary>How much of one file this step has seen, counting from line 1.</summary>
    private sealed class Coverage
    {
        public int Contiguous;   // the highest line reached without a gap from line 1
        public int Total;        // the file's line count, as the read reported it
    }

    private readonly Dictionary<string, Coverage> _files =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tool whose results this ledger is built from.</summary>
    internal const string ReadTool = "read_file";

    /// <summary>The tool this ledger guards.</summary>
    internal const string WriteTool = "write_file";

    /// <summary>
    /// Records what a successful read covered. Everything it needs is already in the result's
    /// metadata — the tools stay ignorant of each other, and the loop that sees every call and every
    /// result is the one place that can put the two together.
    /// </summary>
    public void Saw(ToolCall call, ToolResult result)
    {
        if (!string.Equals(call.Name, ReadTool, StringComparison.Ordinal) || !result.Success)
            return;

        if (Meta(result, "path") is not string path || string.IsNullOrWhiteSpace(path))
            return;

        var first = Int(result, "firstLine");
        var last = Int(result, "lastLine");
        var total = Int(result, "totalLines");

        if (first is not { } from || last is not { } to || total is not { } lines)
            return;

        var key = Key(path);
        if (!_files.TryGetValue(key, out var coverage))
            _files[key] = coverage = new Coverage();

        coverage.Total = lines;

        // A window that starts at or before the first line not yet seen extends the run; one that
        // starts beyond it leaves a hole, and a hole is exactly what makes a rewrite unsafe.
        if (from <= coverage.Contiguous + 1)
            coverage.Contiguous = Math.Max(coverage.Contiguous, to);
    }

    /// <summary>
    /// Why this write must not go ahead, or null when it may. A whole-file write of a file this step
    /// has seen only part of cannot have been derived from the parts it did not see.
    /// </summary>
    public string? Refuse(ToolCall call, string? path)
    {
        if (!string.Equals(call.Name, WriteTool, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(path))
            return null;

        if (!_files.TryGetValue(Key(path), out var coverage))
            return null;   // never read here - not this guard's business

        if (coverage.Total <= 0 || coverage.Contiguous >= coverage.Total)
            return null;   // read in full, in one window or several

        var next = coverage.Contiguous + 1;
        return $"This step has read only lines 1-{coverage.Contiguous} of {coverage.Total} in "
             + $"'{path}', so a whole-file write would replace {coverage.Total - coverage.Contiguous} "
             + "line(s) it has never seen with whatever it happens to produce. That is how a file "
             + "loses the part nobody looked at.\n"
             + $"Either use edit_file, which replaces one exact passage and leaves the rest alone, or "
             + $"read the remainder first: read_file with \"offset\": {next}.";
    }

    /// <summary>
    /// One name per file. The same normalisation the artifact journal uses and for the same reason:
    /// a model spells one path several ways in a single step, and a guard that can be stepped around
    /// by writing './x' instead of 'x' is not a guard.
    /// </summary>
    private static string Key(string path)
        => path.Replace('\\', '/').TrimStart('.', '/');

    /// <summary>
    /// The path a call names, or null. Its own copy rather than the orchestrator's, because that one
    /// also accepts "to" (where a move puts a file) and this guard is only ever asked about writes.
    /// </summary>
    internal static string? FileNamedBy(ToolCall call)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);

            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("path", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static object? Meta(ToolResult result, string name)
        => result.Metadata.TryGetValue(name, out var value) ? value : null;

    private static int? Int(ToolResult result, string name)
        => Meta(result, name) switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null
        };
}
