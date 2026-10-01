namespace Enactive.Remote.Contracts;

using System.Globalization;
using System.Text;

/// <summary>
/// The one way protocol 2 turns a list of fields into bytes - for associated data, MAC input and
/// HKDF info alike. Each field is preceded by its UTF-8 byte count, so no value can be arranged to
/// read as a different list of fields (the reason ActionIdentity was built this way first). The JS
/// twin is wwwroot/js/canonical.js; the shared vectors pin the two together.
/// </summary>
public static class Canonical
{
    public static string Text(string version, params string?[] fields)
    {
        var text = new StringBuilder();
        // The version is a fixed protocol constant, never from data, so it is not length-prefixed.
        text.Append(version).Append('\n');
        foreach (var field in fields)
        {
            var value = field ?? "";
            // The byte count is UTF-8 bytes, not characters, because the hash and MAC are computed over bytes, which differ from characters for non-ASCII text.
            text.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(value).Append('\n');
        }
        return text.ToString();
    }

    public static byte[] Bytes(string version, params string?[] fields) => Encoding.UTF8.GetBytes(Text(version, fields));
}
