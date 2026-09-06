namespace Enactive.App.Ui;

using System.Text.Json;
using Enactive.Core.Context;

/// <summary>
/// Remembered "Allow (workspace)" approvals — which tools may run in which workspace without asking
/// again.
///
/// These used to live in <c>&lt;workspace&gt;/.enactive/permissions.json</c>, inside the very folder
/// the agent can write. <c>write_file</c> could therefore append <c>run_command</c> to the list, and
/// the next approval request was granted automatically; a repository could also arrive with the file
/// already in it. The barrier between "may edit files" and "may run commands" was administered by
/// the thing it was supposed to contain.
///
/// So the record moved out of reach: <c>%APPDATA%/Enactive/permissions.json</c>, keyed by workspace
/// id, written only when a person clicks the button. Tools cannot reach this folder at all — it is
/// not inside any workspace — and <see cref="WorkspaceGuard"/> separately refuses to let them write
/// <c>.enactive/</c>, so the guarantee does not rest on one mechanism.
///
/// A legacy file left in a workspace is IGNORED, never imported: importing it would import exactly
/// the content an agent may have planted. Those approvals are one click each to grant again.
/// </summary>
internal static class ApprovalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();

    private static string File_()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Enactive", "permissions.json");

    /// <summary>Whether this workspace has a standing approval for the tool.</summary>
    public static bool Approves(Guid workspaceId, string tool)
    {
        if (string.IsNullOrEmpty(tool))
            return false;

        return Load().TryGetValue(Key(workspaceId), out var tools)
            && tools.Contains(tool, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Records an approval. Only ever called from a click on the decision card.</summary>
    public static void Approve(Guid workspaceId, string tool)
    {
        if (string.IsNullOrEmpty(tool))
            return;

        lock (Gate)
        {
            var all = Load();
            var key = Key(workspaceId);
            if (!all.TryGetValue(key, out var tools))
                all[key] = tools = new List<string>();

            if (tools.Contains(tool, StringComparer.OrdinalIgnoreCase))
                return;

            tools.Add(tool);
            Save(all);
        }
    }

    /// <summary>
    /// True when a workspace still carries the old in-folder approvals file, so the UI can say once
    /// that it is being ignored rather than letting the user wonder why they are asked again.
    /// </summary>
    public static bool HasLegacyFile(string workspaceRoot)
        => !string.IsNullOrEmpty(workspaceRoot)
        && File.Exists(Path.Combine(workspaceRoot, WorkspaceGuard.ReservedFolder, "permissions.json"));

    private static string Key(Guid workspaceId) => workspaceId.ToString("N");

    private static Dictionary<string, List<string>> Load()
    {
        try
        {
            var file = File_();
            if (!File.Exists(file))
                return new Dictionary<string, List<string>>();

            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file))
                ?? new Dictionary<string, List<string>>();
        }
        catch
        {
            // An unreadable approvals file means NO approvals. Failing the other way would hand out
            // standing permission because a file was corrupt.
            return new Dictionary<string, List<string>>();
        }
    }

    private static void Save(Dictionary<string, List<string>> all)
    {
        try
        {
            var file = File_();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            // Write beside the target and move into place, so an interrupted save cannot leave a
            // half-written file that reads as "no approvals" — or worse, as something else.
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
        catch { /* a lost approval costs one extra click; it must never take the app down */ }
    }
}
