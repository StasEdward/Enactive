namespace Enactive.Application.Projections;

/// <summary>How a token count is written where there is little room: the window's tiles, a history row, the work split.</summary>
internal static class TokenCounts
{
    /// <summary>A tile is about four characters wide, and 128000 does not fit in four.</summary>
    public static string Compact(int tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1_000.0:0.#}k",
        _ => tokens.ToString()
    };
}
