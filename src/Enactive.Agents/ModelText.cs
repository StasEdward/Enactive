namespace Enactive.Agents;

using System.Text.Json;

/// <summary>
/// Reading an answer a model wrote as text, for the two places in the engine that still have to:
/// the planner and the reviewer.
///
/// <para>Everywhere else the engine reads VALUES — tool calls come back through the provider's own
/// tool-calling, criteria are decided by exit codes, events carry typed payloads. These two remain
/// because a plan and a verdict arrive as content, and both used to be pulled out with "from the
/// first <c>{</c> to the last <c>}</c>", duplicated in both files.</para>
///
/// <para>That scrape has two ways of being confidently wrong, and both are fixed here rather than
/// twice:</para>
///
/// <para><b>Unfinished reasoning read as an answer.</b> The old <c>StripThink</c> removed a
/// <c>&lt;think&gt;…&lt;/think&gt;</c> pair and did nothing at all when the closing tag was absent.
/// A reasoning model whose thinking is cut short — by <c>num_ctx</c>, by max tokens, by anything —
/// leaves the tag open, and the scrape then read JSON out of the model's own deliberation: an
/// abandoned draft, complete with whatever the model was considering and rejecting. Unfinished
/// reasoning is not an answer, so it is now discarded and the caller is told there was no
/// answer.</para>
///
/// <para><b>Prose that contains a brace.</b> First-to-last spans everything between them, so one
/// stray <c>{</c> in a sentence before the object, or a second object after it, produced text that
/// is not JSON at all. Scanning for a BALANCED object and trying each candidate start means the
/// first thing in the answer that is actually an object wins, and prose around it costs nothing.</para>
/// </summary>
internal static class ModelText
{
    /// <summary>
    /// The answer with the model's reasoning removed. An unclosed <c>&lt;think&gt;</c> takes
    /// everything after it: the reasoning never ended, so nothing after it is an answer.
    /// </summary>
    public static string StripThink(string text)
    {
        const string open = "<think>";
        const string close = "</think>";

        while (true)
        {
            var start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return text;

            var end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                return text[..start];

            text = text.Remove(start, end + close.Length - start);
        }
    }

    /// <summary>
    /// The first JSON object in the text, or null when there is none. Quotes and escapes are
    /// respected, so a brace inside a string does not end the object.
    ///
    /// <para>Each balanced candidate is PARSED before it is returned, because balanced is not the
    /// same as valid: prose like "I used the shape {like this}" is a balanced span and not JSON, and
    /// returning it would throw away the real object further along the same sentence. The first
    /// candidate that parses is the answer.</para>
    /// </summary>
    public static string? ExtractJsonObject(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{' || Balanced(text, i) is not { } candidate)
                continue;

            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    return candidate;
            }
            catch (JsonException)
            {
                // Balanced but not JSON. Keep looking - the real object may be further along.
            }
        }

        return null;
    }

    private static string? Balanced(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    if (--depth == 0)
                        return text[start..(i + 1)];
                    break;
            }
        }

        // Opened and never closed: an answer that was cut off mid-object is not an object.
        return null;
    }
}
