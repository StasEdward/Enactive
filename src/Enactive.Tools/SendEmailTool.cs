namespace Enactive.Tools;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Mail;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

/// <summary>
/// Sends one message through the person's own SMTP account, with files from the workspace
/// attached.
///
/// <para><b>It is the first tool that acts OUTSIDE this machine</b>, and everything unusual about
/// it follows from that. Every other tool changes a workspace, and a workspace can be reverted:
/// the journal keeps what was there before, a rejected step puts it back, and the worst case is
/// work to redo. A sent message cannot be recalled, and what it carries has left.</para>
///
/// <para><b>So the recipient is not an argument the model may invent.</b> It comes from
/// <see cref="MailAccount.Recipients"/>, which a person filled in, and an address that is not on
/// that list is refused by comparison rather than by judgement. That matters because the work
/// this tool exists for is reading somebody's log from somebody's server: text a model READS is
/// not an instruction, and a mail tool that will send wherever it is told turns that rule from a
/// boundary into a promise. Under <c>--approve allow</c> there is nobody to catch it.</para>
///
/// <para><b>And it always asks.</b> <see cref="RequiresApproval"/> is true whatever the policy
/// says, like the shells, and the question names the recipient, the subject and every attachment
/// — because the point of asking is that a person can see what is about to leave.</para>
/// </summary>
public sealed class SendEmailTool(MailAccount account) : ITool
{
    /// <summary>How many files one message may carry, and how large they may be together.</summary>
    /// <remarks>
    /// A limit rather than trust: an agent asked to "send the logs" can name a directory's worth
    /// of them, and most servers refuse a large message in a way that surfaces as a timeout
    /// halfway through. Ten files and twenty megabytes is more than any report and less than
    /// anything that will be silently dropped in transit.
    /// </remarks>
    internal const int MaxAttachments = 10;

    internal const long MaxAttachmentBytes = 20L * 1024 * 1024;

    /// <summary>How long a send may take before it is abandoned.</summary>
    internal const int TimeoutSeconds = 60;

    /// <summary>
    /// Registered whether or not an account exists, and SAYS WHICH.
    ///
    /// <para>Registering it only when configured looked tidier and broke the invariant a test
    /// guards: a role names <c>send_email</c>, so a host that does not register it leaves the
    /// role advertising a capability that is not there. The set of tools the roles name and the
    /// set the hosts register have to be the same set.</para>
    ///
    /// <para>So the state is in the description instead, where it costs nothing to learn. A model
    /// reading "there is no account configured" does not call it to find out - which is the same
    /// economy <c>ToolOffers</c> makes for a tool that could only ever be refused, arrived at
    /// without teaching the offer machinery about mail.</para>
    /// </summary>
    public ToolDefinition Definition { get; } = new(
        Name: "send_email",
        Description: account.Configured
            ? "Send an email from the configured account, optionally attaching files from the "
              + "workspace. The recipient CANNOT be chosen freely: it must be one of "
              + string.Join(", ", account.Recipients)
              + ". Leave 'to' out to use the first of them. Attach files by their workspace path; "
              + "write the file first and attach it, rather than pasting its contents into the body."
            : "Send an email. NOT AVAILABLE in this workspace: no SMTP account is configured, so "
              + "any call will be refused. Say so in your report rather than calling it.",
        JsonSchema: Schema);

    /// <summary>
    /// Execute, not Autonomous. Sending is not installing or deploying, and a run allowed to
    /// change files is the run this exists for; what keeps it safe is the recipient list and the
    /// question below, not a tier nobody sets.
    /// </summary>
    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    /// <summary>
    /// Whatever the policy says, unless the person turned the question off for this account.
    ///
    /// <para>A sent message cannot be put back, so the default is to ask, and the question names
    /// the recipient, the subject and every attachment - the point of asking is that somebody can
    /// see what is about to leave.</para>
    ///
    /// <para><b>And an unanswerable question is a refusal.</b> A run nobody is watching is not
    /// offered a tool that can only end in an approval prompt, which took the scheduled errand this
    /// tool exists for - read the log overnight, write the report, mail it - off the table
    /// entirely. <see cref="MailAccount.SendWithoutAsking"/> is how a person says "I have already
    /// answered": the recipient list then carries the whole of the consent, and it is still a list
    /// they typed, that no task can add to.</para>
    /// </summary>
    public bool RequiresApproval => !account.SendWithoutAsking;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        if (!account.Configured)
            return ToolResults.Fail(
                "No SMTP account is configured, so nothing can be sent. Fill in Settings → SMTP: "
                + "a host and at least one allowed recipient.");

