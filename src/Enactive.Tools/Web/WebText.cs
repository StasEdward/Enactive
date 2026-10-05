namespace Enactive.Tools.Web;

using System.Net;
using System.Text.RegularExpressions;

/// <summary>
/// A web page as text: its title, and what a reader sees of it - without markup, scripts, styles or comments.
///
/// <para>Read raw, a page is mostly what nobody reads: a documentation page of 6 KB of text comes as 150 KB of
/// HTML, scripts and styles, and a model given it spends its window on markup. Scripts are also where a page puts
/// text it does not show - which is not what the page says to a reader, and is left out for that reason too.</para>
///
/// <para>Done with patterns, not a parser: a page is read for its words, not rendered, and a parser would be a new
/// dependency for a result a reader cannot tell apart. What it gets wrong it gets wrong toward keeping text.</para>
/// </summary>
public static partial class WebText
{
    public static (string? Title, string Text) FromHtml(string html)
    {
        var title = TitleTag().Match(html) is { Success: true } t ? Clean(Decode(t.Groups[1].Value)) : null;

        var text = Comments().Replace(html, " ");
        text = Hidden().Replace(text, " ");
        text = Blocks().Replace(text, "\n");
        text = Cells().Replace(text, " ");
        text = Tags().Replace(text, "");
        text = Decode(text);

        var lines = text.Split('\n').Select(Clean).Where(line => line.Length > 0);
        return (string.IsNullOrEmpty(title) ? null : title, string.Join("\n", lines));
    }

    private static string Decode(string text) => WebUtility.HtmlDecode(text).Replace(' ', ' ');

    private static string Clean(string line) => Spaces().Replace(line, " ").Trim();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    /// <summary>What a browser does not show as text: the head, scripts, styles, and the like.</summary>
    [GeneratedRegex(@"<(head|script|style|noscript|template|svg|iframe)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Hidden();

    /// <summary>Tags that start a line of their own when a page is read.</summary>
    [GeneratedRegex(@"</?(p|div|br|li|ul|ol|h[1-6]|tr|table|section|article|header|footer|nav|main|aside|pre|blockquote|dd|dt|dl|hr|form|figure|figcaption)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Blocks();

    [GeneratedRegex(@"</?(td|th)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Cells();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Spaces();
}
