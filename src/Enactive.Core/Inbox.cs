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
    DateTimeOffset At,

    /// <summary>
    /// The schedule this came from, when a schedule is what produced it.
    ///
    /// <para>An ID rather than the name in <see cref="Title"/>, because the name is the one thing
    /// about a schedule a person is expected to change. Matching a schedule's outcomes by its title
    /// would work until somebody renamed it and then quietly report that a schedule which has been
    /// running for months has never run at all.</para>
    ///
    /// <para>Null for everything else, and for items filed before this existed - which is why it is
    /// nullable rather than <see cref="Guid.Empty"/>: "not from a schedule" and "from a schedule
    /// nobody recorded" are different, and only one of them is worth going to look for.</para>
    /// </summary>
    Guid? ScheduleId = null);

/// <summary>Persists and loads inbox items for a workspace.</summary>
public interface IInboxStore
{
    Task AppendAsync(InboxItem item, CancellationToken ct);
    Task<IReadOnlyList<InboxItem>> LoadAllAsync(CancellationToken ct);

    /// <summary>Marks one item read. Unknown ids are ignored - the inbox is never load-bearing.</summary>
    Task MarkReadAsync(Guid id, CancellationToken ct);

    Task MarkAllReadAsync(CancellationToken ct);
}
