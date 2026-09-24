namespace Enactive.Providers;
using System.Text.Json;
using Enactive.Core.Chat;

internal static class ProviderTimings
{
    private static double? Seconds(JsonElement root, string key, double divisor)
        => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)
           && double.IsFinite(n) && n >= 0 ? n / divisor : null;
    private static int? Count(JsonElement root, string key)
        => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0 ? n : null;
    public static ModelTimings? OpenAi(JsonElement root)
        => root.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object
           ? new(Seconds(t, "prompt_ms", 1000), Seconds(t, "predicted_ms", 1000),
                 GenerationTokens: Count(t, "predicted_n")) : null;
    public static ModelTimings Ollama(JsonElement root)
        => new(Seconds(root, "prompt_eval_duration", 1e9), Seconds(root, "eval_duration", 1e9),
               Seconds(root, "load_duration", 1e9), Count(root, "eval_count"));
}
