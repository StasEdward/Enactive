namespace Enactive.Workspace;

using Enactive.Core.Artifacts;

/// <summary>
/// Puts a file a command changed back from the workspace snapshot taken before the command - byte for byte, or not at
/// all.
///
/// <para><b>Why.</b> Bench build-error, 2026-10-18: refused twice on edit_file, the worker rewrote the forbidden file
/// with a PowerShell <c>-replace | Set-Content</c>; the store had no bytes of it, and the run ended with the file still
/// changed. The snapshot (<see cref="WorkspaceChanges"/>) does hold it - as a blob, the way <c>git add</c> stored it.</para>
///
/// <para><b>Why only byte for byte.</b> The owner's rule: never write what was not there. A blob is the file's bytes
/// only where git converted nothing on the way in - no <c>core.autocrlf</c>, no <c>text</c>, <c>eol</c>,
/// <c>filter</c>, <c>ident</c> or <c>working-tree-encoding</c> attribute. Those are read through
/// <see cref="AutomaticGit"/>, as the snapshot was made: the repository's own configuration and attributes, never the
/// global ones, so they are the settings the blob was made under. Anything else - a converted file, a link, a file
/// changed again since the command, a call that changed a <c>.gitattributes</c> - is left and said why.</para>
/// </summary>
internal static class ExactRestore
{
    private static readonly string[] ConvertingAttributes = ["eol", "filter", "ident", "working-tree-encoding"];

    public static async Task<PutBack> PutBackAsync(string root, string beforeTree, string afterTree, string path,
        IReadOnlyCollection<string> changedWithIt, CancellationToken ct)
    {
        if (changedWithIt.Any(p => Path.GetFileName(p.Replace('\\', '/').TrimEnd('/')).Equals(".gitattributes", StringComparison.OrdinalIgnoreCase)))
            return PutBack.Left(PutBackReasons.Attributes);
        if (await ConvertsAsync(root, path, ct))
            return PutBack.Left(PutBackReasons.Conversion);

        var full = Path.GetFullPath(Path.Combine(root, path));
        // Still as the command left it: changed since, it is no longer the command's change to undo.
        var afterEntry = await EntryAsync(root, afterTree, path, ct);
        var now = File.Exists(full) ? await HashAsync(root, full, ct) : null;
        if (!string.Equals(afterEntry?.Blob, now, StringComparison.Ordinal))
            return PutBack.Left(PutBackReasons.ChangedAgain);

        if (await EntryAsync(root, beforeTree, path, ct) is not { } before || !Regular(before.Mode))
            return PutBack.Left(PutBackReasons.NotAFile);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + ".enactive-putback");
        try
        {
            if (await AutomaticGit.RunToFileAsync(root, ["cat-file", "blob", before.Blob], temp, ct) != 0
                || !await VerifiedAsync(root, temp, before.Blob, ct))
                return PutBack.Left(PutBackReasons.NotExact);
            File.Move(temp, full, overwrite: true);
            return PutBack.Back;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PutBack.Left(PutBackReasons.NotExact);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A plain file, executable or not - not a link (120000) and not a submodule (160000).</summary>
    internal static bool Regular(string mode) => mode is "100644" or "100755";

    /// <summary>Whether <paramref name="file"/>'s bytes, taken as they are, are the blob <paramref name="blobId"/>.</summary>
    internal static async Task<bool> VerifiedAsync(string root, string file, string blobId, CancellationToken ct)
        => string.Equals(await HashAsync(root, file, ct), blobId, StringComparison.Ordinal);

    private static async Task<bool> ConvertsAsync(string root, string path, CancellationToken ct)
    {
        var autocrlf = await AutomaticGit.RunAsync(root, ["config", "--get", "core.autocrlf"], ct);
        if (autocrlf.Exit == 0 && !autocrlf.Output.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)) return true;
        if (autocrlf.Exit is not (0 or 1)) return true;   // could not be read: not proven to convert nothing

        var attrs = await AutomaticGit.RunAsync(root, ["check-attr", "-z", "text", "eol", "filter", "ident", "working-tree-encoding", "--", path], ct);
        if (attrs.Exit != 0) return true;
        var parts = attrs.Output.Split('\0');
        for (var i = 0; i + 2 < parts.Length; i += 3)
        {
            var (name, value) = (parts[i + 1], parts[i + 2]);
            if (name == "text" ? value is not ("unspecified" or "unset") : ConvertingAttributes.Contains(name) && value != "unspecified")
                return true;
        }
        return false;
    }

    private static async Task<(string Mode, string Blob)?> EntryAsync(string root, string tree, string path, CancellationToken ct)
    {
        var listed = await AutomaticGit.RunAsync(root, ["ls-tree", "-z", tree, "--", path], ct);
        if (listed.Exit != 0) return null;
        // "<mode> <type> <id>\t<path>"
        var entry = listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var head = entry?.Split('\t')[0].Split(' ');
        return head is { Length: 3 } && head[1] == "blob" ? (head[0], head[2]) : null;
    }

    private static async Task<string?> HashAsync(string root, string file, CancellationToken ct)
    {
        var hashed = await AutomaticGit.RunAsync(root, ["hash-object", "--no-filters", "--", file], ct);
        return hashed.Exit == 0 && hashed.Output.Trim() is { Length: > 0 } id ? id : null;
    }
}
