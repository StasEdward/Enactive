namespace Enactive.Remote.Contracts;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The one place either side turns these contracts into bytes.
///
/// <para>Enums travel as their names. A number would be smaller and would also mean that inserting
/// a member in the middle of an enum silently renumbers every stored payload - and payloads here
/// are stored, in a gateway's commands table and in a Host's outbox, across upgrades of both.</para>
///
/// <para>Unknown members are refused rather than ignored. A field the reader does not recognise is
/// a message from a version it does not understand, and quietly dropping it is how a Host executes
/// half of an instruction.</para>
/// </summary>
public static class RemoteJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"Expected {typeof(T).Name}, got null.");

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
