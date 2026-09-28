namespace Enactive.Agents;

using System.Text.Json;
using System.Text.Json.Serialization;
using Enactive.Core.Builds;

/// <summary>
/// The workspace baseline of a TASK, kept beside it until the task reaches an end.
///
/// <para><b>Why beside the task and not only in the checkpoint.</b> A run carried on compares
/// against the workspace as it was before its FIRST attempt; a baseline taken again would call the
/// first attempt's errors old. A checkpoint carries it for a run resumed at a step boundary. But a
/// quick action has no step boundary and so no checkpoint, and since NeedsUser it is carried on all
/// the same - started again under the same task once its question is answered. This file is what
/// that run finds. Forgotten when the task's run reaches an end, with its answers.</para>
/// </summary>
internal sealed class BaselineStore(string workspaceRoot)
{
    internal const string Folder = ".enactive/baselines";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private string FileOf(Guid taskId) => Path.Combine(workspaceRoot, Folder, $"{taskId:N}.json");

    public IReadOnlyList<BaselineSnapshot>? Load(Guid taskId)
    {
        try
        {
            var file = FileOf(taskId);
            return File.Exists(file) ? JsonSerializer.Deserialize<List<BaselineSnapshot>>(File.ReadAllText(file), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Best effort: a baseline that cannot be kept costs the comparison of a carried-on run, never the run.</summary>
    public void Save(Guid taskId, IReadOnlyList<BaselineSnapshot> baseline)
    {
        try
        {
            var file = FileOf(taskId);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(baseline, Json));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public void Forget(Guid taskId)
    {
        try { File.Delete(FileOf(taskId)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
