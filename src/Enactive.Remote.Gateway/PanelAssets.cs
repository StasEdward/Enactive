namespace Enactive.Remote.Gateway;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// The panel's page, with every stylesheet and script it names carrying a fingerprint of its own
/// contents.
///
/// <para><b>Why.</b> The files were served with <c>Cache-Control: no-cache</c>, which asks a client
/// to revalidate and trusts it to do so. Most do. A phone does not always: a tab restored from
/// iOS Safari's back-forward cache is the page as it was, scripts included, and no header on a
/// request that never happens can say otherwise. A release reached the server, the desktop showed
/// it, and the same URL on a phone went on drawing the previous build - which from the outside is
/// indistinguishable from the release having failed.</para>
///
/// <para><b>What changes.</b> <c>/app.js</c> becomes <c>/app.js?v=1a2b3c4d</c>, where the token is
/// the first bytes of the file's SHA-256. Edit the file and the URL is a different URL, which no
/// cache anywhere - browser, CDN or proxy - can answer with what it kept for the old one. Nothing
/// has to be trusted to revalidate, because nothing is being asked to.</para>
///
/// <para>The page itself is still <c>no-cache</c>, and has to be: it is the pointer to everything
/// else, and a cached pointer names cached files. That is one revalidation of a few kilobytes per
/// load, which is the whole cost.</para>
///
/// <para><b>What this does not cover.</b> Files referenced from inside a stylesheet rather than
/// from the page - the web fonts named by <c>fonts.css</c> - keep their plain URLs and their
/// revalidation. Rewriting those means parsing CSS, and a stale font is a different kind of wrong
/// from a stale script: it looks wrong rather than behaves wrong, and it cannot hide a release.
/// When a font changes it changes under a new name anyway.</para>
///
/// <para><b>The published list.</b> The same load also hashes every script and stylesheet in the
/// web root, for <c>/.well-known/enactive-panel.json</c>. See <see cref="Manifest"/>.</para>
/// </summary>
public sealed class PanelAssets
{
    /// <summary>Local stylesheets and scripts the page names, and nothing else.</summary>
    private static readonly Regex Referenced = new(
        "(?<attribute>href|src)=\"(?<path>/[^\"?#]+\\.(?:css|js))\"",
        RegexOptions.Compiled);

    /// <summary>
    /// What the published list covers: the code a browser runs and the styles it applies. Images and
    /// fonts are left out - they cannot read a key - and so are the HTML pages: the panel's page is
    /// rewritten with fingerprints that name these files, and the policy pages run no script.
    /// </summary>
    private static readonly string[] Listed = [".js", ".mjs", ".css"];

    private PanelAssets(string page, IReadOnlyDictionary<string, string> files)
    {
        Page = page;
        Files = files;
        Manifest = JsonSerializer.Serialize(
            new { algorithm = "sha256", files },
            new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The page as it is served: identical to index.html but for the fingerprints.</summary>
    public string Page { get; }

    /// <summary>
    /// Every script and stylesheet in the web root, by the path it is served at, with the full SHA-256
    /// of its contents in lower-case hex. Ordered by path, so the same release always produces the same
    /// list, byte for byte.
    /// </summary>
    public IReadOnlyDictionary<string, string> Files { get; }

    /// <summary>
    /// <see cref="Files"/> as the JSON served at <c>/.well-known/enactive-panel.json</c>.
    ///
    /// <para><b>Why.</b> The panel holds the keys that open a person's content, and it is code this
    /// server sends. An operator - or whoever has taken the server - who sends altered code can read
    /// those keys, and nothing inside a web page can prevent it. What can be done is to make the
    /// alteration visible: the hashes of what is served, published where anyone can fetch them and
    /// compare with the hashes a release lists.</para>
    ///
    /// <para><b>Every file, not the ones the page names.</b> The page names <c>app.js</c> and the
    /// stylesheets; the modules in <c>js/</c> and <c>vendor/</c> are reached by <c>import</c>. A list
    /// of what the page names would leave out most of the code that handles keys.</para>
    /// </summary>
    public string Manifest { get; }

    /// <summary>
    /// Reads index.html from the web root and fingerprints what it references.
    ///
    /// <para>Done once at startup rather than per request. These files change when the process is
    /// replaced, and a hash recomputed on every page load would be work spent proving that nothing
    /// had happened.</para>
    /// </summary>
    /// <param name="webRoot">The wwwroot folder.</param>
    public static PanelAssets Load(string webRoot)
    {
        var index = Path.Combine(webRoot, "index.html");

        // A gateway that cannot find its own page is broken in a way that should stop it, not
        // serve an empty panel that reads as a problem somewhere else.
        if (!File.Exists(index))
        {
            throw new InvalidOperationException(
                $"The panel's index.html was not found at {index}. This build is incomplete.");
        }

        var page = Referenced.Replace(File.ReadAllText(index), match =>
        {
            var path = match.Groups["path"].Value;
            var file = Path.Combine(webRoot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

            // A reference to something not shipped is left exactly as it was. Inventing a
            // fingerprint for a file that is not there would turn a 404 into a 404 at a stranger
            // address, which is harder to read and no more correct.
            return File.Exists(file)
                ? $"{match.Groups["attribute"].Value}=\"{path}?v={Fingerprint(file)}\""
                : match.Value;
        });

        return new PanelAssets(page, Hashes(webRoot));
    }

    /// <summary>The full SHA-256 of every listed file under the web root, keyed by its served path.</summary>
    private static SortedDictionary<string, string> Hashes(string webRoot)
    {
        // Ordinal, so the order does not depend on the culture of the machine the gateway runs on: two
        // servers of the same release must publish the same bytes, or comparing them means nothing.
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories))
        {
            if (!Listed.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = File.OpenRead(file);
            var path = "/" + Path.GetRelativePath(webRoot, file).Replace(Path.DirectorySeparatorChar, '/');

            hashes[path] = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        return hashes;
    }

    /// <summary>
    /// Eight hex characters of the file's SHA-256.
    ///
    /// <para>Of the CONTENTS, not the modification time. A file written again with the same bytes
    /// by a redeploy is the same file and should keep the URL a client already holds - and two
    /// files that differ anywhere differ here.</para>
    /// </summary>
    public static string Fingerprint(string file)
    {
        using var stream = File.OpenRead(file);

        return Convert.ToHexStringLower(SHA256.HashData(stream))[..8];
    }
}
