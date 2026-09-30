namespace Enactive.Agents;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Where a validator's refusals are written down, one file each, with exactly what the validator
/// saw - so the refusal can be replayed offline against the same validator instead of rediscovered
/// by a run of several minutes. Kept by the planner's verification contract (<see cref="PlanCheckCorpus"/>).
///
/// <para>Only refusals are kept. They are the blocker, they are rare, and a file per accepted answer
/// would grow the workspace for nothing anybody replays.</para>
/// </summary>
internal static class RefusalCorpus
{
    /// <summary>Oldest removed past this many, per corpus. A bound, not a measurement: enough to hold weeks of refusals.</summary>
    internal const int Keep = 200;

    /// <summary>
    /// Enums by name. A case outlives the code that wrote it, and a number means something else the
    /// day an enum gains a member in the middle; a name either still means the same or fails to read.
    /// Numbers written before this still read.
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Best effort. The work must never fail because its refusal could not be written down: the
    /// corpus is an instrument, and the run it observes matters more than the observation.
    /// </summary>
    internal static void Record<T>(string? workspaceRoot, string folder, DateTimeOffset at, T refusal)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return;
        try
        {
            var path = Path.Combine(workspaceRoot, folder);
            Directory.CreateDirectory(path);
            var text = JsonSerializer.Serialize(refusal, Json);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
            File.WriteAllText(Path.Combine(path, $"{at:yyyyMMdd-HHmmss}-{hash}.json"), text);

            // The folder is the engine's own, and so is every file in it.
            foreach (var old in new DirectoryInfo(path).GetFiles("*.json")
                         .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Keep))
                old.Delete();
        }
        // ArgumentException too: a workspace path the file system rejects outright throws that, not
        // an IOException, and the test for this caught the first draft letting it through.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
        }
    }

    internal static T Read<T>(string path) where T : class
        => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
           ?? throw new InvalidDataException($"Not a {typeof(T).Name}: {path}");
}
