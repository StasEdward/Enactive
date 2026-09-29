namespace Enactive.Agents;

using System.Globalization;

/// <summary>
/// The date and time where the run is, one line, for every model that works on it. A model has no clock: asked for a
/// report of the disks, the worker dated it from its training (2026-09-29), and a step review could not tell it wrong.
/// Taken when the prompt is built - a step, a plan, a review - and to the minute, so a conversation's prefix does not
/// change under it from one call to the next.
/// </summary>
internal static class LocalTime
{
    public static string Line() => Line(DateTimeOffset.Now);

    public static string Line(DateTimeOffset now)
    {
        var offset = now.Offset;
        var zone = $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}";
        return "Local date and time now: " + now.ToString("yyyy-MM-dd HH:mm, dddd", CultureInfo.InvariantCulture) + $" ({zone})";
    }
}
