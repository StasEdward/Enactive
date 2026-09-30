namespace Tariffs;

/// <summary>Delivery prices per zone and weight band, read from a CSV of zone,maxKg,price.</summary>
public sealed class RateTable
{
    private readonly List<(string Zone, double MaxKg, decimal Price)> _rows = [];

    public static RateTable Parse(string csv)
    {
        var table = new RateTable();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
        {
            var parts = line.Split(',');
            table._rows.Add((parts[0], double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)));
        }
        return table;
    }

    /// <summary>The price for a parcel of <paramref name="kg"/> to a zone: the first band it fits in.</summary>
    public decimal PriceFor(string zone, double kg)
        => _rows.Where(r => r.Zone == zone && kg <= r.MaxKg).OrderBy(r => r.MaxKg).Select(r => (decimal?)r.Price).FirstOrDefault()
           ?? throw new ArgumentOutOfRangeException(nameof(kg), $"No band in zone {zone} takes {kg} kg.");
}
