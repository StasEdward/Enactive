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

    /// <summary>
    /// Whole-file writes of each path in a row, with no cheaper change to it in between. See
    /// <see cref="RefuseRewrite"/>.
    /// </summary>
    private readonly Dictionary<string, int> _wholeWrites =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many whole-file writes of one path a step gets before the next is refused. The first
    /// creates the file and the second revises it; the third, with nothing cheaper in between, is
    /// a model retyping a document it should be adding to.
    /// </summary>
    internal const int WholeWritesBeforeRefusal = 2;

    /// <summary>
    /// Below this, a whole-file write is not this guard's business - the same judgement, and the
    /// same number, as the shrink guard's floor in <c>write_file</c>: a short file is cheap to
    /// produce and a model reproduces it reliably, so writing it whole again and again costs
    /// nothing worth refusing. The writes that cost minutes were 15,000 to 37,000 characters.
    /// </summary>
    internal const int RewriteFloorChars = 2_000;

    /// <summary>The tool that changes one passage without retyping the rest.</summary>
    internal const string EditTool = "edit_file";

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
        Wrote(call, result);

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
    /// Counts whole-file writes per path, and forgets the count the moment the file is changed the
    /// cheap way - an <c>edit_file</c>, or a <c>write_file</c> with <c>append</c>. Only SUCCESSFUL
    /// calls count: a write that was refused retyped nothing onto the disk.
    /// </summary>
    private void Wrote(ToolCall call, ToolResult result)
    {
        if (!result.Success || FileNamedBy(call) is not { Length: > 0 } path)
            return;

        var key = Key(path);

        if (string.Equals(call.Name, EditTool, StringComparison.Ordinal)
            || (string.Equals(call.Name, WriteTool, StringComparison.Ordinal) && Appends(call)))
        {
            _wholeWrites.Remove(key);
            return;
        }

        // A short write neither counts nor forgives: it is not the expensive case, and a long
        // rewrite on either side of it is still the same habit.
        if (string.Equals(call.Name, WriteTool, StringComparison.Ordinal) && Long(call))
            _wholeWrites[key] = _wholeWrites.GetValueOrDefault(key) + 1;
    }

    /// <summary>
    /// Why this whole-file write must not go ahead, or null when it may.
    ///
    /// <para><b>Measured 2026-09-24 13:12-13:18, run 98bc6302,</b> on a local model at about 62
    /// tokens a second. After each wiki page the step wrote the whole report again:</para>
    /// <code>
    /// 13:12  write_file Docs/DRIFT_ollama.md   4,502 output tokens    72 s
    /// 13:14  write_file Docs/DRIFT_ollama.md   7,689 output tokens   123 s
    /// 13:16  write_file Docs/DRIFT_ollama.md   9,453 output tokens   152 s
    /// </code>
    /// <para>Each rewrite longer than the last, so the output grows with the SQUARE of the report,
    /// and to the person the run looked hung - minutes with nothing in the log. Nor was it the
    /// same text retyped: only 106 of the first version's 295 lines survived into the second. The
    /// model was composing the report afresh each time, which means findings from pages already
    /// checked can quietly change or vanish between versions.</para>
    ///
    /// <para>FIX_PLAN 9cb saw this first and left it open to be seen twice. The count is of the
    /// PATH, not the content, for exactly the reason above: the versions differ, so no comparison
    /// of content would have caught it.</para>
    ///
    /// <para>Refused rather than warned: the tool description and the shrink guard both already
    /// named <c>edit_file</c>, and that advice was ignored. What makes refusing fair now is that
    /// the cheap route exists - <c>append</c>, which needs no anchor copied exactly - and the
    /// refusal names it. 9cb also measured what a refusal WITHOUT one produces: the whole report
    /// moved into a PowerShell here-string, which is the same rewrite by another door.</para>
    /// </summary>
    public string? RefuseRewrite(ToolCall call, string? path)
    {
        if (!string.Equals(call.Name, WriteTool, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(path) || Appends(call) || !Long(call))
            return null;

        var done = _wholeWrites.GetValueOrDefault(Key(path));
        if (done < WholeWritesBeforeRefusal)
            return null;

        return $"Not written: this step has already written '{path}' whole {done} times, and each "
             + "time the whole document had to be produced again - which is most of what this step "
             + "has been spending, and a way for findings already written to change or drop out "
             + "between versions. "
             + "To ADD a section, send write_file with \"append\": true and ONLY the new text; it "
             + "goes after what is there. To change a passage that is already in the file, use "
             + "edit_file. Putting the whole document into a shell command is the same rewrite and "
             + "costs the same.";
    }

    /// <summary>
    /// A warning owed one call before the refusal, so the refusal is never the first the model
    /// hears of it. Null unless this call was the whole-file write that reached the limit.
    /// </summary>
    public string? WarnRewrite(ToolCall call, ToolResult result)
    {
        if (!result.Success || !string.Equals(call.Name, WriteTool, StringComparison.Ordinal)
            || Appends(call) || !Long(call) || FileNamedBy(call) is not { Length: > 0 } path)
            return null;

        if (_wholeWrites.GetValueOrDefault(Key(path)) != WholeWritesBeforeRefusal)
            return null;

        return $"That is {WholeWritesBeforeRefusal} whole-file writes of '{path}' in this step. "
             + "Another whole-file write of it will be refused. To add to it, send write_file with "
             + "\"append\": true and only the new text; to change a passage, use edit_file.";
    }

    /// <summary>Whether this call's content is long enough to be the expensive case.</summary>
    private static bool Long(ToolCall call)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);

            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("content", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                && (value.GetString()?.Length ?? 0) >= RewriteFloorChars;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether this write_file call adds to the end rather than replacing.</summary>
    internal static bool Appends(ToolCall call)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);

            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("append", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
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
