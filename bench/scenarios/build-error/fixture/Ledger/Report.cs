namespace Ledger;

/// <summary>A one-line summary of a list of amounts. Left half-written: it does not compile yet.</summary>
public static class Report
{
    public static string Line(IReadOnlyList<double> values)
    {
        var total = Stats.Sum(values);
        return $"{values.Count} amounts, {totl:F2} in all";
    }
}
