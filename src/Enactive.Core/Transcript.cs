namespace Enactive.Core.Chat;

using System.Text.Json;
using Enactive.Core.Tools;

/// <summary>
/// Keeping a tool-loop conversation inside a model's context window.
///
/// <para>An agent transcript grows in a way ordinary chat does not: a file travels through it twice
/// at full length — once as the argument of the <c>write_file</c> that produced it, once as the
/// result of the <c>read_file</c> that checked it — and every command's whole stdout is appended
/// verbatim. On 2026-09-06 a local model with <c>num_ctx=8192</c> reached 8174 prompt tokens on a
/// three-file task and was cut off mid-argument (<c>"cd TicTacToe &amp;&amp;</c>), which the engine
/// could only report as "cut off at the token limit". Nothing was wrong with the model or the plan;
/// the transcript had simply eaten the window.</para>
///
/// <para>Old tool traffic is where that weight sits and it is also the most disposable part of the
/// conversation: what a file said six edits ago is rarely load-bearing, while the last two exchanges
/// almost always are. So this elides CONTENT and never removes a message — a tool result separated
/// from its call, or a call from its result, is a malformed conversation that providers reject
/// outright, which would turn a recoverable squeeze into a hard failure.</para>
/// </summary>
public static class Transcript
{
    /// <summary>What is left where a tool call's arguments were. Valid JSON, because it is still sent as arguments.</summary>
    public const string ElidedArguments = """{"_elided":"arguments dropped to fit the context window"}""";

    // ── what a tool call is REMEMBERED as ─────────────────────────────────────────────

    /// <summary>A string argument longer than this is remembered by its head and its size.</summary>
    /// <remarks>
    /// Measured on the run of 2026-09-22 (21.7M prompt tokens over 197 turns): of the largest
    /// prompt's 601,560 characters, 177,608 were the ARGUMENTS of tool calls — 93,588 of them
    /// <c>edit_file</c>'s <c>new_string</c> across 49 calls. Every one of those was re-sent on
    /// every turn after it, and each had already been applied to a file on disk.
    /// </remarks>
    private const int MaxRememberedValueChars = 600;

    /// <summary>
    /// The longest first line quoted back, so the model can still recognise its own work. Longer
    /// than this and none of it is quoted - see <see cref="ShortenArguments"/> for why a cut one
    /// is worse than none.
    /// </summary>
    private const int MaxFirstLineChars = 120;

    /// <summary>
    /// The form a set of tool calls is kept in the transcript — as opposed to the form that is
    /// INVOKED, which is always the model's own text, untouched.
    ///
    /// <para><b>Why this is not <see cref="Elide"/>.</b> Elide is a rescue: it rewrites history
    /// when the window is nearly full, and rewriting history is expensive in a way that is easy to
    /// miss — a provider's prompt cache keys on the PREFIX, so changing an old message invalidates
    /// the cache for everything after it. Measured: capping the window turned 21.7M prompt tokens
    /// into 7.8M and the cached share from 98% into 59%, which is most of the saving given back.
    /// This shortens a message ONCE, at the moment it is recorded, and never touches it again — so
    /// the prefix is stable from the first turn and the cache never notices.</para>
    ///
    /// <para><b>Why it is safe to forget.</b> An argument this size is a file's contents on its way
    /// to disk. By the time this runs the call has been made, the tool result says what happened,
    /// and the file can be read back — which is what a model does anyway when it wants to be sure.
    /// The exact size is kept because it is the one number a model cannot re-derive.</para>
    ///
    /// <para><b>What it is replaced WITH matters as much.</b> Until 2026-09-24 the value became its
    /// first 200 characters and "… (14,188 characters, sent in full; read the file back if you need
    /// the rest)". A model looking back at its own call saw its report stop mid-word - "## Page:
    /// O…" - said "The file was truncated. Let me write it in parts", and wrote the whole file
    /// again. The note in brackets said the opposite, and lost to the text it was attached to: a
    /// document that visibly stops is read as a document that was cut. So the value is now a note
    /// and nothing else - bracketed, saying the content was written in full - with the first line
    /// quoted only when it is short enough to quote whole.</para>
    ///
    /// <para>Anything it cannot parse is returned untouched. A call whose arguments this does not
    /// understand is a call it has no business editing.</para>
    /// </summary>
    public static IReadOnlyList<ToolCall>? ForHistory(IReadOnlyList<ToolCall>? calls)
    {
        if (calls is not { Count: > 0 })
            return calls;

        ToolCall[]? shortened = null;

        for (var i = 0; i < calls.Count; i++)
        {
            var remembered = ShortenArguments(calls[i].ArgumentsJson);
            if (ReferenceEquals(remembered, calls[i].ArgumentsJson))
                continue;

            shortened ??= calls.ToArray();
            shortened[i] = calls[i] with { ArgumentsJson = remembered };
        }

        return shortened ?? calls;
    }

