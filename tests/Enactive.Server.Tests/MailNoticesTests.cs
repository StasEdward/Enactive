namespace Enactive.Server.Tests;

using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Server.Runs;
using Enactive.Settings;
using Enactive.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

/// <summary>
/// The inbox by mail (server plan phase 3: "tray notices -> inbox + mail"): what arrived since the last mail, in one
/// message, to the people server.json names - none named, none sent; nothing repeated, nothing lost over a restart.
/// </summary>
public sealed class MailNoticesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-notices", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly ServerEngine _engine;
    private readonly List<MimeMessage> _sent = new();

    public MailNoticesTests()
    {
        _workspace = Path.Combine(_root, "project");
        Directory.CreateDirectory(_workspace);
        var settings = new AppSettings();
        settings.Smtp.Host = "mail.lab.local";
        settings.Smtp.From = "enactive@lab.local";
        settings.Save(Path.Combine(_root, "settings.json"), replaceUnreadable: true);
        _engine = new ServerEngine(_root, new FileLogSink(Path.Combine(_root, "logs")));
        _engine.Workspaces.Touch(_workspace);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private MailNotices Notices(params string[] to) => new(_engine,
        new ServerOptions { NoticeRecipients = to, HostNames = ["enactive.lab.local"], Listen = ["0.0.0.0:8443"] },
        new ServerFolders(_root), NullLogger<MailNotices>.Instance,
        (_, message, _) => { _sent.Add(message); return Task.CompletedTask; });

    private Task Arrives(string title, string summary, string status = "unread", int secondsFromNow = 1)
    {
        var workspace = WorkspaceInfo.For(_workspace);
        return InboxStoreFactory.Create(workspace).AppendAsync(new InboxItem(Guid.NewGuid(), workspace.Id, "result", title, summary,
            Guid.Empty, status, DateTimeOffset.UtcNow.AddSeconds(secondsFromNow)), CancellationToken.None);
    }

    /// <summary>
    /// An item stamped before the newest one mailed, but filed after that mail - a run in another workspace ending while
    /// the inbox was read - is mailed too. The newest item mailed was the mark, and anything older was never news again.
    /// </summary>
    [Fact]
    public async Task An_item_filed_late_with_an_earlier_time_is_still_mailed()
    {
        var notices = Notices("stas@lab.local");
        await notices.TickAsync(CancellationToken.None);
        await Arrives("nightly review", "Completed", secondsFromNow: 3);
        Assert.Equal(1, await notices.TickAsync(CancellationToken.None));

        await Arrives("morning digest", "Completed", secondsFromNow: 2);

        Assert.Equal(1, await notices.TickAsync(CancellationToken.None));
        Assert.Contains("morning digest", _sent[^1].TextBody);
        Assert.Equal(0, await notices.TickAsync(CancellationToken.None));
    }

    [Fact]
    public async Task What_arrives_is_mailed_once_in_one_message_to_who_server_json_names()
    {
        var notices = Notices("stas@lab.local");
        await notices.TickAsync(CancellationToken.None);   // the first look: the inbox's past is not news

        await Arrives("morning digest", "Completed · 1 artifact(s)");
        await Arrives("nightly review", "Waiting for your decision");

        Assert.Equal(2, await notices.TickAsync(CancellationToken.None));
        var mail = Assert.Single(_sent);
        Assert.Equal("stas@lab.local", mail.To.Mailboxes.Single().Address);
        Assert.Equal("Enactive: 2 new in the inbox", mail.Subject);
        Assert.Contains("nightly review", mail.TextBody);
        Assert.Contains("https://enactive.lab.local:8443/inbox", mail.TextBody);

        Assert.Equal(0, await notices.TickAsync(CancellationToken.None));
        Assert.Single(_sent);
    }

    /// <summary>A restart reads where the last mail stopped: nothing mailed twice.</summary>
    [Fact]
    public async Task A_restart_mails_nothing_again()
    {
        await Notices("stas@lab.local").TickAsync(CancellationToken.None);
        await Arrives("morning digest", "Completed");
        await Notices("stas@lab.local").TickAsync(CancellationToken.None);

        Assert.Equal(0, await Notices("stas@lab.local").TickAsync(CancellationToken.None));
        Assert.Single(_sent);
    }

    [Fact]
    public async Task Nobody_named_is_nobody_mailed()
    {
        await Arrives("morning digest", "Completed");

        Assert.Equal(0, await Notices().TickAsync(CancellationToken.None));
        Assert.Empty(_sent);
    }

    /// <summary>What a person already read in the inbox is not news by mail.</summary>
    [Fact]
    public async Task An_item_already_read_is_not_mailed()
    {
        var notices = Notices("stas@lab.local");
        await notices.TickAsync(CancellationToken.None);
        await Arrives("morning digest", "Completed", status: "read");

        Assert.Equal(0, await notices.TickAsync(CancellationToken.None));
    }

    [Fact]
    public void A_recipient_that_is_not_an_address_keeps_the_service_from_starting()
        => Assert.Contains((new ServerOptions { NoticeRecipients = ["not an address"] }).Problems(), p => p.Contains("NoticeRecipients"));
}
