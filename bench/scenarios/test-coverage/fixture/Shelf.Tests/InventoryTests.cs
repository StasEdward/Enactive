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
}
