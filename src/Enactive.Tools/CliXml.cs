namespace Enactive.Tools;

using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>
/// Turns PowerShell's serialised streams back into the words a person - or a model - can read.
///
/// <para><b>Why this exists.</b> <c>-EncodedCommand</c> puts powershell.exe in "minishell" mode, and
/// a minishell writes its non-output streams as CLIXML whenever they are redirected. Every error
/// therefore reached the model looking like this:</para>
///
/// <code>
/// #&lt; CLIXML
/// &lt;Objs Version="1.1.0.1" ...&gt;&lt;Obj S="progress" ...&gt;&lt;AV&gt;Preparing modules for first use.&lt;/AV&gt;...
/// &lt;S S="Error"&gt;Get-Content : Cannot find path 'C:\no\such.txt' because it does not exist._x000D__x000A_&lt;/S&gt;
/// </code>
///
/// <para>...under a heading that said "the output above says what went wrong". It did, somewhere in
/// there, and no model was going to find it. Measured on 2026-09-10: the trigger is
/// <c>-EncodedCommand</c> itself, not the redirection and not the shape of the pipe - the same
/// script run with <c>-Command</c> gives plain text, and <c>-OutputFormat Text</c> changes nothing
/// about the error stream. So this cannot be turned off from the command line; it has to be
/// decoded.</para>
///
/// <para><b>Progress is dropped, not translated.</b> "Preparing modules for first use" is not an
/// error and never was; two of them arrived in front of every answer. A blob with nothing but
/// progress in it decodes to nothing at all, which is the truthful reading of a script that had
/// nothing to report.</para>
///
/// <para><b>It never destroys.</b> Anything that is not CLIXML comes back untouched, and a blob that
/// will not parse comes back untouched too. A decoder that loses the one line saying what went
/// wrong would be worse than the XML.</para>
/// </summary>
internal static partial class CliXml
{
    /// <summary>The header a minishell puts in front of a serialised stream.</summary>
    private const string Header = "#< CLIXML";

    /// <summary>Streams worth reading back. Progress is deliberately not among them.</summary>
    private static readonly HashSet<string> Readable =
        new(StringComparer.Ordinal) { "Error", "Warning", "Verbose", "Debug", "Information" };

    public static bool Looks(string text)
        => text.AsSpan().TrimStart().StartsWith(Header, StringComparison.Ordinal);

    /// <summary>
    /// The readable text inside a CLIXML blob, or the input unchanged when it is not one.
    ///
    /// <para>Empty when the blob carried only progress records - see the class summary.</para>
    /// </summary>
    public static string ToText(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !Looks(raw))
            return raw;

        var start = raw.IndexOf('<', raw.IndexOf(Header, StringComparison.Ordinal) + Header.Length);
        if (start < 0)
            return raw;

        try
        {
            var document = XDocument.Parse(raw[start..]);
            var ns = document.Root?.GetDefaultNamespace() ?? XNamespace.None;

            var text = new StringBuilder();
            foreach (var element in document.Descendants(ns + "S"))
            {
                if (element.Attribute("S")?.Value is { } stream && Readable.Contains(stream))
                    text.Append(Unescape(element.Value));
            }

            return text.ToString().TrimEnd();
        }
        catch (System.Xml.XmlException)
        {
            // A truncated blob - the capture ceiling cut it, most likely. The raw text is still the
            // best thing anybody has.
            return raw;
        }
    }

    /// <summary>
    /// <c>_x000D__x000A_</c> back into a newline, and every other <c>_xHHHH_</c> escape back into its
    /// character. XML entities are already decoded by the parser.
    /// </summary>
    private static string Unescape(string value)
        => Escape().Replace(value, match =>
            ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());

    [GeneratedRegex("_x([0-9A-Fa-f]{4})_")]
    private static partial Regex Escape();
}
