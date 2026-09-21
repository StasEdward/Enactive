namespace Enactive.App.Ui.ViewModels;

using Enactive.App.Ui.Mvvm;

/// <summary>
/// The SMTP pane: the account <c>send_email</c> sends from, and the only addresses it may send to.
///
/// <para><b>The recipients box is the security of the feature, and the pane says so.</b> A tool
/// that mails wherever the model asks is a way out for anything the agent can read, and the work
/// it exists for is reading somebody else's log. So the list is typed here, once, by a person,
/// and the tool refuses anything else by comparison. A pane that presented this as a convenience
/// would get it filled in with a wildcard.</para>
///
/// <para>The password follows the device token exactly, for the same two reasons: a box
/// pre-filled with the real one puts a credential on screen for whoever is standing behind you,
/// and a box that empties itself on open would silently clear the password of anyone who came in
/// to change the port.</para>
/// </summary>
internal sealed partial class SettingsViewModel
{
    private const int SectionSmtp = 11;

    /// <summary>What the password box shows for one already stored — see <c>TokenUnchanged</c>.</summary>
    private const string PasswordUnchanged = "········ (stored)";

    public bool IsSmtp => Section == SectionSmtp;

    public RelayCommand ShowSmtpCommand { get; private set; } = null!;

    private string _smtpHost = string.Empty;
    private string _smtpPort = string.Empty;
    private bool _smtpStartTls = true;
    private string _smtpUser = string.Empty;
    private string _smtpPassword = string.Empty;
    private string _smtpFrom = string.Empty;
    private string _smtpRecipients = string.Empty;
    private string _smtpProblem = string.Empty;

    /// <summary>
    /// A password on disk this Windows account cannot decrypt. Remembered because the box is then
    /// empty and looks exactly like "none was ever set", which sends somebody after the wrong
    /// problem.
    /// </summary>
    private bool _smtpPasswordUnreadable;

    public string SmtpHost
    {
        get => _smtpHost;
        set { if (Set(ref _smtpHost, value)) Revalidate(); }
    }

    public string SmtpPort
    {
        get => _smtpPort;
        set { if (Set(ref _smtpPort, value)) Revalidate(); }
    }

    public bool SmtpStartTls { get => _smtpStartTls; set => Set(ref _smtpStartTls, value); }
    public string SmtpUser { get => _smtpUser; set => Set(ref _smtpUser, value); }
    public string SmtpPassword { get => _smtpPassword; set => Set(ref _smtpPassword, value); }
    public string SmtpFrom { get => _smtpFrom; set => Set(ref _smtpFrom, value); }

    /// <summary>One address per line. A list, not a pattern: see the note on the class.</summary>
    public string SmtpRecipients
    {
        get => _smtpRecipients;
        set { if (Set(ref _smtpRecipients, value)) Revalidate(); }
    }

    public string SmtpProblem
    {
        get => _smtpProblem;
        private set { if (Set(ref _smtpProblem, value)) OnPropertyChanged(nameof(HasSmtpProblem)); }
    }

    public bool HasSmtpProblem => SmtpProblem.Length > 0;

    /// <summary>
    /// What the tool will do with what is typed, in one line, so the pane answers the question it
    /// raises: a host and no recipient is a section that looks filled in and offers nothing.
    /// </summary>
    public string SmtpState
        => _smtpPasswordUnreadable
            ? "There is a stored password this Windows account cannot read. Type it again."
            : SmtpHost.Trim().Length == 0
                ? "Not configured — send_email will tell the agent it is unavailable."
                : RecipientLines().Count == 0
                    ? "No recipients, so send_email stays unavailable: there is nowhere it may write to."
                    : $"send_email may write to {string.Join(", ", RecipientLines())} and nowhere else.";

    private void InitializeSmtp()
    {
        ShowSmtpCommand = new(() => Section = SectionSmtp);

        var smtp = _working.Smtp;
        _smtpHost = smtp.Host;
        _smtpPort = smtp.Port > 0 ? smtp.Port.ToString() : string.Empty;
        _smtpStartTls = smtp.StartTls;
        _smtpUser = smtp.User;
        _smtpFrom = smtp.From;
        _smtpRecipients = string.Join(Environment.NewLine, smtp.Recipients);
        _smtpPassword = string.IsNullOrEmpty(smtp.Password) ? string.Empty : PasswordUnchanged;
        _smtpPasswordUnreadable =
            string.IsNullOrEmpty(smtp.Password) && !string.IsNullOrEmpty(smtp.PasswordProtected);
    }

    /// <summary>The recipients as typed, one per line, blanks dropped.</summary>
    private List<string> RecipientLines()
        => SmtpRecipients
            .Split('\n', '\r', ',', ';')
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .ToList();

    /// <summary>
    /// What is wrong with the pane, or blank. Only refuses what would go wrong silently: a port
    /// that is not a number, and an address with no <c>@</c> in it, which is the typo that turns
    /// into a send refused by the server twenty minutes into a scheduled run.
    /// </summary>
    private string SmtpValidation()
    {
        if (SmtpPort.Trim().Length > 0
            && (!int.TryParse(SmtpPort.Trim(), out var port) || port is < 1 or > 65535))
            return "Port must be a number between 1 and 65535.";

        var bad = RecipientLines().FirstOrDefault(r => !r.Contains('@'));
        if (bad is not null)
            return $"'{bad}' is not an email address.";

        if (SmtpFrom.Trim().Length > 0 && !SmtpFrom.Contains('@'))
            return "The From address does not look like an email address.";

        return string.Empty;
    }

    private void SaveSmtp()
    {
        var smtp = _working.Smtp;

        smtp.Host = SmtpHost.Trim();
        smtp.Port = int.TryParse(SmtpPort.Trim(), out var port) && port > 0 ? port : 587;
        smtp.StartTls = SmtpStartTls;
        smtp.User = SmtpUser.Trim();
        smtp.From = SmtpFrom.Trim();
        smtp.Recipients = RecipientLines();

        // The placeholder means "leave the stored password alone". Anything else - including an
        // empty box - is what was meant, so clearing the box clears the password.
        if (SmtpPassword != PasswordUnchanged)
            smtp.Password = SmtpPassword;
    }
}
