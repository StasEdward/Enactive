namespace Enactive.Application.Projections;

using System.Globalization;
using Enactive.Agents;
using Enactive.Application.Mvvm;
using Enactive.Core.History;
using Enactive.Core.Inbox;

/// <summary>One item in the inbox list: the window's row, and the service's.</summary>
internal sealed class InboxItemViewModel : ObservableObject
{
    private bool _isUnread;

    public InboxItemViewModel(InboxItem item)
    {
        Item = item;
        _isUnread = InboxRows.IsUnread(item);
    }

    public InboxItem Item { get; }

    public string Title => Item.Title;
    public string Summary => Item.Summary;
    public string Kind => Item.Kind;
    public string When => InboxRows.When(Item.At);

    /// <summary>Unread items carry their weight, the way an unread mail does (Palette.Bold).</summary>
    public bool IsUnread
    {
        get => _isUnread;
        set => Set(ref _isUnread, value);
    }
}

/// <summary>One answer a waiting question offers, as its button says it.</summary>
internal sealed record InboxOption(string Id, string Label);

/// <summary>
/// The inbox's rows and words - which items are unread, what "Unread only" keeps, the status line, the run's header, a
/// question's answers - in the words the window has always shown them. Moved out of the window's InboxViewModel when the
/// service drew the same inbox: a second copy of "(recommended)" or of "2 unread · 5 items" is a second place for the
/// two to stop agreeing. Times and numbers are written the same in every culture: the service says them in its own
/// process's culture, not the reader's.
/// </summary>
internal static class InboxRows
{
    /// <summary>The store writes "unread" or "read"; any case is the same word.</summary>
    public static bool IsUnread(InboxItem item) => string.Equals(item.Status, "unread", StringComparison.OrdinalIgnoreCase);

    /// <summary>The list's rows: newest first.</summary>
    public static IReadOnlyList<InboxItemViewModel> Rows(IEnumerable<InboxItem> items)
        => items.OrderByDescending(i => i.At).Select(i => new InboxItemViewModel(i)).ToList();

    /// <summary>
    /// The rows the list shows. Rebuilt rather than filtered in place: the list is small, and this keeps "unread only"
    /// from having to reason about where an item that just became read used to sit.
    /// </summary>
    public static IReadOnlyList<InboxItemViewModel> Shown(IEnumerable<InboxItemViewModel> all, bool unreadOnly)
        => all.Where(i => Shows(i, unreadOnly)).ToList();

    /// <summary>Whether the list shows this row: every row, or with "Unread only" the unread ones.</summary>
    public static bool Shows(InboxItemViewModel row, bool unreadOnly) => !unreadOnly || row.IsUnread;

    /// <summary>The Inbox's name on the rail: "Inbox — 3 unread" while any are, else "Inbox". Every host's rail says it so.</summary>
    public static string Tooltip(int unread)
        => unread > 0 ? string.Create(CultureInfo.InvariantCulture, $"Inbox — {unread} unread") : "Inbox";

    /// <summary>The line beside the buttons: how many are unread, of how many.</summary>
    public static string Status(int unread, int total)
        => total == 0
            ? "Inbox is empty"
            : string.Create(CultureInfo.InvariantCulture, $"{unread} unread · {total} item{(total == 1 ? "" : "s")}");

    /// <summary>
    /// A time as the inbox writes it. The colon is quoted: in a format string an unquoted ':' is the culture's time
    /// separator, and the invariant culture's is a colon only by its own choice.
    /// </summary>
    public static string When(DateTimeOffset at) => Times.Minute(at);

    /// <summary>The chosen item's line under its title: what it is, and when it came.</summary>
    public static string Meta(InboxItemViewModel item) => $"{item.Kind} · {item.When}";

    /// <summary>When the question the item's run stopped at was asked.</summary>
    public static string Asked(ParkedDecision waiting) => "asked " + When(waiting.AskedAt);

    /// <summary>The answers, as buttons: the recommended one says so on itself, rather than by a colour alone.</summary>
    public static IReadOnlyList<InboxOption> Options(ParkedDecision waiting)
        => waiting.Options.Select(o => new InboxOption(o.Id,
                string.Equals(o.Id, waiting.RecommendedOptionId, StringComparison.OrdinalIgnoreCase) ? o.Label + " (recommended)" : o.Label))
            .ToList();

    /// <summary>
    /// The RUN line: the run's title, how it ended, on which model and in how long - or that it is gone. An old item whose
    /// run history was cleared still shows its own summary, just without a timeline.
    /// </summary>
    public static string RunHeader(RunSummary? summary)
        => summary is null
            ? "The run behind this item is no longer in the store."
            : string.Create(CultureInfo.InvariantCulture,
                $"{summary.Title} — {summary.Status} · {summary.Model} · {(summary.FinishedAt - summary.StartedAt).TotalSeconds:0}s");
}
