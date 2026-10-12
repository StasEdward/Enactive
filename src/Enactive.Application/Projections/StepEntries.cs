namespace Enactive.Application.Projections;

using Enactive.Core.History;

/// <summary>
/// The icon a line of a step card carries, by what the step did. Moved from the window (StepCardViewModel) so the
/// service's step cards mark a command, a file or a refused call the way the window does.
/// </summary>
internal static class StepEntries
{
    /// <summary>Every kind named: one added later is refused here rather than drawn as a tool without a word.</summary>
    public static string Icon(FeedEntryKind kind) => kind switch
    {
        FeedEntryKind.Command => "⌘",
        FeedEntryKind.File => "📄",
        FeedEntryKind.Tool => "🛠",
        FeedEntryKind.Note => "🔔",
        FeedEntryKind.Refusal => "🛇",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A step's action with no icon.")
    };
}
