namespace Enactive.Workspace;

using Enactive.Core.Rules;

/// <summary>
/// A person's side of the rules (Plan Phase 5.2): both layers read as one list, and the decisions on an entry - keep it
/// (as it is, or in their own words, here or for every workspace), dismiss it, delete it, or write one of their own. One
/// class for every host, so the console, the window and the service decide the same way.
/// </summary>
public sealed class RuleBook(IRuleStore workspace, IRuleStore global)
{
    /// <summary>Every entry of both layers, Global first, each oldest first - the order a run is given them in.</summary>
    public async Task<IReadOnlyList<RuleEntry>> ListAsync(CancellationToken ct)
        => [.. (await global.ListAsync(ct)).OrderBy(r => r.At), .. (await workspace.ListAsync(ct)).OrderBy(r => r.At)];

    /// <summary>
    /// The one entry an id or the start of one names - what a person copies from a list. Null when none does, and an
    /// error when several do: acting on whichever came first is acting on a rule nobody chose.
    /// </summary>
    public async Task<RuleEntry?> FindAsync(string id, CancellationToken ct)
    {
        var wanted = id.Trim();
        if (wanted.Length == 0)
            return null;
        var matches = (await ListAsync(ct))
            .Where(r => r.Id.ToString("N").StartsWith(wanted.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException($"'{id}' names {matches.Length} rules; give more of the id.")
        };
    }

    /// <summary>
    /// Confirms an entry: from the next new run on it is given to every run of its layer. <paramref name="text"/> is the
    /// person's own wording, when they gave one. <paramref name="scope"/> moves it: a workspace's lesson made Global
    /// reaches every workspace, and is no longer the workspace's - one rule, in one place.
    /// </summary>
    public async Task<RuleEntry> KeepAsync(RuleEntry entry, string? text, RuleScope? scope, CancellationToken ct)
    {
        var kept = entry with
        {
            Text = string.IsNullOrWhiteSpace(text) ? entry.Text : text.Trim(),
            Scope = scope ?? entry.Scope,
            Status = RuleStatus.Confirmed,
            At = DateTimeOffset.UtcNow
        };
        await StoreOf(kept.Scope).SaveAsync(kept, ct);
        if (kept.Scope != entry.Scope)
            await StoreOf(entry.Scope).DeleteAsync(entry.Id, ct);
        return kept;
    }

    /// <summary>
    /// Sets an entry aside. Kept, not deleted, so the same observation is not proposed again by a later run
    /// (LessonCandidates reads every entry's source, whatever its status).
    /// </summary>
    public Task DismissAsync(RuleEntry entry, CancellationToken ct)
        => StoreOf(entry.Scope).SaveAsync(entry with { Status = RuleStatus.Dismissed }, ct);

    /// <summary>Removes an entry - and with it the memory that its observation was decided on, so a later run may propose it again.</summary>
    public Task DeleteAsync(RuleEntry entry, CancellationToken ct) => StoreOf(entry.Scope).DeleteAsync(entry.Id, ct);

    /// <summary>A rule a person wrote, confirmed as written.</summary>
    public async Task<RuleEntry> AddAsync(string text, RuleScope scope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("A rule needs text.", nameof(text));
        var rule = new RuleEntry(Guid.NewGuid(), scope, RuleHeading.Interpretations, text.Trim(), null, DateTimeOffset.UtcNow,
            RuleStatus.Confirmed) { Origin = "written by a person" };
        await StoreOf(scope).SaveAsync(rule, ct);
        return rule;
    }

    private IRuleStore StoreOf(RuleScope scope) => scope == RuleScope.Global ? global : workspace;
}
