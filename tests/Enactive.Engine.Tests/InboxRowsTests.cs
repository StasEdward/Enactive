namespace Enactive.Engine.Tests;

using System.Globalization;
using Enactive.Agents;
using Enactive.Application.Projections;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Xunit;

/// <summary>
/// The inbox's rows and words - what is unread, what "Unread only" keeps, the status line, the run's header, the labels
/// of a question's answers - as the window has always said them. Kept inside the window's view model until the service
/// drew the same inbox: a second copy of "(recommended)" or of "3 unread · 5 items" is a second place for the two to
/// stop agreeing.
/// </summary>
public sealed class InboxRowsTests
{
    private static InboxItem Item(string status, DateTimeOffset at, string kind = "result")
        => new(Guid.NewGuid(), Guid.NewGuid(), kind, "digest", "done", Guid.NewGuid(), status, at);

    [Fact]
    public void Rows_are_newest_first_and_unread_is_the_item_s_status_in_any_case()
    {
        var at = DateTimeOffset.UtcNow;
        var rows = InboxRows.Rows([Item("read", at.AddHours(-2)), Item("UNREAD", at), Item("unread", at.AddHours(-1))]);

        Assert.Equal([at, at.AddHours(-1), at.AddHours(-2)], rows.Select(r => r.Item.At));
        Assert.Equal([true, true, false], rows.Select(r => r.IsUnread));
    }

    [Fact]
    public void Unread_only_keeps_the_unread_rows_in_their_order()
    {
        var at = DateTimeOffset.UtcNow;
        var rows = InboxRows.Rows([Item("unread", at), Item("read", at.AddMinutes(-1)), Item("unread", at.AddMinutes(-2))]);

        Assert.Equal(rows, InboxRows.Shown(rows, unreadOnly: false));
        Assert.Equal([rows[0], rows[2]], InboxRows.Shown(rows, unreadOnly: true));

        rows[0].IsUnread = false;   // opened: read from then on
        Assert.Equal([rows[2]], InboxRows.Shown(rows, unreadOnly: true));
    }

    [Theory]
    [InlineData(0, 0, "Inbox is empty")]
    [InlineData(1, 1, "1 unread · 1 item")]
    [InlineData(2, 5, "2 unread · 5 items")]
    public void The_status_line_is_the_window_s(int unread, int total, string said)
        => Assert.Equal(said, InboxRows.Status(unread, total));

    [Fact]
    public void The_recommended_answer_says_so_on_its_button()
    {
        var waiting = new ParkedDecision(Guid.NewGuid(), Guid.NewGuid(), "Approve send_email", "", "to the team", "send_email",
            [new("allow", "Allow"), new("deny", "Deny")], "ALLOW", DateTimeOffset.UtcNow);

        Assert.Equal([("allow", "Allow (recommended)"), ("deny", "Deny")], InboxRows.Options(waiting).Select(o => (o.Id, o.Label)));
    }

    [Fact]
    public void The_run_s_header_says_its_title_status_model_and_seconds_or_that_it_is_gone()
    {
        var at = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var run = new RunSummary(Guid.NewGuid(), Guid.NewGuid(), "mail the digest", "qwen", at, at.AddSeconds(42.4), "Completed", [], []);

        Assert.Equal("mail the digest — Completed · qwen · 42s", InboxRows.RunHeader(run));
        Assert.Equal("The run behind this item is no longer in the store.", InboxRows.RunHeader(null));
    }

    /// <summary>
    /// The times are the window's "yyyy-MM-dd HH:mm" under every culture: the service says them in its own process's
    /// culture, not the reader's, and a culture whose time separator is not a colon wrote "09.05" on one box and "09:05"
    /// on another.
    /// </summary>
    [Fact]
    public void A_time_is_written_the_same_in_every_culture()
    {
        var at = new DateTimeOffset(2026, 10, 1, 9, 5, 0, TimeSpan.Zero);
        var expected = at.ToLocalTime().ToString("yyyy-MM-dd HH':'mm", CultureInfo.InvariantCulture);
        var was = CultureInfo.CurrentCulture;
        try
        {
            // fi-FI writes the time with a dot: "09.05".
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
            Assert.Equal(expected, InboxRows.When(at));
            var row = InboxRows.Rows([Item("unread", at, kind: "decision")]).Single();
            Assert.Equal("decision · " + expected, InboxRows.Meta(row));
            var waiting = new ParkedDecision(Guid.NewGuid(), Guid.NewGuid(), "t", "", "", null, [], null, at);
            Assert.Equal("asked " + expected, InboxRows.Asked(waiting));
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }
}
