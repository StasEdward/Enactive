namespace Enactive.Providers;

using System.Runtime.CompilerServices;
using System.Text;
using Enactive.Core.Chat;
using Enactive.Core.Diagnostics;
using Enactive.Core.Providers;

/// <summary>
/// A provider-agnostic decorator that produces the READABLE plane of the log: a rendered view of the
/// prompt going to the model (system + every message + the offered tools) and, once the turn is done,
/// a single assembled entry with the full assistant text, tool calls, finish reason and usage. It
/// wraps any <see cref="IChatProvider"/> and changes nothing about the traffic — the raw byte-level
/// dump is done separately inside each provider (<see cref="WireTap"/>). Together they give a clean,
/// always-on summary plus an on-demand raw trace, correlated to the run via <see cref="LogScope"/>.
/// </summary>
public sealed class LoggingChatProvider : IChatProvider
{
    /// <summary>
    /// What stands in for a prompt this log must not contain — see <see cref="LoggingChatProvider(IChatProvider, ILogSink, string, bool)"/>.
    /// </summary>
    internal const string BodyWithheld =
        "(the prompt is not shown here: it carries this log's own contents, and writing it "
        + "into the log would fold the log into itself. The call above did happen.)";

    private readonly IChatProvider _inner;
    private readonly ILogSink _log;
    private readonly string _providerId;
    private readonly bool _promptBodies;

    /// <param name="promptBodies">
    /// Whether the rendered PROMPT is written to the log. False for a call whose prompt is the log
    /// itself — the "AI Analyze" button on the log window.
    ///
    /// <para>Found 2026-09-07 19:54: a run of 10,429 lines, analysed once, became 15,214 — the
    /// analysis prompt carries a 4,700-line excerpt of the log and was written straight back into
    /// it. Analysed twice, the file was 19,664 lines, 47% of it previous analysis prompts, and the
    /// excerpt the model was given had shrunk from 4,740 lines of the run to 4,427 lines of mostly
    /// itself. The feature was eating the record it exists to read.</para>
    ///
    /// <para>The line still goes in — the same rule as everywhere else here: elide content, never
    /// remove the record that something happened. So does the model's ANSWER, which is short and is
    /// the useful part.</para>
    /// </param>
    public LoggingChatProvider(IChatProvider inner, ILogSink log, string providerId, bool promptBodies = true)
    {
        _inner = inner;
        _log = log;
        _providerId = providerId;
        _promptBodies = promptBodies;
    }

    /// <summary>Whatever the real provider says — a decorator that answered for it would be guessing.</summary>
    public int? ContextWindow(ChatRequest request) => _inner.ContextWindow(request);

    public int? AnswerReserve(ChatRequest request) => _inner.AnswerReserve(request);

    public int? HandoverAtPercent(ChatRequest request) => _inner.HandoverAtPercent(request);

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        _log.Info(LogSource.Prompt,
            $"prompt → {_providerId}/{request.Model} ({request.Messages.Count} messages)",
            Body(request), request.Model);

        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
        string? finish = null;
        int? promptTokens = null, completionTokens = null, cachedTokens = null;

        // What the stream threw, if it threw.
        //
        // This used to be a `faulted` flag that nothing ever set, read by the `finally` below to
        // choose Warn over Info — a guard that enforced nothing while looking configured. The
        // reason it was never set is a language rule: a `yield return` cannot live inside a `try`
        // that CATCHES, so the obvious catch around the loop does not compile, and the flag was
        // left behind when it was removed.
        //
        // The cost, found in a log Stas sent on 2026-09-11 at 16:03 after starting a run with
        // Ollama switched off: the call threw, and the log said
        // "INF  response ← ollama/gemma4:31b-cloud (0 chars, 0 tool call(s))" — informational, and
        // indistinguishable from a model that legitimately said nothing. The exception's message
        // never reached the log at all from here. The non-streaming path below has logged it
        // correctly all along, which is what made the gap invisible: the two halves of one
        // decorator disagreed, and the half in use was the quiet one.
        //
        // The fix is to enumerate by hand so the MOVE is inside a catch and the `yield return`
        // stays outside it.
        Exception? failure = null;

