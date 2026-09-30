namespace Shelf;

/// <summary>Items on a shelf and how many of each.</summary>
public sealed class Inventory
{
    private readonly Dictionary<string, int> _items = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds <paramref name="count"/> of an item. A count below one is refused.</summary>
    public void Add(string item, int count = 1)
    {
        if (string.IsNullOrWhiteSpace(item)) throw new ArgumentException("An item needs a name.", nameof(item));
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "Add at least one.");
        _items[item] = _items.TryGetValue(item, out var have) ? have + count : count;
    }

    /// <summary>Takes <paramref name="count"/> of an item away. False, and nothing changed, if there are not that many.</summary>
    public bool Remove(string item, int count = 1)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "Remove at least one.");
        if (!_items.TryGetValue(item, out var have) || have < count) return false;
        if (have == count) _items.Remove(item);
        else _items[item] = have - count;
        return true;
    }

    /// <summary>How many of one item there are; 0 for one there is none of.</summary>
    public int CountOf(string item) => _items.TryGetValue(item, out var have) ? have : 0;

    /// <summary>How many items there are in all, of every kind.</summary>
    public int Total => _items.Values.Sum();

    /// <summary>The kinds of item there are, in name order.</summary>
    public IReadOnlyList<string> Kinds => _items.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
}
