namespace Enactive.Tools;

using Enactive.Core.Context;

/// <summary>
/// Resolves a relative path inside the workspace root for a TOOL call, refusing anything that
/// escapes it. The rule itself lives in <see cref="WorkspaceGuard"/>, shared with the artifact
/// stores: this used to be one of three near-identical string checks, and only string checks —
/// a junction inside the workspace passed all three while the write followed it outside.
/// </summary>
public static class WorkspacePaths
{
    public static string ResolveInside(string root, string? relativePath)
        => WorkspaceGuard.ResolveInside(root, relativePath);
}