        try
        {
            await using var events = _inner.StreamChatAsync(request, ct).GetAsyncEnumerator(ct);

            while (true)
            {
                ChatStreamEvent evt;
                try
                {
                    if (!await events.MoveNextAsync())
                        break;
                    evt = events.Current;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    throw;
                }

                switch (evt)
                {
                    case TextDelta t:
                        text.Append(t.Text);
                        break;
                    case ToolCallDelta d:
                        if (!calls.TryGetValue(d.Index, out var acc))
                            calls[d.Index] = acc = (d.Id, d.Name, new StringBuilder());
                        if (d.Id is not null || d.Name is not null)
                            calls[d.Index] = (d.Id ?? acc.Id, d.Name ?? acc.Name, acc.Args);
                        if (d.ArgumentsJson is { Length: > 0 } part)
                            acc.Args.Append(part);
                        break;
                    // Logged because a turn made ENTIRELY of reasoning is otherwise a blank line in
                    // the log with a token count beside it, and no way to see what the model was
                    // doing with them.
                    case ReasoningDelta r:
                        reasoning.Append(r.Text);
                        break;
                    case UsageDelta u:
                        promptTokens = u.PromptTokens ?? promptTokens;
                        completionTokens = u.CompletionTokens ?? completionTokens;
                        cachedTokens = u.CachedPromptTokens ?? cachedTokens;
                        break;
                    case FinishDelta f:
                        finish = f.Reason;
                        break;
                }

                yield return evt;
            }
        }
        finally
        {
            // A call that threw is an ERROR line carrying the provider's own words, matching the
            // non-streaming path exactly — the divergence between the two is what hid this.
            // Whatever was received before the throw still goes in the body: a stream that failed
            // half way is more legible with its half than without it.
            if (failure is { } thrown)
                _log.Error(LogSource.Llm,
                    $"response ← {_providerId}/{request.Model} FAILED: {thrown.Message}",
                    RenderResponse(text.ToString(), calls, finish, promptTokens, completionTokens,
                                   cachedTokens, reasoning.ToString())
                        + Environment.NewLine + new string('-', 40) + Environment.NewLine
                        + thrown,
                    request.Model);
            else
                _log.Write(LogLevel.Info, LogSource.Llm,
                    $"response ← {_providerId}/{request.Model}"
                        + $" ({text.Length} chars, {calls.Count} tool call(s)"
                        + (reasoning.Length > 0 ? $", {reasoning.Length} chars of reasoning" : "")
                        + (finish is null ? "" : $", finish={finish}")
                        + (completionTokens is { } c ? $", {c} out-tokens" : "") + ")",
                    RenderResponse(text.ToString(), calls, finish, promptTokens, completionTokens,
                                   cachedTokens, reasoning.ToString()),
                    request.Model);
        }
    }

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        _log.Info(LogSource.Prompt,
            $"prompt → {_providerId}/{request.Model} ({request.Messages.Count} messages, non-stream)",
            Body(request), request.Model);

        ChatCompletion completion;
        try
        {
            completion = await _inner.CompleteAsync(request, ct);
        }
        catch (Exception ex)
        {
            _log.Error(LogSource.Llm, $"response ← {_providerId}/{request.Model} FAILED: {ex.Message}", ex.ToString(), request.Model);
            throw;
        }

        var msg = completion.Message;
        _log.Info(LogSource.Llm,
            $"response ← {_providerId}/{request.Model}"
                + $" ({(msg.Content?.Length ?? 0)} chars, {(msg.ToolCalls?.Count ?? 0)} tool call(s)"
                + (completion.Thinking is { Length: > 0 } th ? $", {th.Length} chars of reasoning" : "")
                + (completion.FinishReason is { } fr ? $", finish={fr}" : "") + ")",
            RenderCompletion(completion), request.Model);
        return completion;
    }

    /// <summary>
    /// The prompt as the log should carry it: rendered, or — for a call whose prompt IS the log —
    /// the settings line and a note in place of the messages. Never nothing: an entry with no body
    /// at all reads as a call that produced no prompt.
    /// </summary>
    private string Body(ChatRequest request)
        => _promptBodies ? RenderPrompt(request, AlreadyLogged(request)) : Settings(request) + BodyWithheld;

    private readonly object _promptGate = new();
    private ChatMessage[]? _lastPrompt;

    /// <summary>
    /// How many of this request's messages are EXACTLY the previous prompt's, from the start - the
    /// same objects, in the same places. Those were written to the log last time and are not written
    /// again.
    ///
    /// <para>Measured 2026-09-24: a prompt of 171 messages and 499,595 characters rendered 506,334
    /// characters into the log, and the next turn's prompt was the same 171 messages plus two. About
    /// 150 MB of log per run, almost all of it repeated; the day's log was 695 MB. A conversation
    /// only ever appends, so the log only needs what was appended.</para>
    ///
    /// <para>By REFERENCE, not by content: a trim or a handover builds new message objects, and then
    /// the prompt really is different from the last one and is written whole - which is exactly when
    /// somebody reading the log needs to see it whole.</para>
    /// </summary>
    private int AlreadyLogged(ChatRequest request)
    {
        lock (_promptGate)
        {
            var last = _lastPrompt;
            var now = request.Messages;
            _lastPrompt = now.ToArray();

            if (last is null || now.Count < last.Length)
                return 0;

            for (var i = 0; i < last.Length; i++)
                if (!ReferenceEquals(now[i], last[i]))
                    return 0;

            return last.Length;
        }
    }

    /// <summary>How the call was made — model, sampling, window, tools. Cheap, and true of every
    /// call whether or not its messages can be shown.</summary>
    private static string Settings(ChatRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("model=").Append(request.Model);
        if (request.Temperature is { } t) sb.Append("  temperature=").Append(t);
        if (request.NumCtx is { } n) sb.Append("  num_ctx=").Append(n);
        if (request.MaxTokens is { } mx) sb.Append("  max_tokens=").Append(mx);
        sb.AppendLine();
        if (request.Tools is { Count: > 0 } tools)
            sb.Append("tools: ").AppendLine(string.Join(", ", tools.Select(x => x.Name)));
        return sb.ToString();
    }

    private static string RenderPrompt(ChatRequest request, int alreadyLogged = 0)
    {
        var sb = new StringBuilder(Settings(request));
        sb.AppendLine(new string('-', 40));

        if (alreadyLogged > 0)
            sb.Append("(messages 1-").Append(alreadyLogged)
              .AppendLine(" are exactly as in the previous prompt to this provider, and are not repeated here)")
              .AppendLine();

        foreach (var m in request.Messages.Skip(alreadyLogged))
        {
            sb.Append("### ").AppendLine(m.Role.ToString().ToUpperInvariant());
            if (!string.IsNullOrEmpty(m.Content))
                sb.AppendLine(m.Content);
            if (m.ToolCalls is { Count: > 0 } mcalls)
                foreach (var c in mcalls)
                    sb.Append("  [tool_call ").Append(c.Name).Append("] ").AppendLine(c.ArgumentsJson);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderResponse(
        string text, SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)> calls,
        string? finish, int? promptTokens, int? completionTokens, int? cachedTokens,
        string? reasoning = null)
    {
        var sb = new StringBuilder();
        if (text.Length > 0) sb.AppendLine(text);
        // Marked as what it is. A reasoning block read as an answer is how a draft - including what
        // the model was considering and rejecting - gets taken for a conclusion.
        if (reasoning is { Length: > 0 })
            sb.AppendLine("----- reasoning (not part of the answer) -----").AppendLine(reasoning);
        foreach (var kv in calls)
            sb.Append("  [tool_call ").Append(kv.Value.Name).Append("] ").AppendLine(kv.Value.Args.ToString());
        if (finish is not null) sb.Append("finish=").Append(finish).Append("  ");
        if (promptTokens is { } p) sb.Append("prompt_tokens=").Append(p).Append("  ");
        if (cachedTokens is { } cache) sb.Append("cached_prompt_tokens=").Append(cache).Append("  ");
        if (completionTokens is { } c) sb.Append("completion_tokens=").Append(c);
        return sb.ToString().TrimEnd();
    }

    private static string RenderCompletion(ChatCompletion completion)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(completion.Message.Content)) sb.AppendLine(completion.Message.Content);
        if (completion.Thinking is { Length: > 0 } thinking)
            sb.AppendLine("----- reasoning (not part of the answer) -----").AppendLine(thinking);
        if (completion.Message.ToolCalls is { Count: > 0 } calls)
            foreach (var c in calls)
                sb.Append("  [tool_call ").Append(c.Name).Append("] ").AppendLine(c.ArgumentsJson);
        if (completion.FinishReason is { } fr) sb.Append("finish=").Append(fr).Append("  ");
        if (completion.PromptTokens is { } p) sb.Append("prompt_tokens=").Append(p).Append("  ");
        // Printed whenever the provider answered, INCLUDING zero. "It cached nothing" and "it does
        // not report caching" are different facts, and a log that shows only the first is how a
        // run of 31 million prompt tokens came to have an unknowable cost - see
        // OpenAiCompatibleProvider.CachedTokens.
        if (completion.CachedPromptTokens is { } cache)
            sb.Append("cached_prompt_tokens=").Append(cache).Append("  ");
        if (completion.CompletionTokens is { } c2) sb.Append("completion_tokens=").Append(c2);
        return sb.ToString().TrimEnd();
    }
}
