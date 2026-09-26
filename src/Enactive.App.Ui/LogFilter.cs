namespace Enactive.App.Ui;

using Enactive.Core.Diagnostics;

internal static class LogFilter
{
    public static bool Matches(LogEntry entry, LogLevel minimum, HashSet<LogSource> sources, string search)
    {
        if (entry.Level < minimum || !sources.Contains(entry.Source)) return false;
        if (string.IsNullOrWhiteSpace(search)) return true;
        // Never concatenate the wire payload into a second large string just to search it.
        return Contains(entry.Message) || Contains(entry.Category) || Contains(entry.Detail)
            || Contains(RunToken(entry));
        bool Contains(string? text) => text?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
    }

    public static string RunToken(LogEntry entry, int idChars = 6)
        => (entry.RunId is { } r ? r.ToString("N")[..idChars] : new string('-', idChars))
           + (entry.Step is { } step ? "#" + step : "");
}
