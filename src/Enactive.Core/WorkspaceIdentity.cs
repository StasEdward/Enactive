namespace Enactive.Core.Context;

using System.Text.Json;

/// <summary>
/// The id a workspace carries with it, in <c>.enactive/workspace.json</c>.
///
/// <para>A workspace's identity used to be a hash of its path, so renaming or moving the folder
/// silently detached every run, memory entry and inbox item ever recorded for it. The rows stay in
/// the database and the filter stops matching them; nothing reports it, because a workspace with no
/// history and a workspace whose history is filed elsewhere look identical from here. Written down
/// inside the folder, the id travels with it — which is the only place an id CAN live if it is to
/// survive the folder being renamed.</para>
///
/// <para><b>This id says WHICH PROJECT, never WHAT IS ALLOWED.</b> The distinction is the whole
/// security argument for putting a file here at all. The folder is the one the agent works in, and
/// a repository can arrive from anywhere with this file already in it — the same reasoning that
/// moved remembered approvals OUT to <c>%APPDATA%</c> and made a legacy in-workspace permissions
/// file something to ignore rather than import. So:</para>
/// <list type="bullet">
///   <item>ATTRIBUTION follows the folder. Getting it wrong costs a confusing history.</item>
///   <item>AUTHORITY stays keyed to the PATH (<see cref="WorkspaceInfo.IdFor"/>). Getting it wrong
///   costs a shell, and a cloned repository must not be able to bring standing approvals with
///   it.</item>
/// </list>
///
/// <para><see cref="WorkspaceGuard"/> separately refuses to let the file tools write anywhere under
/// <c>.enactive/</c>, so an agent cannot rewrite its own workspace's id mid-run. That is a second
/// line, not the argument: the argument is that nothing this file says grants anything.</para>
/// </summary>
public static class WorkspaceIdentity
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>The file, relative to a workspace root.</summary>
    public static string PathFor(string rootPath)
        => Path.Combine(Path.GetFullPath(rootPath), WorkspaceGuard.ReservedFolder, "workspace.json");

    /// <summary>
    /// What this folder says its id is, or null when it does not say.
    ///
    /// <para>Null on anything unreadable — a missing file, a corrupt one, a value that is not a
    /// GUID, a folder that cannot be opened. The caller then falls back to the path hash, which is
    /// exactly the behaviour that existed before this file did: a workspace whose marker is damaged
    /// carries on as the workspace it was, rather than becoming a new one with no history.</para>
    /// </summary>
    public static Guid? Read(string rootPath)
    {
        try
        {
            var path = PathFor(rootPath);
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            var stored = JsonSerializer.Deserialize<StoredIdentity>(stream, Options);

            // Guid.Empty is not an id. It is what an absent or defaulted field deserialises to, and
            // treating it as one would file a workspace's history under the same key as every other
            // workspace whose marker was blank.
            return stored?.Id is { } id && id != Guid.Empty ? id : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the id down, if it is not already. Best-effort: a folder that refuses the write still
    /// works, on the id the path gives it.
    /// </summary>
    /// <param name="originalPath">
    /// Recorded for a person reading the file later, and for nothing else. It is deliberately not
    /// compared against the current path: telling a rename (which this exists to survive) from a
    /// copy (which ends up sharing an id) needs to see both folders at once, and this sees one.
    /// </param>
    public static void Write(string rootPath, Guid id)
    {
        try
        {
            var full = Path.GetFullPath(rootPath);
            Directory.CreateDirectory(Path.Combine(full, WorkspaceGuard.ReservedFolder));

            var path = PathFor(full);
            if (File.Exists(path))
                return;

            // Written through a temporary file and moved into place, like every other state file
            // here: a process killed mid-write leaves no marker rather than half of one, and no
            // marker is a case the reader already handles.
            var temporary = path + ".tmp";
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(
                    stream,
                    new StoredIdentity(id, DateTimeOffset.UtcNow, Path.TrimEndingDirectorySeparator(full)),
                    Options);

            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { /* a workspace that cannot be marked is still a workspace */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <param name="OriginalPath">Where the folder was when this was written. Information, not a check.</param>
    private sealed record StoredIdentity(Guid Id, DateTimeOffset CreatedAt, string OriginalPath);
}
