namespace Enactive.Tools;

/// <summary>
/// One rule about line endings, in one place, because two tools need it and they must not disagree.
///
/// <para>It lived in <see cref="EditFileTool"/> until 2026-09-08, when <c>write_file</c> was made to
/// follow it too (<c>FIX_PLAN.md</c> §9b): a whole-file rewrite converted a CRLF document to LF
/// silently, which is the same defect one level up from the one this was written for.</para>
/// </summary>
internal static class LineEndings
{
    /// <summary>
    /// The same passage, written with the line endings the FILE uses — or null when that would
    /// change nothing.
    ///
    /// <para>2026-09-07: three edit_file calls in a row were refused with "'old_string' does not
    /// appear in web-site/index.html", and the passage the model sent was character-for-character
    /// the right one. The file had CRLF endings on all 413 lines; the model sent 210 characters
    /// containing five LFs and no CR. It had copied the passage exactly, as far as it could: a
    /// carriage return is invisible in the text read_file returns, and a model cannot reproduce a
    /// character it cannot see. The advice in the refusal — "copy the passage exactly, including
    /// line breaks" — was advice it had already followed and could never satisfy.</para>
    ///
    /// <para>That made the tool unusable on Windows, where CRLF is the normal state of a text file.
    /// It went unnoticed because <c>edit_file</c> was in no worker's tool list, so nothing had ever
    /// called it on a real file: two defects each hiding the other.</para>
    ///
    /// <para>Retyping the REPLACEMENT the same way matters as much as matching. Splicing an LF
    /// passage into a CRLF file leaves it with mixed endings — a diff that shows the whole block
    /// rewritten, and a repository with autocrlf churning on it — for an edit that was supposed to
    /// touch one line.</para>
    /// </summary>
    internal static string? RetypedFor(string file, string passage)
    {
        if (passage.Length == 0)
            return null;

        var fileHasCrLf = file.Contains("\r\n", StringComparison.Ordinal);
        var passageHasCr = passage.Contains('\r');

        // Normalising first means a passage that is itself mixed comes out consistent, rather than
        // half-converted by a naive replace.
        if (fileHasCrLf && !passageHasCr)
            return passage.Replace("\n", "\r\n", StringComparison.Ordinal);

        if (!fileHasCrLf && passageHasCr)
            return passage.Replace("\r\n", "\n", StringComparison.Ordinal);

        return null;
    }
}
