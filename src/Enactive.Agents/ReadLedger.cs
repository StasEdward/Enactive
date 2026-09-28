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
        public int Total;        // the file's line count, as the read reported it
        public bool TotalKnown = true;

        // Lines read_file cannot show whole - one line longer than its cap, which it steps OVER
        // when it says where to read on. Such a line is never "seen", so a file holding one is
        // never read in full, and the advice for it must not be "read from that line" again.
        public readonly HashSet<int> TooLong = new();

        // Every window seen, merged: sorted, disjoint, and not touching. It used to be one number -
        // the highest line reached without a gap FROM LINE 1 - and a window that began beyond it was
        // thrown away. So 401-518 and then 1-400, which is the whole file, counted as 1-400, and a
        // whole-file write of a file read in full was refused. The order a file is read in is the
        // model's business; what was seen is the engine's, and it aggregates the ranges itself.
        private readonly List<(int From, int To)> _seen = new();

        // It WAS read, and the text has since been cut from the conversation (a trim, a handover).
        // Not the same as never having seen it whole, and the refusal must not say it was: on
        // 2026-09-28 11:36 a step read a 48-line file in full, the conversation was trimmed, and
        // the write was refused as "seen only as an excerpt" - three times, the model never
        // re-reading, and the step ended there.
        public bool Discarded;

        public void Clear()
        {
            Discarded |= _seen.Count > 0;
            _seen.Clear();
        }

        public void Add(int from, int to)
        {
            if (to < from) return;
            Discarded = false;
            _seen.Add((from, to));
            _seen.Sort((a, b) => a.From.CompareTo(b.From));
            var merged = new List<(int From, int To)>();
            foreach (var range in _seen)
                if (merged.Count > 0 && range.From <= merged[^1].To + 1)
                    merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, range.To));
                else
                    merged.Add(range);
            _seen.Clear();
            _seen.AddRange(merged);
        }

        /// <summary>The highest line reached without a gap from line 1 - where reading must go on from.</summary>
        public int Contiguous => _seen.Count > 0 && _seen[0].From <= 1 ? _seen[0].To : 0;

        /// <summary>The lines not seen, as ranges, up to <see cref="Total"/>.</summary>
        public IReadOnlyList<(int From, int To)> Unseen()
        {
            var gaps = new List<(int From, int To)>();
            var next = 1;
            foreach (var (from, to) in _seen)
            {
                if (from > next) gaps.Add((next, Math.Min(from - 1, Total)));
                next = Math.Max(next, to + 1);
                if (next > Total) break;
            }
            if (next <= Total) gaps.Add((next, Total));
            return gaps.Where(g => g.From <= g.To).ToArray();
        }
    }

    private readonly Dictionary<string, Coverage> _files =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Full file text was discarded; retain the guard, but require fresh coverage.</summary>
    public void ForgetDiscardedReads()
    {
        foreach (var coverage in _files.Values)
        {
            coverage.Clear();
            coverage.TooLong.Clear();
        }
    }

    private Coverage CoverageOf(string path)
    {
        var key = Key(path);
        if (!_files.TryGetValue(key, out var coverage))
            _files[key] = coverage = new Coverage();
        return coverage;
    }

    private static IEnumerable<string> PathsNamedBy(ToolCall call, params string[] names)
    {
        var found = new List<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return found;
            foreach (var name in names)
                if (doc.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String
                    && value.GetString() is { Length: > 0 } path)
                    found.Add(path);
        }
        catch (System.Text.Json.JsonException) { }
        return found;
    }

    /// <summary>
    /// Records what a successful read covered. Everything it needs is already in the result's
    /// metadata — the tools stay ignorant of each other, and the loop that sees every call and every
    /// result is the one place that can put the two together.
    /// </summary>
    public void Saw(ToolCall call, ToolResult result, ToolDefinition? definition)
    {
        // A file deleted or moved away is not the file that was read. What was read of it describes
        // content that is gone, and must not stand between the step and a NEW file at that path.
        // Measured 2026-09-24 23:05-23:08, run 9ecf0e: a step read part of a test file it had just
        // written, deleted it, generated it again from scratch - 8,010 tokens, two minutes on that
        // machine - and the write was refused as "read only in part", about a file that no longer
        // existed.
        if (result.Success && definition?.FileCoverage == FileCoverageBehavior.Delete)
        {
            foreach (var gone in PathsNamedBy(call, "path"))
                _files.Remove(Key(gone));
            return;
        }

        // A MOVE takes what was read with it: the content at the new path is the content that was
        // read at the old one, and a file seen in part must not become rewritable by being renamed.
        if (result.Success && definition?.FileCoverage == FileCoverageBehavior.Move)
        {
            if (PathsNamedBy(call, "from").FirstOrDefault() is { } source
                && PathsNamedBy(call, "to").FirstOrDefault() is { } target)
            {
                _files.Remove(Key(target));
                if (_files.Remove(Key(source), out var moved))
                    _files[Key(target)] = moved;
            }
            return;
        }

        if (definition?.FileCoverage != FileCoverageBehavior.Read || !result.Success)
            return;

        // Several files in one read - read_files, or read_file given 'paths'. Each says how much of it
        // was shown whole; an excerpt counts as none, so a whole-file write of it is refused below.
        if (Meta(result, "files") is IEnumerable<FileCoverage> many)
        {
            foreach (var file in many)
            {
                var entry = CoverageOf(file.Path);
                entry.Total = file.TotalLines;
                entry.TotalKnown = file.TotalLinesKnown;
                entry.Add(1, file.LinesShownWhole);
            }
            return;
        }

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
        coverage.TotalKnown = true;

        // A line read_file cannot show whole. read_file says so in the text; this remembers it, so the
        // advice below agrees with it. Named by the tool rather than inferred from a cursor jumping
        // past the cut: a long LAST line has no cursor after it.
        if (Int(result, "tooLongLine") is { } tooLong)
            coverage.TooLong.Add(tooLong);

        // Every window counts, in whatever order it came. A hole between windows is still a hole,
        // and a hole is exactly what makes a rewrite unsafe.
        coverage.Add(from, to);
    }

    /// <summary>
    /// Why this write must not go ahead, or null when it may. A whole-file write of a file this step
    /// has seen only part of cannot have been derived from the parts it did not see.
    /// </summary>
    /// <param name="fileExists">
    /// Whether there is a file at <paramref name="path"/> now - on disk, or as a staged proposal. A
    /// write where there is none CREATES a file, and there is nothing it could destroy: a file deleted
    /// by a command, which no tool call here saw, is covered by this as well as by
    /// <see cref="Saw"/>'s forgetting.
    /// </param>
    public string? Refuse(ToolCall call, string? path, ToolDefinition? definition, bool fileExists = true)
    {
        if (definition?.FileCoverage != FileCoverageBehavior.Replace
            || string.IsNullOrWhiteSpace(path)
            || !fileExists)
            return null;

        if (!_files.TryGetValue(Key(path), out var coverage))
            return null;   // never read here - not this guard's business

        if (!coverage.TotalKnown)
            return $"This step has seen '{path}' only as an excerpt; its total line count is unknown. "
                 + "Use edit_file to change an exact passage without replacing unread content, "
                 + "or read the whole file before a whole-file write.";

        var unseen = coverage.Unseen();
        if (coverage.Total <= 0 || unseen.Count == 0)
            return null;   // read in full, in one window or several, in any order

        var next = unseen[0].From;

        // The same cursor read_file gave - except where read_file itself stepped over the line
        // because it cannot show it. Sending the model back there returns the same cut forever.
        if (coverage.TooLong.Contains(next))
            return $"'{path}' has a line ({next}) longer than read_file can show, so this step has not "
                 + "seen the whole file and a whole-file write would replace what it has not read. "
                 + "Change it with edit_file, which replaces one exact passage and leaves the rest alone.";

        // Windows read out of order, with a hole left between them: name the holes, not "read from
        // line 1" - the lines on either side were seen.
        if (unseen.Count > 1 || (unseen[0].From > 1 && unseen[0].To < coverage.Total))
        {
            var missing = unseen.Sum(g => g.To - g.From + 1);
            var ranges = string.Join(", ", unseen.Select(g => g.From == g.To ? $"{g.From}" : $"{g.From}-{g.To}"));
            return $"This step has not seen line(s) {ranges} of {coverage.Total} in '{path}', so a whole-file "
                 + $"write would replace {missing} line(s) it has never seen with whatever it happens to produce.\n"
                 + $"Either use edit_file, which replaces one exact passage and leaves the rest alone, or read "
                 + $"what is missing first: read_file with \"offset\": {next}.";
        }

        if (coverage.Discarded)
            return $"This step read '{path}' earlier, but that text has since been cut from this conversation to "
                 + "make room, so a whole-file write now would be written from memory of it and replace what is "
                 + "actually there. Read it again first - read_file with \"offset\": 1 - or change one passage "
                 + "with edit_file.";

        if (coverage.Contiguous == 0)
            return $"This step has seen '{path}' only as an excerpt, not whole ({coverage.Total} lines), so a "
                 + "whole-file write would replace the part it has not seen with whatever it happens to produce. "
                 + "Either use edit_file, which replaces one exact passage and leaves the rest alone, or read it "
                 + "first: read_file with \"offset\": 1.";

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
