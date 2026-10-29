namespace Enactive.Bench;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Rules;
using Enactive.Workspace;

/// <summary>
/// The rules a measured run is given (Plan Phase 5, "Measurement"): read from a file of rule texts and kept, as a person
/// keeps them, in the run workspace's OWN store - never the machine's Global layer, which the desktop's runs share and a
/// benchmark must not change. Each run's workspace is a fresh copy of the fixture, so it has no rules but these.
/// </summary>
internal static class Rules
{
    /// <summary>The texts in <paramref name="file"/> - a JSON array of strings - as confirmed workspace rules of <paramref name="work"/>.</summary>
    public static async Task<int> SeedAsync(string work, string file, CancellationToken ct)
    {
        var texts = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(file, ct))
                    ?? throw new InvalidOperationException($"{file} is not a JSON array of rule texts.");
        var store = RuleStoreFactory.Create(WorkspaceInfo.Adopt(work));
        var at = DateTimeOffset.UtcNow;
        foreach (var (text, i) in texts.Where(t => !string.IsNullOrWhiteSpace(t)).Select((t, i) => (t, i)))
            await store.SaveAsync(new RuleEntry(Guid.NewGuid(), RuleScope.Workspace, RuleHeading.Interpretations, text.Trim(), null,
                at.AddMilliseconds(i), RuleStatus.Confirmed) { Origin = "bench --rules " + Path.GetFileName(file) }, ct);
        return texts.Length;
    }
}
