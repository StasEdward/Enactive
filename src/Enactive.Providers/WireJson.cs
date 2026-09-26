namespace Enactive.Providers;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Enactive.Core.Tools;

// Weak keys avoid retaining tools and historical arguments after a run. Record copies get their
// own entry, so `with { JsonSchema = ... }` never reuses the previous schema.
internal static class WireJson
{
    private static readonly ConditionalWeakTable<ToolDefinition, Lazy<JsonElement>> Schemas = new();
    private static readonly ConditionalWeakTable<ToolCall, Lazy<JsonElement>> Arguments = new();

    public static JsonElement Schema(ToolDefinition tool) => Schemas.GetValue(tool,
        static t => new Lazy<JsonElement>(() => Parse(t.JsonSchema))).Value;

    public static JsonElement ArgumentsOf(ToolCall call) => Arguments.GetValue(call,
        static c => new Lazy<JsonElement>(() => Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson))).Value;

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
