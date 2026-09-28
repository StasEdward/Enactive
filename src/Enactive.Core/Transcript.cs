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

    // What a tool call is remembered as: exactly what the model sent. It was shortened at record
    // time from 2026-09-22 to 2026-09-24, and every form of that misled the model about its own work
    // - see the comment where the orchestrator records a reply. Only Elide, under window pressure,
    // rewrites calls, and never the newest ones.

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
    public const double DefaultCharsPerToken = 3.0;

    private double _charsPerToken = DefaultCharsPerToken;

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
