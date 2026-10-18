namespace Enactive.Application;

using System.Text;
using Enactive.Core;

/// <summary>
/// Reading a file of a workspace to show it to a person - who, on the service, reads it through the service and may
/// have no other way to reach that machine's disks at all (ADR 0002, open question "viewing files").
///
/// <para>So it is only ever a file UNDER the workspace: the path is made canonical and must stay inside the folder
/// (RunHistoryService.FileOf), and no folder or file on the way may be a junction or a link - <c>docs</c> pointed at
/// <c>C:\Users</c> is inside the workspace by name and outside it by fact. It is text, and not more of it than a page
/// can show: a log of gigabytes or an executable is refused with what it is, not streamed into a browser.</para>
/// </summary>
internal static class WorkspaceFiles
{
    /// <summary>The most a page is sent. Past it, the file is somebody's to open where it is.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The file's text, or why it is not shown.</summary>
    public static (string? Text, string? Problem) Read(string root, string relative)
    {
        if (RunHistoryService.FileOf(root, relative) is not { } full)
            return (null, "That path is not in this workspace.");

        if (LinkOnTheWay(root, full) is { } link)
            return (null, $"'{link}' is a link to somewhere else; files are shown only from the workspace itself.");

        var file = new FileInfo(full);
        if (!file.Exists)
            return (null, "The file is not there any more.");
        if (file.Length > MaxBytes)
            return (null, $"The file is {Invariant.Format(file.Length / 1024 / 1024.0, "0.#")} MB; a page shows files up to {MaxBytes / 1024 / 1024} MB.");

        return Decode(File.ReadAllBytes(full));
    }

    /// <summary>
    /// Bytes as text a page can show, or why not - the same rules for a file on disk and for a copy of how it was before a
    /// run (WrittenFileReview), so the two sides of a diff are held to one standard.
    /// </summary>
    public static (string? Text, string? Problem) Decode(byte[] bytes)
    {
        if (bytes.Length > MaxBytes)
            return (null, $"The file is {Invariant.Format(bytes.Length / 1024 / 1024.0, "0.#")} MB; a page shows files up to {MaxBytes / 1024 / 1024} MB.");
        // A zero byte in the first few kilobytes is the cheap, reliable sign of a binary file; text formats do not use it.
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).IndexOf((byte)0) >= 0)
            return (null, "The file is not text.");

        return (Encoding.UTF8.GetString(bytes), null);
    }

    /// <summary>
    /// The first folder or file between the workspace and the file that is a reparse point - a junction, a symbolic
    /// link, a mount - or null. The workspace folder itself may be one: that is where its owner put it.
    /// </summary>
    internal static string? LinkOnTheWay(string root, string full)
    {
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var at = full;
        while (at.Length > top.Length)
        {
            var info = new FileInfo(at);
            if (info.Exists || Directory.Exists(at))
            {
                var attributes = File.GetAttributes(at);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    return Path.GetRelativePath(top, at);
            }
            at = Path.GetDirectoryName(at) ?? top;
        }
        return null;
    }
}