    /// <summary>
    /// One call's arguments, with every oversized string value replaced by its head and its length.
    /// Returns the SAME instance when nothing needed shortening, so callers can tell.
    /// </summary>
    public static string ShortenArguments(string argumentsJson)
    {
        // The overwhelming majority of calls are a path and a pattern. Parsing those would be work
        // done to discover there is nothing to do.
        if (string.IsNullOrEmpty(argumentsJson) || argumentsJson.Length <= MaxRememberedValueChars)
            return argumentsJson;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return argumentsJson;

            var shortened = new Dictionary<string, JsonElement>();
            var changed = false;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { } text
                    && text.Length > MaxRememberedValueChars)
                {
                    shortened[property.Name] = JsonSerializer.SerializeToElement(Remembered(text));
                    changed = true;
                }
                else
                {
                    shortened[property.Name] = property.Value.Clone();
                }
            }

            return changed ? JsonSerializer.Serialize(shortened) : argumentsJson;
        }
        catch (JsonException)
        {
            return argumentsJson;
        }
    }

    /// <summary>
    /// What a long value is remembered as: a note, not a fragment. See <see cref="ShortenArguments"/>.
    /// </summary>
    private static string Remembered(string text)
    {
        var end = text.IndexOf('\n');
        var first = (end < 0 ? text : text[..end]).TrimEnd('\r').Trim();

        var began = first.Length is > 0 and <= MaxFirstLineChars
            ? $" It began with the line: \"{first}\"."
            : "";

        return $"[Not repeated here: this call sent all {text.Length:N0} characters, and they were "
             + $"used in full - nothing was cut.{began} Read the file if you need the content.]";
    }

    /// <summary>
    /// Roughly how big this conversation is, in characters. Characters rather than tokens because
    /// Core has no tokenizer and every provider's differs; the caller converts with a ratio measured
    /// against what the provider actually reported (see <see cref="Enactive.Core.Chat.TokenScale"/>).
    /// </summary>
    public static int Size(IReadOnlyList<ChatMessage> messages)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += message.Content?.Length ?? 0;
            if (message.ToolCalls is { } calls)
                foreach (var call in calls)
                    total += call.Name.Length + (call.ArgumentsJson?.Length ?? 0);
            // Roles, ids and message framing are not free on the wire either.
            total += 24;
        }
        return total;
    }

    /// <summary>
    /// Elides tool exchanges until <see cref="Size"/> is at or under <paramref name="targetChars"/>,
    /// and returns how many messages were changed (0 = there was nothing left to give).
    ///
    /// <para>Oldest first, and the newest <paramref name="keepRecent"/> exchanges are spared while
    /// anything older still has content to drop — the model is usually mid-repair on those, and
    /// taking them costs the step its working memory. But sparing them is a preference, not a
    /// guarantee: if the transcript still does not fit, they go too, newest last. A step that loses
    /// a tool result can read the file again; a step that will not send its request at all is over.
    /// One oversized write is exactly this case, and protecting it would kill the run to save the
    /// contents of a file already on disk.</para>
    ///
    /// <para>The list is edited in place and keeps its length. A message already elided is not
    /// counted again.</para>
    /// </summary>
    public static int Elide(List<ChatMessage> messages, int targetChars, int keepRecent = 2)
    {
        if (messages.Count == 0 || Size(messages) <= targetChars)
            return 0;

        // An exchange is one assistant turn that asked for tools plus the results that answered it.
        // Grouping them keeps the elision from cutting between a call and its result, which reads to
        // the model as a call that was never answered.
        var exchanges = new List<List<int>>();
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role == ChatRole.Assistant && message.ToolCalls is { Count: > 0 })
                exchanges.Add(new List<int> { i });
            else if (message.Role == ChatRole.Tool && exchanges.Count > 0)
                exchanges[^1].Add(i);
        }

        var spared = Math.Clamp(keepRecent, 0, exchanges.Count);
        var changed = 0;

        // Pass one: everything older than the spared tail.
        for (var e = 0; e < exchanges.Count - spared && Size(messages) > targetChars; e++)
            changed += ElideExchange(messages, exchanges[e]);

        // Pass two: it still does not fit, so the tail goes as well - oldest of it first.
        for (var e = exchanges.Count - spared; e < exchanges.Count && Size(messages) > targetChars; e++)
            changed += ElideExchange(messages, exchanges[e]);

        return changed;
    }

    private static int ElideExchange(List<ChatMessage> messages, List<int> exchange)
    {
        var changed = 0;
        foreach (var index in exchange)
            if (ElideOne(messages, index))
                changed++;
        return changed;
    }

    /// <summary>Replaces one message's payload with a note saying what used to be there.</summary>
    private static bool ElideOne(List<ChatMessage> messages, int index)
    {
        var message = messages[index];

        if (message.Role == ChatRole.Tool)
        {
            var length = message.Content?.Length ?? 0;
            if (length == 0 || message.Content!.StartsWith(ElidedResultMarker, StringComparison.Ordinal))
                return false;

            messages[index] = message with
            {
                Content = $"{ElidedResultMarker} {length} characters, dropped to fit the context window]"
            };
            return true;
        }

        if (message.Role == ChatRole.Assistant && message.ToolCalls is { Count: > 0 } calls)
        {
            if (calls.All(c => c.ArgumentsJson == ElidedArguments))
                return false;

            messages[index] = message with
            {
                ToolCalls = calls.Select(c => c with { ArgumentsJson = ElidedArguments }).ToArray()
            };
            return true;
        }

        return false;
    }

    private const string ElidedResultMarker = "[earlier tool result:";
}

