namespace Enactive.Application.Projections;

using System.Globalization;

/// <summary>How a token count is written where there is little room: the window's tiles, a history row, the work split.</summary>
internal static class TokenCounts
{
    /// <summary>
    /// A tile is about four characters wide, and 128000 does not fit in four. Written the same in every culture: the
    /// service draws its tiles in the server's culture, not the reader's, and "1,5k" on one machine and "1.5k" on another
    /// was one count said two ways.
    /// </summary>
    public static string Compact(int tokens) => tokens switch
    {
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000_000.0:0.#}M"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000.0:0.#}k"),
        _ => tokens.ToString(CultureInfo.InvariantCulture)
    };
}
