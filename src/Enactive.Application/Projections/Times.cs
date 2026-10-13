namespace Enactive.Application.Projections;

using System.Globalization;

/// <summary>
/// A moment as the window has always written it - "2026-10-13 09:05", "09:05", "09:05:12", "Oct 13" - the same under
/// every culture.
///
/// <para>The service writes in its own process's culture, not the reader's: under one whose time separator is not a
/// colon (fi-FI writes "09.05") or whose month names are not English, the same run read "09.05" or "loka 13" on one
/// screen and "09:05" or "Oct 13" on another. The colon is quoted: in a format string an unquoted ':' is the culture's
/// time separator, and the invariant culture's is a colon only by its own choice.</para>
/// </summary>
internal static class Times
{
    /// <summary>"2026-10-13 09:05", local time.</summary>
    public static string Minute(DateTimeOffset at) => Minute(at.ToLocalTime().DateTime);

    /// <summary>"2026-10-13 09:05", of a time already local (a file's last write).</summary>
    public static string Minute(DateTime local) => local.ToString("yyyy-MM-dd HH':'mm", CultureInfo.InvariantCulture);

    /// <summary>"09:05", local time.</summary>
    public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH':'mm", CultureInfo.InvariantCulture);

    /// <summary>"09:05:12", local time.</summary>
    public static string Seconds(DateTimeOffset at) => at.ToLocalTime().ToString("HH':'mm':'ss", CultureInfo.InvariantCulture);

    /// <summary>"Oct 13", local time - the month in English, as every other word on the screen.</summary>
    public static string Day(DateTimeOffset at) => at.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
}
