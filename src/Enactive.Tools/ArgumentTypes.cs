namespace Enactive.Tools;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Arguments a model sent as strings where the tool's schema declares a number or a true/false - made the declared type
/// before the tool reads them.
///
/// <para><b>Why.</b> Qwen3.5 and later write tool calls in an XML form whose parameters are text; a server that does not
/// convert them by the schema hands on <c>"offset":"118"</c> or <c>"recursive":"false"</c>. A tool that reads a number
/// finds none and takes its default - read_file would read from line 1, silently, when the model asked for line 118 of
/// a kept output. The coercion is Unsloth Studio's idea (reviewed 2026-09-30); nothing of its code. Only top-level
/// properties and arrays of them; a value that does not parse as the declared type is left as it came, for the tool to
/// refuse in its own words.</para>
/// </summary>
internal static class ArgumentTypes
{
    /// <summary>The arguments with declared-type strings converted, or the same string where nothing needed it.</summary>
    public static string Coerce(string schemaJson, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || !argumentsJson.Contains('"')) return argumentsJson;
        JsonObject? args;
        Dictionary<string, JsonElement> declared;
        try
        {
            args = JsonNode.Parse(argumentsJson) as JsonObject;
            using var schema = JsonDocument.Parse(schemaJson);
            if (args is null || !schema.RootElement.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object)
                return argumentsJson;
            declared = properties.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        }
        catch (JsonException) { return argumentsJson; }

        var changed = false;
        foreach (var (name, value) in args.ToArray())
        {
            if (!declared.TryGetValue(name, out var property)) continue;
            if (Typed(Kind(property), value) is { } typed) { args[name] = typed; changed = true; }
            else if (value is JsonArray items && Kind(property) == "array"
                     && property.TryGetProperty("items", out var itemSchema) && Kind(itemSchema) is { } itemKind)
                for (var i = 0; i < items.Count; i++)
                    if (Typed(itemKind, items[i]) is { } typedItem) { items[i] = typedItem; changed = true; }
        }
        return changed ? args.ToJsonString() : argumentsJson;
    }

    /// <summary>The declared type, the first that is not "null" where a list of types is declared.</summary>
    private static string? Kind(JsonElement schema)
        => !schema.TryGetProperty("type", out var type) ? null
            : type.ValueKind == JsonValueKind.String ? type.GetString()
            : type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString())
                    .FirstOrDefault(t => t != "null")
                : null;

    private static JsonNode? Typed(string? kind, JsonNode? value)
    {
        if (value is not JsonValue v || !v.TryGetValue<string>(out var text)) return null;
        text = text.Trim();
        return kind switch
        {
            "integer" when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
            "number" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
                => JsonValue.Create(d),
            "boolean" when bool.TryParse(text, out var b) => JsonValue.Create(b),
            _ => null
        };
    }
}
