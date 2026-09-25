namespace Enactive.Providers;

using Enactive.Core.Chat;
using Enactive.Core.Providers;

/// <summary>One output-budget contract for every wire adapter.</summary>
internal static class OutputTokenBudget
{
    public static int? Resolve(ChatRequest request, ProviderDescriptor descriptor,
        int? defaultBudget = null, int? modelLimit = null)
    {
        if (request.OutputTokenLimit is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.OutputTokenLimit),
                "A hard output token limit must be positive.");

        // Non-positive legacy preferences mean unset; they must not become Ollama's unlimited -1.
        var budget = Positive(request.MaxTokens) ?? Positive(descriptor.MaxTokens) ?? Positive(defaultBudget);
        budget = Clamp(budget, request.OutputTokenLimit);
        return Clamp(budget, Positive(modelLimit));
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;
    private static int? Clamp(int? budget, int? ceiling)
        => ceiling is { } cap ? Math.Min(budget ?? cap, cap) : budget;
}
