namespace Enactive.Core.Permissions;

using System.Text.Json;
using Enactive.Core.Context;

/// <summary>
/// Folders outside a workspace that this workspace may write to, remembered past the run.
///
/// <para><b>What this is for.</b> <c>SANDBOX_PLAN</c> step 4 offers a person three answers when a
/// command appears to write outside the workspace: once, for this run, or keep it inside. The
/// fourth — <i>add this as a writable root</i> — was in the plan's own table and could not be built,
/// because a remembered grant needs somewhere outside the workspace to live and there was nothing
/// to put it in. <see cref="GrantedRoots"/> says so in as many words. This is that somewhere.</para>
///
/// <para><b>Why it is not the "general policy store" the plan asked for.</b> Step 2 asked for one
/// per-workspace store and this is a second file beside <see cref="ApprovalStore"/>'s. The general
/// thing in this codebase turned out to be the PATTERN, not a file: in Core so a test can reach the
/// rules, under <c>%APPDATA%</c> so no tool can reach the data, keyed by the path so a clone cannot
/// inherit authority. One file per kind of authority is also what makes the rules below possible —
/// they are about places, and none of them would mean anything applied to a list of tool names.
/// </para>
///
/// <para><b>Remembering a PLACE is not remembering a TOOL.</b>
/// <see cref="Decisions.DecisionRequest.MayBeRemembered"/> stays false for a shell, and nothing here
/// changes that: the tier still decides whether a command may run at all, the shell setting still
/// decides whether it is asked about, and both are consulted before this is. What a root removes is
/// one question about one place. The distinction is the whole reason this may exist while
/// "remember: allow run_command" may not — that button meant unlimited command execution forever,
/// and this one means the boundary is a different shape.</para>
///
/// <para>That distinction only holds while a root is SMALL, which is what <see cref="Refuses"/> is
/// for. A root of <c>C:\</c> would silence the geography question everywhere and be exactly the
/// forever-button under another name.</para>
/// </summary>
public sealed class WritableRoots
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _file;

    /// <param name="filePath">
    /// Where the record lives. A parameter for the same reason <see cref="ApprovalStore"/> has one:
    /// the rules are the thing worth testing, and a test that wrote into the developer's real
    /// %APPDATA% to reach them is a test nobody runs twice.
    /// </param>
    public WritableRoots(string filePath) => _file = filePath;

    /// <summary>The store every host uses.</summary>
    public static WritableRoots Default { get; } = new(DefaultPath());

    /// <summary><c>%APPDATA%/Enactive/writable-roots.json</c> — outside every workspace.</summary>
    public static string DefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Enactive", "writable-roots.json");

    /// <summary>
    /// The folders this workspace has been given, for seeding a run's <see cref="GrantedRoots"/>.
    /// Empty for a workspace nobody has granted anything, and empty for a file that cannot be read.
    /// </summary>
    public IReadOnlyList<string> For(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return Array.Empty<string>();

        return Load().TryGetValue(KeyFor(workspaceRoot), out var roots)
            ? roots.ToArray()
            : Array.Empty<string>();
    }

    /// <summary>
    /// Remembers a folder for this workspace. Returns what stopped it, or null when it was added —
    /// a REASON rather than a bool, because the person is standing in front of a decision card and
    /// "no" without a why is how a refusal gets read as a bug.
    /// </summary>
    /// <remarks>
    /// Adding a folder already covered by a root, or one inside the workspace, is success and not an
    /// error: the caller asked for a state that already holds.
    /// </remarks>
    public string? Add(string workspaceRoot, string folder)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(folder))
            return "There is no folder to add.";

        var full = Normalise(folder);

        if (Refuses(full) is { } refusal)
            return refusal;

        // Already inside the workspace, or already covered. Nothing to write, nothing wrong.
        if (WorkspaceGuard.IsInside(Normalise(workspaceRoot), full) || Covers(workspaceRoot, full))
            return null;

        lock (_gate)
        {
            var all = Load();
            var key = KeyFor(workspaceRoot);
            if (!all.TryGetValue(key, out var roots))
                all[key] = roots = new List<string>();

            // A new root may CONTAIN ones already granted. Keeping both would leave the narrower
            // entries as decoration a person could revoke with no effect.
            roots.RemoveAll(existing => WorkspaceGuard.IsInside(full, Normalise(existing)));
            roots.Add(full);
            Save(all);
        }

        return null;
    }

    /// <summary>
    /// Withdraws a folder. Nothing here is useful without it: a grant a person cannot see the end of
    /// is a grant they cannot take back, and this store exists precisely to hold the ones that
    /// outlive the run that made them.
    /// </summary>
    public void Revoke(string workspaceRoot, string folder)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(folder))
            return;

        var full = Normalise(folder);

        lock (_gate)
        {
            var all = Load();
            var key = KeyFor(workspaceRoot);
            if (!all.TryGetValue(key, out var roots))
                return;

            if (roots.RemoveAll(existing => string.Equals(Normalise(existing), full, Comparison)) > 0)
                Save(all);
        }
    }

    /// <summary>Whether a root this workspace already has contains the path.</summary>
    public bool Covers(string workspaceRoot, string path)
    {
        var full = Normalise(path);
        foreach (var root in For(workspaceRoot))
            if (WorkspaceGuard.IsInside(Normalise(root), full))
                return true;

        return false;
    }

    /// <summary>
    /// Why this folder may not be a writable root, or null when it may.
    ///
    /// <para>Public and static so a UI can say no BEFORE offering the button, rather than offering
    /// it and then explaining. Four rules:</para>
    ///
    /// <list type="number">
    /// <item><b>Never a drive root.</b> <c>C:\</c> is not a place, it is everywhere, and a root that
    /// wide silences the question this store exists to answer.</item>
    /// <item><b>Never the folder this very record lives in, and never a folder containing it.</b>
    /// A root reaching <c>%APPDATA%/Enactive</c> hands the shell the approvals file, this file and
    /// the settings — the exact hole that moving the policy out of the workspace was meant to close,
    /// dug again from the other side and by consent. One click would buy the ability to grant every
    /// remaining click.</item>
    /// <item><b>Never a system folder, nor anything under one.</b> Nothing legitimate writes its
    /// build output into <c>C:\Windows</c>.</item>
    /// <item><b>Never a whole user profile — but anything UNDER one is fine.</b> The two halves are
    /// deliberately different, and getting it wrong once is how this rule was written: refusing
    /// everything inside the profile refuses <c>C:\Users\someone\source\repos</c>, which is where
    /// the work actually is, and the check would have been useless in exactly the ordinary case.
    /// What must not be granted is the profile ITSELF — that is documents, desktop and keys in one
    /// click — or any folder that swallows it.</item>
    /// </list>
    ///
    /// <para>These are not a security boundary — nothing here is; a shell can still write wherever
    /// the user's token allows and always could. They stop a person from CONSENTING to something
    /// they did not read, which is a different job and the only one a store like this can do.</para>
    /// </summary>
    public static string? Refuses(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return "There is no folder to add.";

        string full;
        try { full = Normalise(folder); }
        catch (Exception ex) { return $"That is not a usable path ({ex.GetType().Name})."; }

        // A drive root normalises to "C:\" and KEEPS its separator - TrimEndingDirectorySeparator
        // leaves a root alone by design. Comparing it against a trimmed "C:" is how the first
        // version of this let a whole drive through, and the test for it found that rather than the
        // rule it was written for.
        if (Path.GetPathRoot(full) is { Length: > 0 } drive && string.Equals(Normalise(drive), full, Comparison))
            return "A whole drive cannot be a writable root — name the folder the work belongs in.";

        if (Path.GetDirectoryName(DefaultPath()) is { Length: > 0 } settings)
        {
            var store = Normalise(settings);
            if (WorkspaceGuard.IsInside(store, full) || WorkspaceGuard.IsInside(full, store))
                return "That folder holds Enactive's own permissions, so it cannot be granted to a run.";
        }

        foreach (var system in SystemFolders())
            if (WorkspaceGuard.IsInside(system, full) || WorkspaceGuard.IsInside(full, system))
                return $"'{system}' is a system folder — name a folder made for this work instead.";

        foreach (var profile in Profiles())
            if (string.Equals(profile, full, Comparison) || WorkspaceGuard.IsInside(full, profile))
                return $"'{profile}' is a whole user profile — name the folder the work belongs in.";

        return null;
    }

    /// <summary>Folders nothing may be granted under, in either direction.</summary>
    private static IEnumerable<string> SystemFolders()
        => Special(
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86);

    /// <summary>Folders that may not be granted THEMSELVES, while their contents may.</summary>
    private static IEnumerable<string> Profiles()
        => Special(Environment.SpecialFolder.UserProfile);

    private static IEnumerable<string> Special(params Environment.SpecialFolder[] kinds)
    {
        foreach (var kind in kinds)
        {
            var path = Environment.GetFolderPath(kind);
            // Empty off Windows, where several of these do not exist. A blank string would
            // normalise to the current directory and refuse everything under it.
            if (!string.IsNullOrWhiteSpace(path))
                yield return Normalise(path);
        }
    }

    private static StringComparison Comparison
        => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static string Normalise(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// The key: the id the PATH gives, never the one the folder carries. Rule 1 of
    /// <see cref="ApprovalStore"/>, for the same reason and derived the same way — a clone of a
    /// repository must not inherit somebody's grant to write to their build folder.
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
            // Unreadable means NO roots. Failing the other way would widen a boundary because a
            // file was corrupt — the same direction ApprovalStore fails in, for the same reason.
            return new Dictionary<string, List<string>>();
        }
    }

    private void Save(Dictionary<string, List<string>> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);

            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, JsonOptions));
            File.Move(temp, _file, overwrite: true);
        }
        catch { /* a lost grant costs one more question; it must never take the app down */ }
    }
}
