namespace Enactive.App.Ui.ViewModels;

using System.Collections.ObjectModel;
using Enactive.App.Ui.Mvvm;
using Enactive.Settings;

// The pane has a property called MailRoles (the rows); this is the RULE the rows are built from.
using MailRoleRules = Enactive.Settings.MailRoles;

/// <summary>
/// One role, and whether it may send. See <see cref="MailRoles"/> for why the tick is here rather
/// than only in the team editor, and why a wildcarded role is shown on and left alone.
/// </summary>
internal sealed class MailRoleRow(string id, string role, bool granted, bool fixedOn) : ObservableObject
{
    private bool _granted = granted;

    public string Id { get; } = id;
    public string Role { get; } = role;

    /// <summary>A role holding <c>*</c>: already has the tool, and not ours to take away here.</summary>
    public bool Fixed { get; } = fixedOn;

    public bool Changeable => !Fixed;

    public bool Granted
    {
        get => _granted;
        set => Set(ref _granted, value);
    }
}

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
    private bool _smtpSendWithoutAsking;
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

    /// <summary>
    /// Off means the tool asks every time and is therefore not offered at all to a run nobody is
    /// watching. On means the recipient list is the whole of the consent.
    /// </summary>
    public bool SmtpSendWithoutAsking
    {
        get => _smtpSendWithoutAsking;
        set { if (Set(ref _smtpSendWithoutAsking, value)) OnPropertyChanged(nameof(SmtpAsking)); }
    }

    /// <summary>The consequence of the switch, said where the switch is.</summary>
    public string SmtpAsking
        => SmtpSendWithoutAsking
            ? "A scheduled run and a run started from a phone may send on their own — to the "
              + "addresses above, and to nothing else."
            : "Every send asks first, and the question names the recipient, the subject and each "
              + "attachment. A run nobody is watching is not offered the tool at all, because its "
              + "question could never be answered.";

    public string SmtpProblem
    {
        get => _smtpProblem;
        private set { if (Set(ref _smtpProblem, value)) OnPropertyChanged(nameof(HasSmtpProblem)); }
    }

    public bool HasSmtpProblem => SmtpProblem.Length > 0;

    /// <summary>
    /// The roles that may call the tool, ticked here rather than only in the team editor — see
    /// <see cref="MailRoles"/>. Empty when no role is allowed to run anything at all.
    /// </summary>
    public ObservableCollection<MailRoleRow> MailRoles { get; } = new();

    public bool HasMailRoles => MailRoles.Count > 0;

    /// <summary>
    /// What the tool will do with what is typed, in one line, so the pane answers the question it
    /// raises: a host and no recipient is a section that looks filled in and offers nothing.
    ///
    /// <para>The last case is the one that cost an evening: an account filled in correctly, and no
    /// role naming <c>send_email</c>, so every run was offered eleven tools and not that one. The
    /// pane used to say "send_email may write to …" over exactly that, which was true about the
    /// tool and false about the application.</para>
    /// </summary>
    public string SmtpState
        => _smtpPasswordUnreadable
            ? "There is a stored password this Windows account cannot read. Type it again."
            : SmtpHost.Trim().Length == 0
                ? "Not configured — send_email will tell the agent it is unavailable."
                : RecipientLines().Count == 0
                    ? "No recipients, so send_email stays unavailable: there is nowhere it may write to."
                    : MailRoles.All(r => !r.Granted)
                        ? "Nobody may use it yet: tick a role below, or no run will be offered "
                          + "send_email at all."
                        : $"{string.Join(", ", MailRoles.Where(r => r.Granted).Select(r => r.Role))} "
                          + $"may write to {string.Join(", ", RecipientLines())} and nowhere else.";

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
        _smtpSendWithoutAsking = smtp.SendWithoutAsking;
        _smtpPassword = string.IsNullOrEmpty(smtp.Password) ? string.Empty : PasswordUnchanged;
        _smtpPasswordUnreadable =
            string.IsNullOrEmpty(smtp.Password) && !string.IsNullOrEmpty(smtp.PasswordProtected);

        foreach (var worker in _working.Workers.Where(MailRoleRules.CanCarry))
        {
            var row = new MailRoleRow(
                worker.Id,
                string.IsNullOrWhiteSpace(worker.Role) ? worker.Id : worker.Role,
                granted: MailRoleRules.Carries(worker),
                fixedOn: MailRoleRules.Wildcarded(worker));

            // The sentence above the list has to change with the ticks, or a person turns the last
            // one off and the pane goes on saying who may send.
            row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SmtpState));
            MailRoles.Add(row);
        }
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
        smtp.SendWithoutAsking = SmtpSendWithoutAsking;

        // The placeholder means "leave the stored password alone". Anything else - including an
        // empty box - is what was meant, so clearing the box clears the password.
        if (SmtpPassword != PasswordUnchanged)
            smtp.Password = SmtpPassword;

        // The ticks are a change to the TEAM, written where the team is kept, so the team editor
        // and this pane cannot disagree about who may send.
        MailRoleRules.Apply(
            _working.Workers,
            MailRoles.Where(r => r.Granted).Select(r => r.Id).ToArray());
    }
}
