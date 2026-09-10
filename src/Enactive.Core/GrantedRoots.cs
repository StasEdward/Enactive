namespace Enactive.Core.Context;

/// <summary>
/// The places outside the workspace a person has said yes to, for as long as this run lasts.
///
/// <para><b>It does not reach the disk, and that is the design.</b> The same reasoning as
/// <c>DecisionRequest.SessionOnly</c>: a remembered "yes" to a place outside the workspace is a
/// standing permission to write there, and one click should not buy that forever. A grant made
/// here dies with the run that made it, which is what makes it a smaller thing to agree to rather
/// than the same thing with a shorter name.</para>
///
/// <para>A persisted version - "add this as a writable root for this workspace" - belongs with the
/// policy store of SANDBOX_PLAN step 2, which does not exist yet. Building the remembering half
/// first, into the workspace folder, is precisely the design bug that step 2 is about.</para>
///
/// <para>The grant is the FOLDER, not the file: somebody who agrees to
/// <c>C:\builds\out\app.exe</c> has agreed to that build going to <c>C:\builds\out</c>, and asking
/// again for the next file in the same folder is how a question becomes noise. Everything under a
/// granted folder is covered, which is <see cref="WorkspaceGuard.IsInside"/>'s own rule rather
/// than a second opinion about what "inside" means.</para>
/// </summary>
public sealed class GrantedRoots
{
    private readonly List<string> _roots = new();

    /// <summary>The roots granted so far, for handing to <see cref="ShellGeography"/>.</summary>
    public IReadOnlyCollection<string> Roots => _roots;

    /// <summary>
    /// Remembers the folder this write lands in. A write whose place is unknown - a variable -
    /// grants nothing: there is no folder to name, and inventing one from this process's
    /// environment would grant somewhere nobody agreed to.
    /// </summary>
    public void Grant(OutsideWrite write)
    {
        if (!write.Known)
            return;

        var folder = Directory.Exists(write.Path)
            ? write.Path
            : Path.GetDirectoryName(write.Path);

        if (string.IsNullOrWhiteSpace(folder) || Covers(folder))
            return;

        _roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
    }

    /// <summary>Whether an already-granted root contains this path.</summary>
    public bool Covers(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (var root in _roots)
            if (WorkspaceGuard.IsInside(root, full))
                return true;

        return false;
    }
}