        string? subject, body, to;
        List<string> attachments;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            subject = Text(root, "subject");
            body = Text(root, "body");
            to = Text(root, "to");
            attachments = Paths(root);
        }
        catch (JsonException ex)
        {
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(subject))
            return ToolResults.Unreadable("'subject' is required.");

        // Empty is allowed - a message that is only an attachment is a real thing to send - but
        // null and "" are the same to a reader, so neither is an error.
        body ??= "";

        var recipient = string.IsNullOrWhiteSpace(to) ? account.Default : to!.Trim();
        if (recipient is null)
            return ToolResults.Fail("No recipient is configured. Add one in Settings → SMTP.");

        // The refusal names the list, because a model that guessed once will otherwise guess again.
        if (!account.Allows(recipient))
            return ToolResults.Fail(
                $"'{recipient}' is not an address this workspace may send to. The allowed "
                + $"address(es): {string.Join(", ", account.Recipients)}. This is set by the "
                + "person in Settings → SMTP and cannot be changed from here - if the message "
                + "should go somewhere else, say so in your report and let them decide.");

        var files = new List<(string Path, string Full)>();
        long total = 0;

        foreach (var relative in attachments)
        {
            if (files.Count >= MaxAttachments)
                return ToolResults.Fail(
                    $"Too many attachments: {attachments.Count}, and a message may carry "
                    + $"{MaxAttachments}. Send fewer, or put them in one file first.");

            string full;
            try { full = WorkspacePaths.ResolveInside(ctx.WorkspaceRoot, relative); }
            catch (ArgumentException ex) { return ToolResults.Fail(ex.Message); }

            if (!File.Exists(full))
                return ToolResults.Fail(
                    $"There is no '{relative}' to attach. Write the file first, then attach it.");

            total += new FileInfo(full).Length;
            if (total > MaxAttachmentBytes)
                return ToolResults.Fail(
                    $"The attachments come to more than {MaxAttachmentBytes / 1024 / 1024} MB "
                    + "together, which most servers will refuse. Send a summary instead, or one "
                    + "file at a time.");

            files.Add((relative, full));
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(account.Sender));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = subject!.Trim();

        var content = new BodyBuilder { TextBody = body };
        foreach (var (_, full) in files)
            await content.Attachments.AddAsync(full, ct);
        message.Body = content.ToMessageBody();

        try
        {
            using var client = new SmtpClient { Timeout = TimeoutSeconds * 1000 };

            await client.ConnectAsync(account.Host, account.Port, SocketOptions(account), ct);

            // A server that wants no credentials is a real configuration - a relay on a LAN, a
            // local submission agent - and offering it an empty password is how that fails.
            if (!string.IsNullOrWhiteSpace(account.User))
                await client.AuthenticateAsync(account.User, account.Password, ct);

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The real error, and nothing invented on top of it. A mail server's refusal says why
            // - a wrong password, a blocked port, a sender it will not relay for - and that
            // sentence is worth more than any wording of ours.
            return ToolResults.Fail($"The message was not sent: {ex.Message}");
        }

        var carried = files.Count == 0
            ? ""
            : $" with {files.Count} attachment(s): {string.Join(", ", files.Select(f => f.Path))}";

        return ToolResults.Ok(
            output: $"Sent to {recipient}: \"{message.Subject}\"{carried}.",
            metadata: new Dictionary<string, object?>
            {
                ["to"] = recipient,
                ["subject"] = message.Subject,
                ["attachments"] = files.Count
            });
    }

    /// <summary>
    /// What "STARTTLS" means, which is what the Settings pane says it means: ON requires the
    /// upgrade, OFF is implicit TLS on 465 and no TLS anywhere else.
    ///
    /// <para>It used to pass <see cref="SecureSocketOptions.Auto"/> when the switch was off, and
    /// Auto upgrades whenever the server merely ADVERTISES the extension. An internal relay is
    /// exactly the case that breaks: ours advertises STARTTLS and presents a certificate issued to
    /// its own LAN name, so a switch the person had turned OFF became a hostname failure - the
    /// message never left, and nothing in the settings explained why. Measured 2026-09-20 against
    /// a relay on the local network: <c>Auto</c> fails with "did not match the name given in the server's SSL
    /// certificate (enactiveapp.lan)", <c>None</c> connects.</para>
    ///
    /// <para>This is not a licence to send in the clear: it is the choice the person made, and
    /// turning the switch on still requires the upgrade or fails.</para>
    /// </summary>
    internal static SecureSocketOptions SocketOptions(MailAccount account)
        => account.StartTls
            ? SecureSocketOptions.StartTls
            : account.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.None;

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static List<string> Paths(JsonElement root)
    {
        var list = new List<string>();

        if (!root.TryGetProperty("attachments", out var value))
            return list;

        // One path as a bare string is what a model writes when there is only one, and refusing
        // it would be refusing the commonest correct call over its punctuation.
        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.GetString() is { Length: > 0 } one) list.Add(one);
            return list;
        }

        if (value.ValueKind == JsonValueKind.Array)
            foreach (var e in value.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
                    list.Add(s);

        return list;
    }

    private static readonly string Schema = """
    {
      "type": "object",
      "properties": {
        "subject": { "type": "string", "description": "The subject line." },
        "body": { "type": "string", "description": "The message text. Plain text, not HTML." },
        "to": { "type": "string", "description": "One of the configured recipients. Omit for the first of them." },
        "attachments": {
          "type": "array",
          "items": { "type": "string" },
          "description": "Workspace-relative paths of files to attach."
        }
      },
      "required": ["subject"]
    }
    """;
}
