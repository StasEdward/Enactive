namespace Enactive.Core.Chat;

using Enactive.Core.Providers;

public static class GenerationAllowance
{
    public static int Total(int visible, int reasoning)
        => (int)Math.Clamp((long)visible + Math.Max(0, reasoning), 1, int.MaxValue);

    /// <param name="promptTokens">
    /// The prompt's size as somebody MEASURED it - a provider's count and what was added since. Without
    /// it the prompt is estimated from characters at a fixed, deliberately pessimistic rate, which near
    /// a full window refuses requests that fit.
    /// </param>
    public static ChatRequest Fit(ChatRequest request, IChatProvider provider, int? promptTokens = null)
    {
        var limit = Total(request.OutputTokenLimit ?? 2048, provider.ReasoningAllowance(request));
        if (provider.ContextWindow(request) is > 0 and var window)
        {
            var chars = Transcript.Size(request.Messages)
                + (request.ResponseSchema?.Length ?? 0)
                + (request.Tools is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(request.Tools).Length : 0);
            var prompt = promptTokens ?? new TokenScale().TokensFor(chars);
            var room = window - prompt;
            // With the numbers: the prompt here is an ESTIMATE from characters at a fixed rate, and a
            // refusal that does not say so cannot be told from a prompt that really is too large.
            if (room < 128) throw new InvalidOperationException("Insufficient context room for a complete model response. Increase the context window (num_ctx on Ollama) or shorten the request. "
                + (promptTokens is not null
                    ? $"(window {window} tokens; prompt measured at {prompt} tokens)"
                    : $"(window {window} tokens; prompt estimated at {prompt} tokens from {chars} characters at {TokenScale.DefaultCharsPerToken} per token)"));
            limit = Math.Min(limit, room);
        }
        return request with { OutputTokenLimit = limit };
    }
}
