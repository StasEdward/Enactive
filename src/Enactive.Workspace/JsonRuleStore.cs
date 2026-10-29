namespace Enactive.Workspace;

using System.Text.Json;
using Enactive.Core.Rules;

/// <summary>
/// Rules in one JSON file (Plan Phase 5.1): the machine's Global layer, next to its settings.json, and a workspace's own
/// under ENACTIVE_STORE=json. Locked against the other processes on this machine as the other JSON stores are
/// (<see cref="JsonFileStore"/>): the window and a scheduled run read and write the same file.
/// </summary>
public sealed class JsonRuleStore(string path) : IRuleStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>The Global layer of a host whose settings are in <paramref name="settingsFolder"/>.</summary>
    public static JsonRuleStore Global(string? settingsFolder = null) => new(Path.Combine(settingsFolder ?? DefaultFolder, "rules.json"));

    /// <summary>Where the desktop and the console keep their settings, and so their Global rules.</summary>
    public static string DefaultFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Enactive");

    public string FilePath { get; } = path;

    public async Task<IReadOnlyList<RuleEntry>> ListAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath))
            return [];
        await using var hold = await JsonFileStore.HoldAsync(FilePath, ct).ConfigureAwait(false);
        var (items, _) = await JsonFileStore.LoadAsync<RuleEntry>(FilePath, JsonOpts, ct).ConfigureAwait(false);
        return items;
    }

    public Task SaveAsync(RuleEntry entry, CancellationToken ct)
        => ChangeAsync(items =>
        {
            var at = items.FindIndex(r => r.Id == entry.Id);
            if (at < 0) items.Add(entry);
            else items[at] = entry;
        }, ct);

    public Task DeleteAsync(Guid id, CancellationToken ct) => ChangeAsync(items => items.RemoveAll(r => r.Id == id), ct);

    private async Task ChangeAsync(Action<List<RuleEntry>> change, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        await using var hold = await JsonFileStore.HoldAsync(FilePath, ct).ConfigureAwait(false);
        var (items, readable) = await JsonFileStore.LoadAsync<RuleEntry>(FilePath, JsonOpts, ct).ConfigureAwait(false);
        // A file that cannot be read is not an empty list: saving over it would lose every rule a person confirmed. Moved
        // aside first, and refused if it could not be - as the memory store does (JsonFileStore.QuarantineUnreadable).
        if (!readable && !JsonFileStore.QuarantineUnreadable(FilePath))
            throw new IOException($"The rules file {FilePath} cannot be read and could not be moved aside; nothing was saved.");
        change(items);
        await JsonFileStore.SaveAsync(FilePath, items, JsonOpts, ct).ConfigureAwait(false);
    }
}