/// <summary>
/// How many characters of transcript one token of this model's prompt turned out to be.
///
/// <para>Measured, not assumed: the ratio differs by model and by content — code and JSON tokenize
/// far worse than prose — and a guess that is off by a third is the difference between trimming a
/// conversation that had room and being cut off mid-call in one that did not. It starts at a
/// deliberately pessimistic value and is replaced by the real one as soon as a turn reports its
/// prompt size.</para>
/// </summary>
public sealed class TokenScale
{
    // Pessimistic on purpose: assuming FEWER characters per token over-estimates the transcript, and
    // over-estimating costs a little window, while under-estimating costs the step.
    private double _charsPerToken = 3.0;

    /// <summary>Records what a request of <paramref name="chars"/> characters actually cost.</summary>
    public void Observe(int chars, int promptTokens)
    {
        if (chars <= 0 || promptTokens <= 0)
            return;

        // Clamped because one anomalous turn - a cached prompt, a provider that reports totals
        // differently - must not be able to convince the estimate that a token is 40 characters.
        _charsPerToken = Math.Clamp(chars / (double)promptTokens, 1.5, 8.0);
    }

    /// <summary>About how many prompt tokens a transcript of this size will cost.</summary>
    public int TokensFor(int chars) => (int)Math.Ceiling(chars / _charsPerToken);

    /// <summary>About how many characters fit in this many tokens.</summary>
    public int CharsFor(int tokens) => (int)(tokens * _charsPerToken);
}
