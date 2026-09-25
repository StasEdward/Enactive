namespace Enactive.Core.Diagnostics;

using System.Text;

/// <summary>Payload budgets count UTF-16 bytes retained by .NET strings, excluding object overhead.</summary>
public static class LogPayload
{
    public const int MaxEntryBytes = 256 * 1024;
    public const string Truncated = "\n… [log payload truncated]\n";

    public static long Bytes(LogEntry entry)
        => 2L * (entry.Message.Length + (long)(entry.Detail?.Length ?? 0) + (entry.Category?.Length ?? 0));

    public static string? Limit(string? text, int maxBytes)
    {
        if (text is null || text.Length <= maxBytes / 2) return text;
        var chars = Math.Max(0, maxBytes / 2);
        if (chars < Truncated.Length) return Truncated[..chars];
        var keep = chars - Truncated.Length;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1])) keep--;
        return text[..keep] + Truncated;
    }

    public static LogEntry Limit(LogEntry entry, int maxBytes)
    {
        if (Bytes(entry) <= maxBytes) return entry;
        var category = Limit(entry.Category, maxBytes / 8);
        var message = Limit(entry.Message, maxBytes / 4)!;
        var remaining = maxBytes - 2 * (message.Length + (category?.Length ?? 0));
        return entry with { Message = message, Category = category, Detail = Limit(entry.Detail, remaining) };
    }
}

/// <summary>A bounded prefix of a diagnostic stream. Absent entirely when its level is disabled.</summary>
public sealed class BoundedLogBuffer
{
    private readonly StringBuilder _text = new();
    private readonly int _maxChars;
    private bool _truncated;

    private BoundedLogBuffer(int maxBytes) => _maxChars = maxBytes / 2;

    public static BoundedLogBuffer? Create(ILogSink? sink, LogLevel level,
        int maxBytes = LogPayload.MaxEntryBytes)
        => sink.IsLoggingEnabled(level) ? new BoundedLogBuffer(Math.Max(128, maxBytes)) : null;

    public void AppendLine(string line)
    {
        Append(line);
        Append(Environment.NewLine);
    }

    private void Append(string text)
    {
        var take = Math.Min(text.Length, _maxChars - _text.Length);
        _text.Append(text.AsSpan(0, take));
        _truncated |= take < text.Length;
    }

    public override string ToString()
    {
        if (!_truncated) return _text.ToString();
        var keep = _maxChars - LogPayload.Truncated.Length;
        if (keep > 0 && char.IsHighSurrogate(_text[keep - 1])) keep--;
        return _text.ToString(0, keep) + LogPayload.Truncated;
    }
}
