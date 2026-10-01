namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Xunit;

/// <summary>
/// Folders outside a workspace that it may write to, remembered past the run.
///
/// <para>The fourth answer on the geography card — <i>keep for this workspace</i> — was in
/// the sandbox plan's own table and could not be built, because a remembered grant needs
/// somewhere outside the workspace to live. That is what step 2 was for and what none of the
/// pieces before it built: each solved its own problem instead.</para>
///
/// <para>The rules here are the reason this may exist at all. "Remember: allow run_command" is
/// refused everywhere in this codebase, because one click would buy unlimited command execution
/// forever. A writable root is a different thing — it changes the SHAPE of the boundary and not the
/// tool set — but only while it is small. A root of <c>C:\</c> would be the forever-button under
/// another name, which is what <see cref="WritableRoots.Refuses"/> is for.</para>
/// </summary>
public sealed class WritableRootsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "enactive-roots", Guid.NewGuid().ToString("N"));

    private readonly string _workspace;
    private readonly string _other;
    private readonly WritableRoots _roots;

    public WritableRootsTests()
    {
        Directory.CreateDirectory(_dir);
        _workspace = Path.Combine(_dir, "project");
        _other = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_other);
        _roots = new WritableRoots(Path.Combine(_dir, "writable-roots.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder */ }
    }

    // ── what the store is for ───────────────────────────────────────────────

    [Fact]
    public void A_workspace_starts_with_nothing()
    {
        Assert.Empty(_roots.For(_workspace));
    }

    [Fact]
    public void A_granted_folder_is_remembered()
    {
        Assert.Null(_roots.Add(_workspace, _other));

        Assert.Contains(_other, _roots.For(_workspace), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A grant covers what is under it — the same rule <c>GrantedRoots</c> has, and
    /// <c>WorkspaceGuard.IsInside</c>'s rather than a second opinion about "inside".</summary>
    [Fact]
    public void A_grant_covers_what_is_under_it()
    {
        _roots.Add(_workspace, _other);

        Assert.True(_roots.Covers(_workspace, Path.Combine(_other, "build", "app.exe")));
    }

    /// <summary>
    /// The point of a STORE rather than a field: it outlives the object that wrote it. A second
    /// instance reading the same file is the closest a test gets to the next run.
    /// </summary>
    [Fact]
    public void A_grant_outlives_the_run_that_made_it()
    {
        _roots.Add(_workspace, _other);

        var nextRun = new WritableRoots(Path.Combine(_dir, "writable-roots.json"));

        Assert.Contains(_other, nextRun.For(_workspace), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Withdrawable, or it is not a grant but a trapdoor. This store exists to hold precisely the
    /// permissions that outlive the moment somebody agreed to them.
    /// </summary>
    [Fact]
    public void A_grant_can_be_taken_back()
    {
        _roots.Add(_workspace, _other);
        _roots.Revoke(_workspace, _other);

        Assert.Empty(_roots.For(_workspace));
    }

    /// <summary>A wider grant replaces the narrower ones it swallows — otherwise revoking the one
    /// a person can see leaves them still covered by one they cannot.</summary>
    [Fact]
    public void A_wider_grant_replaces_the_ones_it_covers()
    {
        var inner = Path.Combine(_other, "build");
        Directory.CreateDirectory(inner);

        _roots.Add(_workspace, inner);
        _roots.Add(_workspace, _other);

        Assert.Equal(_other, Assert.Single(_roots.For(_workspace)), StringComparer.OrdinalIgnoreCase);
    }

    // ── authority is keyed to the PATH ──────────────────────────────────────

    /// <summary>
    /// Rule 1 of <see cref="ApprovalStore"/>, here for the same reason: a clone of a repository
    /// must not arrive carrying somebody's standing permission to write to their build folder. The
    /// key is derived from the folder handed in, so no host can get it wrong.
    /// </summary>
    [Fact]
    public void Another_workspace_does_not_inherit_the_grant()
    {
        var clone = Path.Combine(_dir, "project-copy");
        Directory.CreateDirectory(clone);

        _roots.Add(_workspace, _other);

        Assert.Empty(_roots.For(clone));
    }

    // ── the rules that make this safe to have at all ────────────────────────

    /// <summary>
    /// THE rule. A root covering <c>%APPDATA%/Enactive</c> hands the shell the approvals file, this
    /// file and the settings — the exact hole that moving the policy out of the workspace closed,
    /// dug again from the other side and with consent. One click would buy the ability to grant
    /// every remaining click.
    /// </summary>
    [Fact]
    public void The_folder_holding_the_permissions_can_never_be_granted()
    {
        var settings = Path.GetDirectoryName(WritableRoots.DefaultPath())!;

        Assert.NotNull(WritableRoots.Refuses(settings));
        Assert.NotNull(WritableRoots.Refuses(Path.Combine(settings, "anything")));
    }

    [Fact]
    public void A_whole_drive_can_never_be_granted()
    {
        var drive = Path.GetPathRoot(Path.GetFullPath(_dir))!;

        Assert.NotNull(WritableRoots.Refuses(drive));
    }

    /// <summary>
    /// A whole user profile is documents, desktop and keys. Nobody agreeing to somewhere for a
    /// build to write believes they are agreeing to that.
    /// </summary>
    [Fact]
    public void A_user_profile_can_never_be_granted()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            return;   // not a thing on this OS

        Assert.NotNull(WritableRoots.Refuses(profile));
    }

    /// <summary>
    /// And a folder UNDER the profile is fine — the other half of the same rule, and the half the
    /// first version got wrong. <c>C:\Users\someone\source\repos</c> is where the work is; a check
    /// that refused it would have been useless in exactly the ordinary case, and this test is what
    /// said so.
    /// </summary>
    [Fact]
    public void A_folder_under_the_profile_is_allowed()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            return;   // not a thing on this OS

        Assert.Null(WritableRoots.Refuses(Path.Combine(profile, "source", "repos", "out")));
    }

    /// <summary>A folder that CONTAINS the profile is refused — granting it grants the profile.
    /// The relation is the point: inside is fine, containing is not.</summary>
    [Fact]
    public void A_folder_containing_the_profile_is_refused()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parent = Path.GetDirectoryName(profile);
        if (string.IsNullOrWhiteSpace(parent))
            return;   // not a thing on this OS

        Assert.NotNull(WritableRoots.Refuses(parent));
    }

    [Fact]
    public void A_system_folder_can_never_be_granted()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
            return;   // not a thing on this OS

        Assert.NotNull(WritableRoots.Refuses(windows));
    }

    /// <summary>A refused folder is not quietly dropped — Add says WHY, because the person is
    /// standing in front of a card and a no without a reason reads as a fault.</summary>
    [Fact]
    public void A_refused_folder_is_refused_with_a_reason_and_stored_nowhere()
    {
        var drive = Path.GetPathRoot(Path.GetFullPath(_dir))!;

        var refusal = _roots.Add(_workspace, drive);

        Assert.False(string.IsNullOrWhiteSpace(refusal));
        Assert.Empty(_roots.For(_workspace));
    }

    /// <summary>An ordinary working folder is not refused — a check that said no to everything
    /// would be safe and useless, and nobody would keep it.</summary>
    [Fact]
    public void An_ordinary_folder_is_allowed()
    {
        Assert.Null(WritableRoots.Refuses(_other));
    }

    // ── the boring ways it must not go wrong ────────────────────────────────

    /// <summary>
    /// Unreadable means NO roots. Failing the other way would WIDEN a boundary because a file was
    /// corrupt — the same direction <see cref="ApprovalStore"/> fails in, for the same reason.
    /// </summary>
    [Fact]
    public void An_unreadable_file_grants_nothing()
    {
        var path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.Empty(new WritableRoots(path).For(_workspace));
    }

    /// <summary>A folder already inside the workspace is writable already: asking for a state that
    /// holds is success, not an error to report.</summary>
    [Fact]
    public void A_folder_inside_the_workspace_is_not_an_error_and_is_not_stored()
    {
        Assert.Null(_roots.Add(_workspace, Path.Combine(_workspace, "src")));
        Assert.Empty(_roots.For(_workspace));
    }

    [Fact]
    public void A_folder_already_covered_is_not_stored_twice()
    {
        var inner = Path.Combine(_other, "build");
        Directory.CreateDirectory(inner);

        _roots.Add(_workspace, _other);
        _roots.Add(_workspace, inner);

        Assert.Single(_roots.For(_workspace));
    }

    /// <summary>A run starts from what the store holds, which is the one thing the engine needs
    /// from it — see RunScope.</summary>
    [Fact]
    public void A_run_seeded_from_the_store_covers_the_granted_place()
    {
        _roots.Add(_workspace, _other);

        var granted = new GrantedRoots(_roots.For(_workspace));

        Assert.True(granted.Covers(Path.Combine(_other, "out", "app.exe")));
    }
}
