namespace Enactive.Server.Components.Pages;

using System.Security.Claims;
using Enactive.Agents;
using Enactive.Application.Projections;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;

/// <summary>
/// One row of the list: the window's row (InboxItemViewModel - its title, kind, time and whether it is unread), and what
/// the service adds to it - the workspace it is from, the question it waits on and the run behind it.
/// </summary>
internal sealed record InboxCard(InboxEntry Entry, InboxItemViewModel Row);

/// <summary>
/// What the Inbox page shows and does - the window's InboxViewModel for one person in a browser, over every workspace the
/// service knows rather than the one open folder. A plain class, so what it decides is tested without a page; the rows and
/// their words are the window's own (InboxRows). It offers by role; every action is still decided by ServerInbox, which
/// checks the account again at the action.
/// </summary>
internal sealed class InboxState(ServerInbox inbox, ClaimsPrincipal user)
{
    private IReadOnlyList<InboxCard> _all = [];
    private bool _unreadOnly;
    private bool _answering;

    /// <summary>The rows shown: every item, newest first, or with <see cref="UnreadOnly"/> the unread ones.</summary>
    public IReadOnlyList<InboxCard> Items { get; private set; } = [];

    public InboxCard? Selected { get; private set; }

    /// <summary>
    /// The question the chosen item's run stopped at, while it waits - gone once answered here, as the window takes its
    /// buttons away before anything can be clicked twice.
    /// </summary>
    public ParkedDecision? Waiting { get; private set; }

    /// <summary>The chosen run's timeline, read for it alone when it is opened; empty until then.</summary>
    public IReadOnlyList<RunEventViewModel> Timeline { get; private set; } = [];

    /// <summary>What the answer came to - carried on, or why not (the window's DecisionStatus).</summary>
    public string? Answered { get; private set; }

    /// <summary>What the service said to the last action on the list, when it refused it.</summary>
    public string? Said { get; private set; }

    /// <summary>
    /// Offered answering and Mark all read. Only offered: ServerInbox refuses both to anybody else, whatever a page sent.
    /// </summary>
    public bool CanOperate => ServerAuth.RoleOf(user) >= Role.Operator;

    /// <summary>An answer is on its way: a second press is not a second answer.</summary>
    public bool Busy => _answering;

    public string Status => InboxRows.Status(_all.Count(c => c.Row.IsUnread), _all.Count);

    public bool UnreadOnly
    {
        get => _unreadOnly;
        set
        {
            _unreadOnly = value;
            ApplyFilter();
        }
    }

    /// <summary>Reads the inbox again, keeping the item open if it is still listed - as the window's Refresh does.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var keep = Selected?.Entry;
        Said = null;
        // Newest first: ListAsync's order, the window's (InboxRows.Rows).
        _all = (await inbox.ListAsync(user, ct)).Select(e => new InboxCard(e, new InboxItemViewModel(e.Item))).ToList();
        ApplyFilter();
        if (keep is not null && Items.FirstOrDefault(c => Same(c.Entry, keep)) is { } again)
            await OpenAsync(again, ct);
    }

    /// <summary>
    /// Opens an item: its detail, its question, its run's timeline - and marks it read on the way, as the window does. Only
    /// for an Operator: what is read is the service's, the same for every person, and a Viewer's reading it would have
    /// taken it out of everybody's unread.
    /// </summary>
    public async Task OpenAsync(InboxCard card, CancellationToken ct)
    {
        Selected = card;
        Waiting = card.Entry.Waiting;
        Answered = null;
        Timeline = [];

        if (card.Row.IsUnread && CanOperate)
        {
            card.Row.IsUnread = false;
            if (await inbox.MarkReadAsync(user, card.Entry.Workspace, card.Row.Item.Id, ct) is { } refused)
            {
                card.Row.IsUnread = true;
                Said = refused;
            }
        }

        if (card.Entry.Run is null)
            return;
        var timeline = await inbox.TimelineAsync(user, card.Entry.Workspace, card.Row.Item.RunId, ct);
        // An earlier item's timeline can land after a later one's was asked for: filling the panel with the run somebody
        // has already clicked away from is worse than filling it late, so it is dropped (the window's LoadTimelineAsync).
        if (ReferenceEquals(Selected, card))
            Timeline = timeline ?? [];
    }

    /// <summary>Marks the whole inbox read - every workspace's items, as the page lists them.</summary>
    public async Task MarkAllReadAsync(CancellationToken ct)
    {
        if (await inbox.MarkAllReadAsync(user, ct) is { } refused)
        {
            Said = refused;
            return;
        }
        Said = null;
        foreach (var card in _all)
            card.Row.IsUnread = false;
        ApplyFilter();
    }

    /// <summary>
    /// Answers the question shown, once (ServerInbox.AnswerAsync); the run carried on, when one was - the page opens it
    /// where the Runs screen shows it.
    /// </summary>
    public async Task<Guid?> AnswerAsync(string optionId, CancellationToken ct)
    {
        if (_answering || Waiting is null || Selected is not { } card)
            return null;
        _answering = true;
        Waiting = null;   // one answer: the buttons go before anything can be clicked twice
        try
        {
            var (said, run) = await inbox.AnswerAsync(user, card.Entry.Workspace, card.Row.Item.Id, optionId, ct);
            Answered = said;
            return run;
        }
        finally
        {
            _answering = false;
        }
    }

    private void ApplyFilter()
    {
        Items = _all.Where(c => InboxRows.Shows(c.Row, _unreadOnly)).ToList();
        // An item the filter took away is closed with it, as the window's list drops its selection.
        if (Selected is { } open && !Items.Contains(open))
        {
            Selected = null;
            Waiting = null;
            Answered = null;
            Timeline = [];
        }
    }

    private static bool Same(InboxEntry a, InboxEntry b) => a.Item.Id == b.Item.Id && a.Workspace == b.Workspace;
}
