namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Agents;
using Enactive.Core.Permissions;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Enactive.Server.Accounts;
using Enactive.Server.Components.Pages;
using Enactive.Server.Components.Shell;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The Inbox as the window's (InboxWindow.axaml): the list of what came in, 360 px, with Refresh, Mark all read and Unread
/// only above it; the chosen item on the right - the question its run stopped at, WAITING FOR YOUR DECISION, with its
/// answers, then RUN, ARTIFACTS and DECISIONS. Answering carries the run on and opens it on the Runs screen. Every action is
/// the service's to decide (ServerInbox): a Viewer reads, an Operator answers and marks read.
/// </summary>
public sealed class InboxTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-inbox", Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public InboxTests()
    {
        // A run carried on starts only under the service's roots.
        Services.AddSingleton(new WorkspaceRoots([_folders]));
        _root = Path.Combine(_folders, "project");
        Directory.CreateDirectory(_root);
        Services.GetRequiredService<ServerEngine>().Workspaces.Touch(_root);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Enactive.Tests.SqlitePools.ClearUnder(_folders);
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
        }
    }

    private static readonly DateTimeOffset At = DateTimeOffset.Now.AddHours(-1);

    /// <summary>A run that stopped at "Approve send_email", and the inbox item it filed - a question nobody was there to answer.</summary>
    private async Task<(ParkedDecision Parked, InboxItem Item)> ParkedAsync()
    {
        var taskId = Guid.NewGuid();
        var parked = new DecisionLedger(_root).Park(new DecisionRequest(taskId, "Approve send_email", "to the team",
            [new("allow", "Allow"), new("deny", "Deny")], "allow", Subject: "send_email"));
        var workspace = WorkspaceInfo.For(_root);
        var run = new RunRecord(Guid.NewGuid(), taskId, "mail the digest", "qwen", At, At.AddSeconds(12), "NeedsUser",
            [new RunEventRecord(At, nameof(EventKind.ToolInvoked), "write_file digest.md")], ["digest.md"], ["Approve write_file: allow"]);
        await RunStoreFactory.Create(workspace).SaveAsync(run, CancellationToken.None);
        var item = new InboxItem(Guid.NewGuid(), workspace.Id, "decision", "mail the digest", "Waiting for your decision", run.RunId,
            "unread", At);
        await InboxStoreFactory.Create(workspace).AppendAsync(item, CancellationToken.None);
        return (parked, item);
    }

    /// <summary>An item a finished run filed, with no run behind it any more.</summary>
    private async Task<InboxItem> FiledAsync(string title, string status, DateTimeOffset at)
    {
        var workspace = WorkspaceInfo.For(_root);
        var item = new InboxItem(Guid.NewGuid(), workspace.Id, "result", title, "Completed", Guid.NewGuid(), status, at);
        await InboxStoreFactory.Create(workspace).AppendAsync(item, CancellationToken.None);
        return item;
    }

    private IRenderedComponent<InboxScreen> Inbox(string who, int items)
    {
        SignIn(who);
        var screen = Render<InboxScreen>();
        screen.WaitForAssertion(() => Assert.Equal(items, screen.FindAll(".inbox-list .inbox-item").Count));
        return screen;
    }

    private static void Open(IRenderedComponent<InboxScreen> screen, string title)
        => screen.FindAll(".inbox-list .inbox-item").First(i => i.QuerySelector(".inbox-item-title")!.TextContent.Trim() == title).Click();

    private static string[] Titles(IRenderedComponent<InboxScreen> screen)
        => screen.FindAll(".inbox-list .inbox-item .inbox-item-title").Select(t => t.TextContent.Trim()).ToArray();

    private static string[] Buttons(IRenderedComponent<InboxScreen> screen)
        => screen.FindAll("button").Select(b => b.TextContent.Trim()).Where(t => t.Length > 0).ToArray();

    private async Task<string> StatusOfAsync(Guid id)
        => (await InboxStoreFactory.Create(WorkspaceInfo.For(_root)).LoadAllAsync(CancellationToken.None)).Single(i => i.Id == id).Status;

    [Fact]
    public async Task An_operator_answers_and_the_run_carries_on()
    {
        var (parked, item) = await ParkedAsync();
        var screen = Inbox("alice", 1);

        Open(screen, "mail the digest");

        var question = screen.Find(".inbox-question");
        Assert.Equal("WAITING FOR YOUR DECISION", question.QuerySelector(".panel-header")!.TextContent.Trim());
        Assert.Equal("Approve send_email", question.QuerySelector(".inbox-question-topic")!.TextContent.Trim());
        Assert.Equal(["Allow (recommended)", "Deny"], question.QuerySelectorAll("button").Select(b => b.TextContent.Trim()));

        question.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Allow (recommended)").Click();

        // Carried on under the same task, as a run of the person who answered, and opened where the Runs screen shows it.
        var navigation = Services.GetRequiredService<NavigationManager>();
        screen.WaitForAssertion(() => Assert.Matches(@"/runs/[0-9a-f-]{36}$", navigation.Uri));
        var carried = Services.GetRequiredService<IRunControl>()
            .Find(ServerAuth.PrincipalOf(Users.Find("alice")!), Guid.Parse(navigation.Uri[^36..]))!;
        Assert.Equal(parked.TaskId, carried.TaskId);
        Assert.Equal("alice", carried.Owner);
        Assert.Equal("read", await StatusOfAsync(item.Id));
    }

    [Fact]
    public async Task A_viewer_reads_and_cannot_answer()
    {
        var (_, item) = await ParkedAsync();
        var screen = Inbox("vera", 1);

        Open(screen, "mail the digest");

        // Everything the window shows of the item and its run.
        var detail = screen.Find(".inbox-detail");
        Assert.Equal("mail the digest", detail.QuerySelector(".inbox-title")!.TextContent.Trim());
        Assert.Equal(["WAITING FOR YOUR DECISION", "RUN", "ARTIFACTS", "DECISIONS"],
            detail.QuerySelectorAll(".panel-header").Select(h => h.TextContent.Trim()));
        Assert.Equal("mail the digest — NeedsUser · qwen · 12s", detail.QuerySelector(".inbox-run-header")!.TextContent.Trim());
        screen.WaitForAssertion(() => Assert.Contains("write_file digest.md",
            screen.FindAll(".inbox-detail .timeline-row .timeline-summary").Select(r => r.TextContent.Trim())));
        Assert.Equal(["digest.md"], detail.QuerySelectorAll(".inbox-artifacts li").Select(l => l.TextContent.Trim()));
        Assert.Equal(["Approve write_file: allow"], detail.QuerySelectorAll(".inbox-decisions li").Select(l => l.TextContent.Trim()));

        // And nothing to do to it: no answer, no Mark all read - and reading it did not mark it read for everybody.
        Assert.Empty(screen.FindAll(".inbox-question button"));
        Assert.DoesNotContain("Mark all read", Buttons(screen));
        Assert.Equal("unread", await StatusOfAsync(item.Id));
    }

    [Fact]
    public async Task Unread_only_hides_read_items()
    {
        await FiledAsync("the read one", "read", At.AddMinutes(-5));
        await FiledAsync("the new one", "unread", At);
        var screen = Inbox("alice", 2);
        Assert.Equal(["the new one", "the read one"], Titles(screen));
        Assert.Equal("1 unread · 2 items", screen.Find(".inbox-status").TextContent.Trim());

        screen.Find("input[type=checkbox]").Change(true);

        Assert.Equal(["the new one"], Titles(screen));
    }

    /// <summary>Opening an item marks it read on the way, as the window does - the dot goes and the count with it.</summary>
    [Fact]
    public async Task Opening_an_item_marks_it_read()
    {
        var item = await FiledAsync("the new one", "unread", At);
        var screen = Inbox("alice", 1);
        Assert.Single(screen.FindAll(".inbox-item.unread"));

        Open(screen, "the new one");

        screen.WaitForAssertion(() => Assert.Empty(screen.FindAll(".inbox-item.unread")));
        Assert.Equal("0 unread · 1 item", screen.Find(".inbox-status").TextContent.Trim());
        Assert.Equal("read", await StatusOfAsync(item.Id));
        Assert.Equal("The run behind this item is no longer in the store.", screen.Find(".inbox-run-header").TextContent.Trim());
    }

    /// <summary>
    /// The rail's count follows Mark all read on this page, as the window's badge follows its Inbox (UnreadChanged). It was
    /// read again only at a navigation, and Mark all read is none: the icon went on saying "2 unread" over an inbox with none.
    /// </summary>
    [Fact]
    public async Task Mark_all_read_takes_the_count_off_the_inbox_icon()
    {
        await FiledAsync("one", "unread", At);
        await FiledAsync("two", "unread", At.AddMinutes(-1));
        var screen = Inbox("alice", 2);
        var rail = Render<Rail>();
        rail.WaitForAssertion(() => Assert.Equal("2", rail.Find("[data-rail='/inbox'] .badge").TextContent.Trim()));

        screen.FindAll("button").Single(b => b.TextContent.Trim() == "Mark all read").Click();

        screen.WaitForAssertion(() => Assert.Empty(screen.FindAll(".inbox-item.unread")));
        rail.WaitForAssertion(() => Assert.Empty(rail.FindAll("[data-rail='/inbox'] .badge")));
    }
}
