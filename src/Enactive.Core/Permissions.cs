namespace Enactive.Core.Permissions;

/// <summary>Autonomy tier that gates each tool / action.</summary>
public enum PermissionLevel
{
    Observe,     // read only
    Suggest,     // prepare changes but do not apply
    Execute,     // modify files, run scripts
    Autonomous   // install, deploy, act without asking
}

/// <summary>
/// Effective permission policy. Lives on a Workspace; a Task may override it (PLAN_v2 §2.10).
/// </summary>
public sealed record PermissionPolicy(
    PermissionLevel Level,
    IReadOnlyList<string> Allow,
    IReadOnlyList<string> AskBefore)
{
    /// <summary>Permissive default used by the first vertical slice (no gating yet).</summary>
    public static readonly PermissionPolicy PermissiveDefault =
        new(PermissionLevel.Execute, new[] { "*" }, Array.Empty<string>());
}
