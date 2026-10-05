namespace Enactive.Agents;

using Enactive.Core.Chat;

/// <summary>
/// Whether the server's prompt cache held from one request of a conversation to the next - and, where it did not, whether
/// the engine rewrote the conversation or it broke with nothing rewritten.
///
/// <para><b>Why it matters.</b> A conversation the engine only appends to should be read by the server once: each request
/// finds the last one in its cache and reads only what was added. Where it is not, every turn pays the whole prompt
/// again. On a hybrid model (Qwen3.5/3.6 MoE, recurrent layers beside attention) the server cannot roll back to any token,
/// only to a context checkpoint - so a change near the end of the prompt costs up to a checkpoint's spacing (8,192 tokens
/// on the user's server), and a trim near the start costs nearly the whole prompt (27 s at 49,533 tokens, 2026-09-24).
/// None of that showed: the usage was logged, but nothing compared it with the request before. The idea of measuring
/// the cache rather than assuming it is Unsloth Studio's (reviewed 2026-09-30); nothing of its code.</para>
///
/// <para>Rewritten or not is told by the messages themselves: a conversation only appended to still holds the very
/// messages it sent before, and a trim or a hand-over replaces them.</para>
/// </summary>
internal sealed class PrefixCacheWatch
{
    /// <summary>Tokens re-read, beyond what was added, below which a cache that held is not called broken.</summary>
    internal const int Slack = 1024;

    private ChatMessage[] _sent = [];
    private ChatMessage[] _previous = [];
    private int? _previousPrompt;
    private bool _observed;
    private string? _server;
    private bool _sameServer;
    private string? _tools;
    private bool _toolsChanged;

    /// <summary>Records what is about to be sent.</summary>
    /// <param name="server">The server and model the request goes to. A cache is the server's own: a request is compared
    /// with the request before it to the SAME one. A conversation can change hands - a step on a local server, the next
    /// routed to a cloud model - and the first request to the new server was told as a break "though the conversation
    /// only grew", when no cache of the old one could have been there for it (run b90162, 2026-10-05).</param>
    /// <param name="tools">The names of the tools sent with the request. A server renders them ahead of the conversation,
    /// so a change to them - a tool loaded from the step's catalog - is read again from there on. That is the engine's own
    /// doing, and is said as such rather than as a difference nobody can account for (runs 0a2be9 and 43cda6, 2026-10-05).</param>
    public void Sending(IReadOnlyList<ChatMessage> messages, string? server = null, IReadOnlyList<string>? tools = null)
    {
        var signature = tools is null ? null : string.Join('\n', tools);
        _toolsChanged = signature is not null && _tools is not null && signature != _tools;
        _tools = signature ?? _tools;
        _sameServer = server is null || _server is null || string.Equals(server, _server, StringComparison.Ordinal);
        _server = server ?? _server;
        _previous = _sent;
        _sent = messages.ToArray();
        _observed = false;
    }

    /// <summary>
    /// What the server said it read, against the request before: a line to record, or null where there is nothing to
    /// say - the cache held, this is the first request, or the server does not count cached tokens.
    /// </summary>
    public string? Observed(int? promptTokens, int? cachedTokens, bool promptIncludesCache)
    {
        // Once per request, on the report that has the numbers: a provider may report usage more than once in one reply.
        if (_observed || !promptIncludesCache || promptTokens is not { } now || cachedTokens is not { } cached) return null;
        _observed = true;
        var previousPrompt = _previousPrompt;
        _previousPrompt = now;
        if (previousPrompt is not { } before || _previous.Length == 0 || !_sameServer) return null;

        var reread = now - cached;
        var appended = _sent.Length >= _previous.Length && _previous.Select((m, i) => ReferenceEquals(m, _sent[i])).All(same => same);
        if (appended)
            // Only grown since: all of the request before should have been found. Re-reading more than what was added
            // (and a little slack) is the cache breaking with nothing rewritten.
            return cached >= before - Slack
                ? null
                : _toolsChanged
                ? $"Prompt cache: the tools sent with it changed since the request before (a tool was loaded), and the server "
                  + $"re-read {reread} of {now} prompt tokens (cached {cached}) from where they come. The price of loading, paid once."
                : $"Prompt cache: the server re-read {reread} of {now} prompt tokens (cached {cached}), though the "
                  + $"conversation only grew since the request before, whose prompt was {before} tokens. Something sent again "
                  + "differs from what the server holds - a message re-rendered, a reasoning block, or the tools.";
        return $"Prompt cache: the conversation was rewritten (a trim or a hand-over) and the server re-read {reread} of "
               + $"{now} prompt tokens (cached {cached}).";
    }
}
