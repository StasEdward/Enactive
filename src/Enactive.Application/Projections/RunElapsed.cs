namespace Enactive.Application.Projections;

using System.Globalization;

/// <summary>
/// How long a run has taken, as the ELAPSED tile says it. Moved from the window (MainWindow.FormatElapsed) so the
/// service's tile reads the same: "2m 3s" in one and "00:02:03" in the other would be one run said two ways.
/// </summary>
internal static class RunElapsed
{
    public static string Format(TimeSpan span)
    {
        if (span.TotalHours >= 1)
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s");
        if (span.TotalMinutes >= 1)
            return string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}m {span.Seconds}s");
        return string.Create(CultureInfo.InvariantCulture, $"{span.Seconds}s");
    }
}
