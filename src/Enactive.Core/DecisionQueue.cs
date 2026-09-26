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

/// <summary>A remembered decision is about a tool in the action's workspace, never the selected UI folder.</summary>
public sealed class SessionApprovals
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Root, string Tool), byte> _items = new();

    public bool Approves(DecisionRequest request)
        => Key(request) is { } key && _items.ContainsKey(key);

    public void Remember(DecisionRequest request)
    {
        if (Key(request) is { } key) _items.TryAdd(key, 0);
    }

    private static (string Root, string Tool)? Key(DecisionRequest request)
    {
        if (request.RequiresExplicitAnswer || request.Action is not { } action || string.IsNullOrWhiteSpace(request.Subject)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(action.WorkingDirectory));
        if (OperatingSystem.IsWindows()) root = root.ToUpperInvariant();
        return (root, request.Subject.ToUpperInvariant());
    }
}
