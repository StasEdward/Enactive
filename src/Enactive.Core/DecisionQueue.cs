namespace Enactive.Core.Permissions;

/// <summary>One visible decision at a time, shared across runs using the same UI surface.</summary>
public sealed class DecisionQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct,
        Func<DecisionRequest, CancellationToken, Task<DecisionOutcome>> show)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await show(request, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return outcome;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>
/// A remembered decision is about a tool in the action's workspace, never the selected UI folder - and is the
/// remembering person's. One engine, and so one of these, serves every run of a process; in the service (ADR 0002)
/// that is several people signed in at once, and "allow for this session" given by one of them was a yes for all.
/// A principal names the person, as in <see cref="ApprovalStore"/> (rule 5); null is the account the process runs as.
/// </summary>
public sealed class SessionApprovals
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Root, string Tool, string Principal), byte> _items = new();

    public bool Approves(DecisionRequest request, string? principal = null)
        => Key(request, principal) is { } key && _items.ContainsKey(key);

    /// <summary>Whether a call to this tool in this workspace would be approved - asked before there is a request, to decide what a run is offered.</summary>
    public bool Approves(string workspaceRoot, string tool, string? principal = null)
        => !string.IsNullOrWhiteSpace(tool) && KeyOf(workspaceRoot, tool, principal) is { } key && _items.ContainsKey(key);

    public void Remember(DecisionRequest request, string? principal = null)
    {
        if (Key(request, principal) is { } key) _items.TryAdd(key, 0);
    }

    private static (string Root, string Tool, string Principal)? Key(DecisionRequest request, string? principal)
    {
        if (request.RequiresExplicitAnswer || request.Action is not { } action || string.IsNullOrWhiteSpace(request.Subject)) return null;
        return KeyOf(action.WorkingDirectory, request.Subject, principal);
    }

    /// <summary>
    /// Null for an empty principal: as in the approval store, a host that lost track of who is signed in is not
    /// handed the process account's answers - and the account's own are filed under "", which no name is.
    /// </summary>
    private static (string Root, string Tool, string Principal)? KeyOf(string workspaceRoot, string tool, string? principal)
    {
        if (principal is not null && string.IsNullOrWhiteSpace(principal)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        if (OperatingSystem.IsWindows()) root = root.ToUpperInvariant();
        return (root, tool.ToUpperInvariant(), principal?.Trim().ToLowerInvariant() ?? "");
    }
}
