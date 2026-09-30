namespace Tariffs.Tests;

using Xunit;

/// <summary>Checked against the published rate table, data/rates.csv (made by tools/make_rates.py).</summary>
public sealed class RateTableTests
{
    private static RateTable Published()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tariffs.sln"))) dir = dir.Parent;
        var csv = Path.Combine(dir!.FullName, "data", "rates.csv");
        return RateTable.Parse(File.ReadAllText(csv));
    }

    [Fact]
    public void A_light_parcel_to_the_near_zone_costs_the_first_band() => Assert.Equal(4.20m, Published().PriceFor("A", 1));

    [Fact]
    public void A_heavier_parcel_takes_the_band_it_fits_in() => Assert.Equal(14.55m, Published().PriceFor("B", 7.5));

    [Fact]
    public void The_far_zone_costs_more_for_the_same_weight() => Assert.True(Published().PriceFor("C", 2) > Published().PriceFor("A", 2));
}
