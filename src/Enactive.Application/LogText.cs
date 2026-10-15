namespace Enactive.Application;

using System.Globalization;
using System.Text;
using Enactive.Core.Diagnostics;

/// <summary>
/// The log as text: one line per entry, and the export - which is also what a model is given to analyse. Shared by the
/// window's log and the service's log page, because what is analysed has to be what a person can read and export: an
/// answer compared against a file that was rendered differently is compared against something else.
/// </summary>
internal static class LogText
{
    /// <summary>One entry on one line: time, level, source, run and step, category, the message cut to 200 characters.</summary>
    public static string Line(LogEntry entry)
    {
        // Padded so the category and message columns stay aligned whether or not a line has a step.
        var run = LogFilter.RunToken(entry).PadRight(9);
        var category = string.IsNullOrEmpty(entry.Category) ? "" : $" [{entry.Category}]";
        // Cut, then flattened, then cut again - as the window always drew it, so a line reads the same everywhere.
        var message = entry.Message.Length > 200 ? entry.Message[..200] + "…" : entry.Message;
        message = message.Replace("\r", " ").Replace("\n", " ");
        if (message.Length > 200)
            message = message[..200] + "…";
        // The time in the same digits on every machine: a format's ":" is the culture's time separator, which is not a colon
        // everywhere, and a line exported on one machine and read against another must say the same.
        var at = entry.At.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return $"{at}  {Short(entry.Level)}  {entry.Source,-12} {run}{category}  {message}";
    }

    /// <summary>
    /// The entries as one text, in time order, each detail indented under its line. Time order, not arrival: domain
    /// events reach the log after wire lines they preceded, which is fine while tailing and wrong read as a timeline.
    /// </summary>
    public static string Render(IEnumerable<LogEntry> entries)
    {
        var text = new StringBuilder();
        foreach (var entry in entries.OrderBy(e => e.At).ThenBy(e => e.Seq))
        {
            text.AppendLine(Line(entry));
            if (!string.IsNullOrEmpty(entry.Detail))
                foreach (var line in entry.Detail.Replace("\r\n", "\n").Split('\n'))
                    text.Append("    | ").AppendLine(line);
        }
        return text.ToString();
    }

    private static string Short(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "?"
    };
}
