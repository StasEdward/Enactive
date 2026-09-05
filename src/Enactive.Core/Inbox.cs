namespace Enactive.Core.Inbox;

/// <summary>
/// An item in the AI Inbox (PLAN_v2 §9): the durable outcome of a background run, or a decision a
/// background run hit while no human was watching. The Inbox is how headless work reports back.
/// </summary>
public sealed record InboxItem(
    Guid Id,
    Guid WorkspaceId,
    string Kind,        // "result" | "decision" | "error"
    string Title,
    string Summary,
    Guid RunId,
    string Status,      // "unread" | "read"
    DateTimeOffset At);

/// <summary>Persists and loads inbox items for a workspace.</summary>
public interface IInboxStore
{
    Task AppendAsync(InboxItem item, CancellationToken ct);
    Task<IReadOnlyList<InboxItem>> LoadAllAsync(CancellationToken ct);

    /// <summary>Marks one item read. Unknown ids are ignored - the inbox is never load-bearing.</summary>
    Task MarkReadAsync(Guid id, CancellationToken ct);

    Task MarkAllReadAsync(CancellationToken ct);
}
