namespace Enactive.Core.Mail;

/// <summary>
/// The account <c>send_email</c> sends from, and the only addresses it may send to.
///
/// <para>Here rather than in settings because <c>Enactive.Tools</c> knows nothing about
/// <c>AppSettings</c> and must not: a tool takes what it needs as data, the way a provider takes
/// a <c>ProviderDescriptor</c>. The host fills this in from the person's SMTP section.</para>
///
/// <para><b>The recipients are part of the ACCOUNT, not of a call.</b> A model reads whatever it
/// was pointed at, and text it reads is not an instruction — but a tool that mails wherever it is
/// told makes that a promise rather than a boundary, and under <c>--approve allow</c> nobody is
/// watching. The list is chosen once by a person, so the tool refuses anything else by comparison
/// and never by judgement.</para>
/// </summary>
/// <param name="Password">Plaintext, in memory only. Decrypted from the DPAPI form on load.</param>
/// <param name="From">Blank falls back to <paramref name="User"/>, which is usually right.</param>
public sealed record MailAccount(
    string Host,
    int Port,
    bool StartTls,
    string User,
    string Password,
    string From,
    IReadOnlyList<string> Recipients)
{
    /// <summary>An account with nothing filled in: the tool is not offered at all.</summary>
    public static readonly MailAccount None =
        new("", 0, false, "", "", "", Array.Empty<string>());

    public bool Configured => !string.IsNullOrWhiteSpace(Host) && Recipients.Count > 0;

    /// <summary>Who a message goes to when the call does not say — the only one, when there is one.</summary>
    public string? Default => Recipients.Count > 0 ? Recipients[0] : null;

    public string Sender => string.IsNullOrWhiteSpace(From) ? User : From;

    /// <summary>Whether this address is one the person listed. Exact, case-insensitive, no patterns.</summary>
    public bool Allows(string address)
        => Recipients.Any(r => string.Equals(r.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase));
}
