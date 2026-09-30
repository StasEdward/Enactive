namespace Ledger;

/// <summary>Figures over a list of amounts.</summary>
public static class Stats
{
    /// <summary>The sum of the amounts; 0 for none.</summary>
    public static double Sum(IReadOnlyList<double> values) => values.Sum();

    /// <summary>The mean of the amounts. Refused for an empty list: there is no mean of nothing.</summary>
    public static double Average(IReadOnlyList<double> values)
    {
        if (values.Count == 0) throw new ArgumentException("No amounts to average.", nameof(values));
        return values.Sum() / values.Count;
    }

    /// <summary>The middle amount, or the mean of the two middle ones. Refused for an empty list.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) throw new ArgumentException("No amounts to take the median of.", nameof(values));
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
