namespace Enactive.Server.Runs;

using System.Text;
using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Inbox;
using Enactive.Settings;
using Enactive.Workspace;
using MimeKit;

/// <summary>
/// What the window's tray notice says - a run nobody was watching finished, a schedule could not start, a run stopped
/// at a question - said by mail on the service (ADR 0002 §10: "tray notices become the inbox in the web UI, and mail").
/// A service has no tray, and its people are not looking at its pages all day.
///
/// <para>Every minute, the inbox items that arrived since the last mail, across the workspaces the service knows, in
/// ONE message - a night of schedules is one mail in the morning, not twenty. Sent to <c>NoticeRecipients</c> from
/// server.json through the mail account the settings configure; none named, none sent. What was last sent is kept in
/// the data folder, so a restart neither repeats a mail nor loses one.</para>
/// </summary>
internal sealed class MailNotices(ServerEngine engine, ServerOptions options, ServerFolders folders, ILogger<MailNotices> log,
    Func<Core.Mail.MailAccount, MimeMessage, CancellationToken, Task>? send = null) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private readonly Func<Core.Mail.MailAccount, MimeMessage, CancellationToken, Task> _send = send ?? Tools.MailSending.SendAsync;
    private string StateFile => Path.Combine(folders.Data, "notices.json");

    private sealed record State(DateTimeOffset Since);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        if (options.NoticeRecipients.Count == 0)
            return;
        using var timer = new PeriodicTimer(Every);
        do
        {
            try { await TickAsync(stopping); }
            catch (Exception ex) when (!stopping.IsCancellationRequested)
            {
                log.LogWarning(ex, "The inbox could not be mailed this minute.");
            }
        }
        while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false));
    }

    /// <summary>One look: mails what is new, and says how many items it mailed (0 when none, or nothing configured).</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        if (options.NoticeRecipients.Count == 0)
            return 0;
        // The settings' mail server, not the agent's mail account (EngineComposition.Mail): that one is "configured"
        // only with recipients the AGENT may write to, and a service that mails notices need not let runs mail anyone.
        var smtp = (engine.Settings ?? new AppSettings()).Smtp;
        if (string.IsNullOrWhiteSpace(smtp.Host))
        {
            log.LogWarning("NoticeRecipients are named in server.json, but no mail server is set in the settings; nothing is mailed.");
            return 0;
        }
        var account = new Core.Mail.MailAccount(smtp.Host.Trim(), smtp.Port, smtp.StartTls, smtp.User.Trim(), smtp.Password,
            smtp.From.Trim(), []);

        var since = ReadSince();
        var fresh = new List<(string Workspace, InboxItem Item)>();
        foreach (var workspace in engine.Workspaces.Entries)
        {
            try
            {
                foreach (var item in await InboxStoreFactory.Create(WorkspaceInfo.For(workspace.RootPath)).LoadAllAsync(ct))
                    if (item.At > since && item.Status == "unread")
                        fresh.Add((workspace.Name, item));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A folder whose inbox cannot be read now is read again next minute; the others are mailed.
            }
        }
        if (fresh.Count == 0)
            return 0;

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(account.Sender));
        foreach (var to in options.NoticeRecipients)
            message.To.Add(MailboxAddress.Parse(to));
        message.Subject = fresh.Count == 1
            ? $"Enactive: {fresh[0].Item.Title} - {fresh[0].Item.Summary}"
            : $"Enactive: {fresh.Count} new in the inbox";
        var body = new StringBuilder();
        foreach (var (workspace, item) in fresh.OrderBy(f => f.Item.At))
            body.AppendLine($"{item.At.ToLocalTime():yyyy-MM-dd HH:mm}  {workspace}  {item.Title}")
                .AppendLine($"    {item.Summary}")
                .AppendLine();
        body.AppendLine(options.HostNames.Count > 0
            ? $"The inbox: https://{options.HostNames[0]}{PortSuffix()}/inbox"
            : "Open the inbox on the service.");
        message.Body = new TextPart("plain") { Text = body.ToString() };

        await _send(account, message, ct);
        WriteSince(fresh.Max(f => f.Item.At));
        return fresh.Count;
    }

    private string PortSuffix()
        => options.Listen.FirstOrDefault() is { } listen && System.Net.IPEndPoint.TryParse(listen, out var endpoint) && endpoint.Port != 443
            ? ":" + endpoint.Port
            : "";

    /// <summary>When the last mailed item arrived; for a first start, now - the inbox's past is not news.</summary>
    private DateTimeOffset ReadSince()
    {
        try
        {
            if (File.Exists(StateFile) && JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } state)
                return state.Since;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        var now = DateTimeOffset.UtcNow;
        WriteSince(now);
        return now;
    }

    private void WriteSince(DateTimeOffset since)
    {
        Directory.CreateDirectory(folders.Data);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(new State(since)));
    }
}
