namespace Enactive.Core.Tasks;

using System.Text.Json;

/// <summary>What one field of a step's output holds. A closed set, because every type here is one the engine can check.</summary>
public enum StepOutputFieldType
{
    /// <summary>Prose for a person or the next step to read. Over its limit it is cut and marked, never refused.</summary>
    Text,

    /// <summary>A short value: a name, an identifier, a status.</summary>
    String,

    Integer,
    Boolean,

    /// <summary>A workspace-relative path that exists - on disk, or staged by this run.</summary>
    Path,

    /// <summary>A list of <see cref="Path"/>.</summary>
    PathList,

    /// <summary>A list of <see cref="String"/>.</summary>
    StringList
}

/// <summary>
/// One field of a step's output. The limits are the WORK's, declared with it (amendment C.7): how
/// many pages a discovery step may name is a fact about that task, and a constant chosen for one
/// scenario would be inherited by all the others. Null means the work set none.
/// </summary>
public sealed record StepOutputField(
    string Name,
    StepOutputFieldType Type,
    string Description,
    bool Required = true,
    int? MaxItems = null,
    int? MaxLength = null);

/// <summary>The contract of a step's result: what it hands the steps after it, as values.</summary>
public sealed record StepOutputSchema(string Id, int Version, IReadOnlyList<StepOutputField> Fields)
{
    /// <summary>The wire names of the types, as the planner writes them.</summary>
    public static string NameOf(StepOutputFieldType type) => type switch
    {
        StepOutputFieldType.Text => "text",
        StepOutputFieldType.String => "string",
        StepOutputFieldType.Integer => "integer",
        StepOutputFieldType.Boolean => "boolean",
        StepOutputFieldType.Path => "path",
        StepOutputFieldType.PathList => "path[]",
        _ => "string[]"
    };

    public static StepOutputFieldType? TypeNamed(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "text" => StepOutputFieldType.Text,
        "string" => StepOutputFieldType.String,
        "integer" or "int" or "number" => StepOutputFieldType.Integer,
        "boolean" or "bool" => StepOutputFieldType.Boolean,
        "path" => StepOutputFieldType.Path,
        "path[]" or "paths" => StepOutputFieldType.PathList,
        "string[]" or "strings" => StepOutputFieldType.StringList,
        _ => null
    };
}

/// <summary>
/// A step's result, accepted by the engine: the values, and where they came from (Phase 2.3).
/// <see cref="ValuesJson"/> is a JSON object whose members are exactly the schema's fields.
/// </summary>
public sealed record StepOutput(
    int StepNo,
    string Step,
    Guid AttemptId,
    string SchemaId,
    int SchemaVersion,
    DateTimeOffset At,
    string ValuesJson,
    IReadOnlyList<int> Evidence,
    int Revision,
    IReadOnlyList<string> Notes)
{
    /// <summary>One value by name, or null.</summary>
    public JsonElement? Value(string name)
    {
        using var doc = JsonDocument.Parse(ValuesJson);
        return doc.RootElement.TryGetProperty(name, out var value) ? value.Clone() : null;
    }
}
