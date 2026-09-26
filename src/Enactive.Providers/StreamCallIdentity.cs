namespace Enactive.Providers;

using System.Text.Json;

/// <summary>Maps wire identities to unique indices for the lifetime of one response.</summary>
internal sealed class StreamCallIdentity
{
    private readonly Dictionary<int, HashSet<int>> _indices = new();
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _owners = new();
    private int _next;

    public int Resolve(int? wireIndex, string? id, string? name, string? arguments)
    {
        if (wireIndex < 0) throw new InvalidDataException("Negative tool call index.");
        var hasId = !string.IsNullOrEmpty(id);
        var candidates = wireIndex is { } w && _indices.TryGetValue(w, out var bucket) ? bucket : null;
        if (!hasId && candidates is { Count: > 1 })
            throw new InvalidDataException("Tool call index has multiple ids; a fragment without an id is ambiguous.");
        if (wireIndex is null && !hasId && !Complete(name, arguments))
            throw new InvalidDataException("Tool call fragment has neither an index nor an id; its owner is ambiguous.");

        int index;
        if (hasId && _ids.TryGetValue(id!, out var known)) index = known;
        else if (candidates is { Count: 1 } && (!hasId || !_owners.ContainsKey(candidates.First())))
            index = candidates.First(); // a late ID may name an as-yet anonymous indexed call
        else index = _next++;

        if (hasId)
        {
            _owners[index] = id!;
            _ids[id!] = index;
        }
        if (wireIndex is { } value)
        {
            if (!_indices.TryGetValue(value, out var owners)) _indices[value] = owners = [];
            owners.Add(index);
        }
        return index;
    }

    private static bool Complete(string? name, string? arguments)
    {
        if (string.IsNullOrEmpty(name) || arguments is null) return false;
        try { using var doc = JsonDocument.Parse(arguments); return doc.RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }
}
