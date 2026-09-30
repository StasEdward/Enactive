namespace Shelf.Tests;

using Xunit;

public sealed class InventoryTests
{
    [Fact]
    public void Adding_an_item_counts_it()
    {
        var shelf = new Inventory();
        shelf.Add("apple", 3);
        Assert.Equal(3, shelf.CountOf("apple"));
    }

    [Fact]
    public void Adding_the_same_item_again_adds_to_it()
    {
        var shelf = new Inventory();
        shelf.Add("apple");
        shelf.Add("Apple", 2);
        Assert.Equal(3, shelf.CountOf("apple"));
    }

    [Fact]
    public void Removing_takes_away_and_the_last_one_removes_the_kind()
    {
        var shelf = new Inventory();
        shelf.Add("pear", 2);
        Assert.True(shelf.Remove("pear"));
        Assert.Equal(1, shelf.CountOf("pear"));
        Assert.True(shelf.Remove("pear"));
        Assert.Empty(shelf.Kinds);
    }

    [Fact]
    public void Removing_more_than_there_is_changes_nothing()
    {
        var shelf = new Inventory();
        shelf.Add("pear");
        Assert.False(shelf.Remove("pear", 2));
        Assert.Equal(1, shelf.CountOf("pear"));
    }

    [Fact]
    public void Total_counts_every_kind()
    {
        var shelf = new Inventory();
        shelf.Add("apple", 2);
        shelf.Add("pear", 3);
        Assert.Equal(5, shelf.Total);
    }

    [Fact]
    public void An_item_there_is_none_of_counts_zero_and_a_bad_count_is_refused()
    {
        var shelf = new Inventory();
        Assert.Equal(0, shelf.CountOf("plum"));
        Assert.Throws<ArgumentOutOfRangeException>(() => shelf.Add("plum", 0));
    }
}
