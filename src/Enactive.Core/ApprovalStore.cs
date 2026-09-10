namespace Enactive.Core.Permissions;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Tools;

/// <summary>
/// Remembered "Allow (workspace)" approvals — which tools may run in which workspace without being
/// asked again.
///
/// <para>These used to live in <c>&lt;workspace&gt;/.enactive/permissions.json</c>, inside the very
/// folder the agent can write. <c>write_file</c> could append <c>run_command</c> to the list and the
/// next request was granted automatically; a repository could also arrive with the file already in
/// it. The barrier between "may edit files" and "may run commands" was administered by the thing it
/// was supposed to contain. So the record moved out of reach, to
/// <c>%APPDATA%/Enactive/permissions.json</c>.</para>
///
/// <para><b>Moved into Core 2026-09-10, and that is the point of this file's existence.</b> All
/// three rules below were already written, correctly, in <c>App.Ui</c> — which is a WinExe no test
/// project references. Three security rules whose only enforcement was prose in a comment, in the
/// one assembly nothing can compile against. This codebase has been here twice: <c>AutonomyTiers</c>
/// was moved out of <c>MainWindow</c> for exactly this reason, and by then the console host had
/// written its own version that disagreed. Nothing had diverged this time — the console has no
/// standing approvals at all and the remote host delegates to the desktop — which is luck rather
/// than design.</para>
///
/// <para>The three rules, each now enforced HERE and tested:</para>
/// <list type="number">
/// <item><b>Authority is keyed to the PATH.</b> Not to the id in <c>.enactive/workspace.json</c>:
/// that file travels with the folder, and a clone must not bring somebody else's standing "yes,
/// run_command is fine here" with it. Attribution may follow a renamed folder; authority may not.
/// The key is derived in here rather than taken from the caller, so no host can get it wrong.</item>
/// <item><b>A shell is never remembered.</b> Whatever is in the file. The button that granted it was
/// the same button as for <c>read_file</c> and meant unlimited command execution on the machine for
/// as long as the workspace exists — see <see cref="ShellTools"/>. Entries written by earlier builds
/// stay in the file and stop being honoured, which is the reversible way round.</item>
/// <item><b>A legacy in-workspace file is ignored, never imported.</b> Importing it would import
/// exactly the content an agent may have planted. Those approvals are one click each to grant
/// again.</item>
/// </list>
///
/// <para><see cref="WorkspaceGuard"/> separately refuses to let tools write under
/// <c>.enactive/</c>, so the guarantee does not rest on one mechanism.</para>
/// </summary>
public sealed class ApprovalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _file;

    /// <param name="filePath">
    /// Where the record lives. A parameter so a test can point it somewhere temporary — the rules
    /// below are the thing worth testing, and a test that had to write into the developer's real
    /// %APPDATA% to reach them would be a test nobody runs twice.
    /// </param>
    public ApprovalStore(string filePath) => _file = filePath;

    /// <summary>The store every host uses.</summary>
    public static ApprovalStore Default { get; } = new(DefaultPath());

    /// <summary>
    /// <c>%APPDATA%/Enactive/permissions.json</c> — outside every workspace, so no tool can reach
    /// it at all.
    /// </summary>
    public static string DefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Enactive", "permissions.json");

    /// <summary>
    /// Whether this workspace has a standing approval for the tool.
    /// </summary>
    /// <param name="workspaceRoot">
    /// The FOLDER, not an id. Rule 1 lives in that choice: handed an id, this method would honour
    /// whichever id the caller worked out, and the id a workspace carries is read from a file
    /// inside it. Handed a path, there is nothing to get wrong.
    /// </param>
    public bool Approves(string workspaceRoot, string tool)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrEmpty(tool) || ShellTools.IsShell(tool))
            return false;

        // NOTHING reads the workspace's own .enactive/permissions.json - see rule 3 in the summary.
        // The absence of that code IS the rule, which is why the test for it plants the file and
        // asserts it grants nothing rather than asserting on any code path here.
        return Load().TryGetValue(KeyFor(workspaceRoot), out var tools)
            && tools.Contains(tool, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Records an approval. Only ever called from a click on the decision card.</summary>
    public void Approve(string workspaceRoot, string tool)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrEmpty(tool) || ShellTools.IsShell(tool))
            return;

        lock (_gate)
        {
            var all = Load();
            var key = KeyFor(workspaceRoot);
            if (!all.TryGetValue(key, out var tools))
                all[key] = tools = new List<string>();

            if (tools.Contains(tool, StringComparer.OrdinalIgnoreCase))
                return;

            tools.Add(tool);
            Save(all);
        }
    }

    /// <summary>
    /// True when a workspace still carries the old in-folder approvals file, so a UI can say once
    /// that it is being ignored rather than letting the person wonder why they are asked again.
    /// </summary>
    public static bool HasLegacyFile(string workspaceRoot)
        => !string.IsNullOrWhiteSpace(workspaceRoot)
        && File.Exists(Path.Combine(workspaceRoot, WorkspaceGuard.ReservedFolder, "permissions.json"));

    /// <summary>
    /// The key an approval is filed under: the id the PATH gives, never the one the folder carries.
    /// </summary>
    private static string KeyFor(string workspaceRoot)
        => WorkspaceInfo.IdFor(workspaceRoot).ToString("N");

    private Dictionary<string, List<string>> Load()
    {
        try
        {
            if (!File.Exists(_file))
                return new Dictionary<string, List<string>>();

            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_file))
                ?? new Dictionary<string, List<string>>();
        }
        catch
        {
            // An unreadable approvals file means NO approvals. Failing the other way would hand out
            // standing permission because a file was corrupt.
            return new Dictionary<string, List<string>>();
        }
    }

    private void Save(Dictionary<string, List<string>> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);

            // Written beside the target and moved into place, so an interrupted save cannot leave a
            // half-written file that reads as "no approvals" — or worse, as something else.
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, JsonOptions));
            File.Move(temp, _file, overwrite: true);
        }
        catch { /* a lost approval costs one extra click; it must never take the app down */ }
    }
}
