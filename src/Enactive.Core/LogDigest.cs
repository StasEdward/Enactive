namespace Enactive.Core.Diagnostics;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>One record line of a log, as much of it as a digest needs.</summary>
/// <param name="Run">The short run id as the log prints it, with <c>#step</c> when there is one.</param>
public readonly record struct DigestRecord(
    TimeSpan At, LogLevel Level, string Source, string Run, string? Category, string Message);

/// <summary>What a digest threw away, so the reader is never guessing.</summary>
public sealed record DigestStats(
    long LinesRead, long DetailLinesDropped, long RecordsKept, long TimelineDropped);

/// <summary>
/// Turns a log that is too big to read into one that answers the question.
///
/// <para><b>Why not the excerpt.</b> <c>LogAnalyst.Excerpt</c> keeps the start and the end and
/// drops the middle, which is right for a log of a few thousand lines and useless for one of a few
/// hundred million: the start is one run's routing, the end is another's, and everything that
/// happened is in the part that went. Worse, it takes the whole log as a <c>string</c> — a .NET
/// string cannot hold two gigabytes at all, so a large file does not get a poor answer, it gets an
/// exception before the model is ever asked.</para>
///
/// <para><b>What actually makes a log enormous.</b> Not the records — the DETAIL under them. Every
/// prompt and every assembled response is written out beneath its line as <c>"    | "</c>
/// continuations, and at Trace so is the raw HTTP body of every call. Dropping those alone takes a
/// log from gigabytes to megabytes, and loses nothing a diagnosis needs: what was asked, what ran,
/// what it answered and what went wrong are all on the record lines.</para>
///
/// <para><b>Streamed, and bounded.</b> One pass, one line held at a time, and the timeline is
/// capped — a log with a million errors in it must not become a digest with a million errors in
/// it. What the cap dropped is counted and said.</para>
///
/// <para>This follows a script that was used by hand against real exports for weeks; the shape of
/// what it keeps is that script's, which is the part that was worth keeping.</para>
/// </summary>
public static class LogDigest
{
    /// <summary>
    /// Categories that are noise in a digest: they restate what the line above or below already
    /// says. Taken from the hand-run script's own skip list.
    /// </summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "ToolCompleted", "ArtifactProduced", "ToolResult"
    };

    /// <summary>Sources whose every line is part of the story of a run, at any level.</summary>
    private static readonly HashSet<string> Always = new(StringComparer.OrdinalIgnoreCase)
    {
        "Orchestrator", "Reviewer", "Permission"
    };

    /// <summary>
    /// A record line of either spelling.
    ///
    /// <para>There are two, and a digest that knew one of them would silently produce nothing from
    /// half the logs it was given. The FILE sink writes the level as the enum name padded to five
    /// (<c>Info </c>, <c>Warn </c>) and the run as eight hex digits; the log window's Export writes
    /// a three-letter level (<c>INF</c>, <c>WRN</c>) and six. Both are matched here, and a line
    /// that matches neither is counted rather than dropped in silence.</para>
    /// </summary>
    private static readonly Regex Head = new(
        @"^(?<h>\d\d):(?<m>\d\d):(?<s>\d\d)\.(?<ms>\d{1,3})\s+"
        + @"(?<lvl>TRC|DBG|INF|WRN|ERR|Trace|Debug|Info|Warn|Error)\s+"
        + @"(?<src>\S+)\s+(?:run=)?(?<run>[0-9a-f-]{6,}(?:#\d+)?)\s*"
        + @"(?:\[(?<cat>[^\]]*)\]\s*)?(?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>How much of a message survives into the digest.</summary>
    private const int MessageChars = 240;

    /// <summary>
    /// Whether this line is a detail continuation — a prompt dump, a response body, raw wire.
    ///
    /// <para>Tested by the leading space and nothing else, exactly as the hand-run script did. The
    /// file sink writes <c>"    | "</c>; anything else that arrives indented is somebody's wrapped
    /// output and belongs with it.</para>
    /// </summary>
    public static bool IsDetail(string line)
        => line.Length > 0 && (line[0] == ' ' || line[0] == '\t');

    /// <summary>The record this line carries, or null when it carries none.</summary>
    public static DigestRecord? Parse(string line)
    {
        if (string.IsNullOrEmpty(line) || IsDetail(line))
            return null;

        var m = Head.Match(line);
        if (!m.Success)
            return null;

        var at = new TimeSpan(
            0,
            int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["ms"].Value.PadRight(3, '0'), CultureInfo.InvariantCulture));

        return new DigestRecord(
            at,
            LevelOf(m.Groups["lvl"].Value),
            m.Groups["src"].Value,
            m.Groups["run"].Value,
            m.Groups["cat"].Success ? m.Groups["cat"].Value : null,
            m.Groups["msg"].Value.TrimEnd());
    }

    private static LogLevel LevelOf(string text) => text switch
    {
        "TRC" or "Trace" => LogLevel.Trace,
        "DBG" or "Debug" => LogLevel.Debug,
        "WRN" or "Warn" => LogLevel.Warn,
        "ERR" or "Error" => LogLevel.Error,
        _ => LogLevel.Info
    };

    /// <summary>
    /// Digests a log read line by line. The reader is consumed once and never held, so the size of
    /// the log is the size of the file and not the size of anything in memory.
    /// </summary>
    /// <param name="maxTimelineEntries">
    /// The ceiling on the timeline. A run that failed ten thousand times does not need ten thousand
    /// lines to say so, and the digest exists because something did not fit.
    /// </param>
    public static (string Text, DigestStats Stats) Of(
        TextReader reader, int maxTimelineEntries = 2_000)
    {
        var steps = new Dictionary<string, StepTotals>(StringComparer.Ordinal);
        var order = new List<string>();
        var timeline = new List<string>();

        long read = 0, detail = 0, kept = 0, dropped = 0;

        while (reader.ReadLine() is { } line)
        {
            read++;

            if (IsDetail(line)) { detail++; continue; }
            if (Parse(line) is not { } r) continue;

            kept++;

            if (!steps.TryGetValue(r.Run, out var totals))
            {
                steps[r.Run] = totals = new StepTotals(r.At);
                order.Add(r.Run);
            }
            totals.Saw(r);

            if (!Interesting(r)) continue;

            if (timeline.Count >= maxTimelineEntries) { dropped++; continue; }
            timeline.Add(Line(r));
        }

        return (Render(order, steps, timeline, dropped),
                new DigestStats(read, detail, kept, dropped));
    }

    /// <summary>
    /// Whether this record earns a line in the timeline. Everything else is counted into its step's
    /// totals and not otherwise shown — the totals are what say it happened.
    /// </summary>
    private static bool Interesting(DigestRecord r)
        => !(r.Category is { } c && Noise.Contains(c))
           && (r.Level >= LogLevel.Warn
               || Always.Contains(r.Source)
               || string.Equals(r.Category, "ToolInvoked", StringComparison.OrdinalIgnoreCase));

    private static string Line(DigestRecord r)
    {
        var what = r.Category is { Length: > 0 } c ? $"[{c}] " : "";
        var msg = r.Message.Length > MessageChars
            ? r.Message[..MessageChars] + "…"
            : r.Message;

        return $"{r.At:hh\\:mm\\:ss\\.fff}  {r.Run,-11} {Mark(r.Level)}{what}{msg}";
    }

    private static string Mark(LogLevel level) => level switch
    {
        LogLevel.Error => "ERR ",
        LogLevel.Warn => "WRN ",
        _ => ""
    };

    private static string Render(
        List<string> order, Dictionary<string, StepTotals> steps, List<string> timeline, long dropped)
    {
        var sb = new StringBuilder();

        sb.AppendLine("== per run/step: records, model calls, tokens, tools, span ==");
        foreach (var key in order)
        {
            var s = steps[key];
            if (s.Records == 0) continue;

            sb.Append($"{key,-11} records={s.Records,6}");
            if (s.Calls > 0) sb.Append($"  calls={s.Calls,3}  in={s.TokensIn,9:N0}  out={s.TokensOut,7:N0}");
            if (s.Warnings > 0) sb.Append($"  warn={s.Warnings}");
            if (s.Errors > 0) sb.Append($"  ERRORS={s.Errors}");
            sb.Append($"  {s.First:hh\\:mm\\:ss}..{s.Last:hh\\:mm\\:ss}");
            if (s.Tools.Count > 0)
                sb.Append("  " + string.Join(
                    " ", s.Tools.OrderByDescending(t => t.Value).Select(t => $"{t.Key}={t.Value}")));
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("== timeline: warnings, errors, tool calls, orchestration, review, permission ==");
        foreach (var line in timeline)
            sb.AppendLine(line);

        if (dropped > 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                $"… {dropped:N0} further timeline entries are NOT SHOWN — the digest was capped. "
                + "The per-step totals above still count them.");
        }

        return sb.ToString();
    }

    /// <summary>What one run/step adds up to. Counters only: nothing here grows with the log.</summary>
    private sealed class StepTotals
    {
        private static readonly Regex Usage = new(
            @"tokens:\s*(?<in>\d+)\s*in,\s*(?<out>\d+)\s*out",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        public StepTotals(TimeSpan first) { First = first; Last = first; }

        public long Records { get; private set; }
        public int Calls { get; private set; }
        public long TokensIn { get; private set; }
        public long TokensOut { get; private set; }
        public int Warnings { get; private set; }
        public int Errors { get; private set; }
        public TimeSpan First { get; }
        public TimeSpan Last { get; private set; }
        public Dictionary<string, int> Tools { get; } = new(StringComparer.Ordinal);

        public void Saw(DigestRecord r)
        {
            Records++;
            Last = r.At;

            if (r.Level == LogLevel.Warn) Warnings++;
            if (r.Level == LogLevel.Error) Errors++;

            if (string.Equals(r.Category, "ToolInvoked", StringComparison.OrdinalIgnoreCase))
            {
                var name = ToolName(r.Message);
                if (name.Length > 0)
                    Tools[name] = Tools.TryGetValue(name, out var n) ? n + 1 : 1;
            }

            // Matched on the message because that is where the number is by the time anything reads
            // a log. The hand-run script carries a scar about a SECOND phrasing it did not match,
            // and reported an ice-cold cache in every run for weeks as a result - so anything this
            // cannot read is left out of the totals rather than counted as zero, and the record
            // still shows up in Records.
            if (Usage.Match(r.Message) is { Success: true } u)
            {
                Calls++;
                TokensIn += long.Parse(u.Groups["in"].Value, CultureInfo.InvariantCulture);
                TokensOut += long.Parse(u.Groups["out"].Value, CultureInfo.InvariantCulture);
            }
        }

        private static string ToolName(string message)
        {
            var text = message.StartsWith("ToolInvoked:", StringComparison.OrdinalIgnoreCase)
                ? message["ToolInvoked:".Length..].TrimStart()
                : message;

            var end = text.IndexOfAny(new[] { ' ', '(', '{' });
            return end < 0 ? text : text[..end];
        }
    }
}
