namespace Enactive.Core.Chat;

using Enactive.Core.Providers;

public static class GenerationAllowance
{
    public static int Total(int visible, int reasoning)
        => (int)Math.Clamp((long)visible + Math.Max(0, reasoning), 1, int.MaxValue);

    public static ChatRequest Fit(ChatRequest request, IChatProvider provider)
    {
        var limit = Total(request.OutputTokenLimit ?? 2048, provider.ReasoningAllowance(request));
        if (provider.ContextWindow(request) is > 0 and var window)
        {
            var prompt = new TokenScale().TokensFor(Transcript.Size(request.Messages)
                + (request.ResponseSchema?.Length ?? 0)
                + (request.Tools is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(request.Tools).Length : 0));
            var room = window - prompt;
            if (room < 128) throw new InvalidOperationException("Insufficient context room for a complete model response. Increase the context window (num_ctx on Ollama) or shorten the request.");
            limit = Math.Min(limit, room);
        }
        return request with { OutputTokenLimit = limit };
    }
}
