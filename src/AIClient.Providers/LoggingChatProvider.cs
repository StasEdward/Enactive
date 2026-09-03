namespace AIClient.Providers;

using System.Runtime.CompilerServices;
using System.Text;
using AIClient.Core.Chat;
using AIClient.Core.Diagnostics;
using AIClient.Core.Providers;

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
    private readonly IChatProvider _inner;
    private readonly ILogSink _log;
    private readonly string _providerId;

    public LoggingChatProvider(IChatProvider inner, ILogSink log, string providerId)
    {
        _inner = inner;
        _log = log;
        _providerId = providerId;
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        _log.Info(LogSource.Prompt,
            $"prompt → {_providerId}/{request.Model} ({request.Messages.Count} messages)",
            RenderPrompt(request), request.Model);

        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
        string? finish = null;
        int? promptTokens = null, completionTokens = null;
        var faulted = false;

        try
        {
            await foreach (var evt in _inner.StreamChatAsync(request, ct))
            {
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
                    case UsageDelta u:
                        promptTokens = u.PromptTokens ?? promptTokens;
                        completionTokens = u.CompletionTokens ?? completionTokens;
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
            _log.Write(faulted ? LogLevel.Warn : LogLevel.Info, LogSource.Llm,
                $"response ← {_providerId}/{request.Model}"
                    + $" ({text.Length} chars, {calls.Count} tool call(s)"
                    + (finish is null ? "" : $", finish={finish}")
                    + (completionTokens is { } c ? $", {c} out-tokens" : "") + ")",
                RenderResponse(text.ToString(), calls, finish, promptTokens, completionTokens),
                request.Model);
        }
    }

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        _log.Info(LogSource.Prompt,
            $"prompt → {_providerId}/{request.Model} ({request.Messages.Count} messages, non-stream)",
            RenderPrompt(request), request.Model);

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
                + (completion.FinishReason is { } fr ? $", finish={fr}" : "") + ")",
            RenderCompletion(completion), request.Model);
        return completion;
    }

    private static string RenderPrompt(ChatRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("model=").Append(request.Model);
        if (request.Temperature is { } t) sb.Append("  temperature=").Append(t);
        if (request.NumCtx is { } n) sb.Append("  num_ctx=").Append(n);
        if (request.MaxTokens is { } mx) sb.Append("  max_tokens=").Append(mx);
        sb.AppendLine();
        if (request.Tools is { Count: > 0 } tools)
            sb.Append("tools: ").AppendLine(string.Join(", ", tools.Select(x => x.Name)));
        sb.AppendLine(new string('-', 40));

        foreach (var m in request.Messages)
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
        string? finish, int? promptTokens, int? completionTokens)
    {
        var sb = new StringBuilder();
        if (text.Length > 0) sb.AppendLine(text);
        foreach (var kv in calls)
            sb.Append("  [tool_call ").Append(kv.Value.Name).Append("] ").AppendLine(kv.Value.Args.ToString());
        if (finish is not null) sb.Append("finish=").Append(finish).Append("  ");
        if (promptTokens is { } p) sb.Append("prompt_tokens=").Append(p).Append("  ");
        if (completionTokens is { } c) sb.Append("completion_tokens=").Append(c);
        return sb.ToString().TrimEnd();
    }

    private static string RenderCompletion(ChatCompletion completion)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(completion.Message.Content)) sb.AppendLine(completion.Message.Content);
        if (completion.Message.ToolCalls is { Count: > 0 } calls)
            foreach (var c in calls)
                sb.Append("  [tool_call ").Append(c.Name).Append("] ").AppendLine(c.ArgumentsJson);
        if (completion.FinishReason is { } fr) sb.Append("finish=").Append(fr).Append("  ");
        if (completion.PromptTokens is { } p) sb.Append("prompt_tokens=").Append(p).Append("  ");
        if (completion.CompletionTokens is { } c2) sb.Append("completion_tokens=").Append(c2);
        return sb.ToString().TrimEnd();
    }
}
