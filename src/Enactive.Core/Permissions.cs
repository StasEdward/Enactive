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
    /// <summary>
    /// Tools this run may not use at all, whatever its autonomy tier.
    ///
    /// <para>Until this existed the engine could only answer Allow or Ask: everything a policy did
    /// not permit turned into a question. That is fine while a person is sitting there to answer
    /// it, and exactly wrong for a saved task whose whole point is to say "this one may never
    /// push" - the run would stop and wait for an approval nobody is there to give, and a run
    /// nobody is watching would sit on it until it was cancelled.</para>
    ///
    /// <para>An init property rather than a fourth positional parameter, so every existing
    /// construction of a policy still compiles and still means what it meant.</para>
    /// </summary>
    public IReadOnlyList<string> Deny { get; init; } = Array.Empty<string>();

    /// <summary>Permissive default used by the first vertical slice (no gating yet).</summary>
    public static readonly PermissionPolicy PermissiveDefault =
        new(PermissionLevel.Execute, new[] { "*" }, Array.Empty<string>());
}
