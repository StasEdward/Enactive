namespace Enactive.Remote.Gateway.Accounts;

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

/// <summary>
/// A provider's stable identity, not its display name or email. Validated outside the CLI so another
/// adapter cannot accidentally write Unicode into ASCII columns or admit an identity containing a newline.
/// </summary>
internal sealed partial record AdmissionIdentity
{
    public string Provider { get; }
    public string Subject { get; }

    private AdmissionIdentity(string provider, string subject) => (Provider, Subject) = (provider, subject);

    public static bool TryParse(string? text, [NotNullWhen(true)] out AdmissionIdentity? identity)
    {
        identity = null;
        if (text is null) return false;
        var match = Pattern().Match(text);
        if (!match.Success) return false;
        identity = new(match.Groups[1].Value, match.Groups[2].Value);
        return true;
    }

    public override string ToString() => $"{Provider}:{Subject}";

    // Match the database widths exactly. \z rather than $ prevents accepting the text before a
    // trailing newline, which would name a different subject from the one any provider reports.
    [GeneratedRegex(@"^([a-z0-9_-]{1,20}):([\x21-\x7E]{1,255})\z")]
    private static partial Regex Pattern();
}
