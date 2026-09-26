namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Xunit;

/// <summary>
/// The three rules that decide what a remembered "Allow (workspace)" is worth.
///
/// <para>All three were already written, correctly, in <c>App.Ui</c> — a WinExe no test project
/// references. So the security argument for the whole approvals design rested on three lines
/// nothing could compile against, let alone check. This file exists because that is not a state a
/// rule should be in, not because anybody had found the rules wrong.</para>
///
/// <para>SANDBOX_PLAN step 2 asked for the policy to be moved OUT of the workspace. It had been,
/// piecemeal, by earlier work — approvals to <c>%APPDATA%</c>, autonomy and worker to the registry,
/// and <c>.enactive/workspace.json</c> reduced to an id that grants nothing. What the step was
/// really still missing was this.</para>
/// </summary>
public sealed class ApprovalStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "enactive-approvals", Guid.NewGuid().ToString("N"));

    private readonly ApprovalStore _store;

    public ApprovalStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new ApprovalStore(Path.Combine(_root, "permissions.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private string Workspace(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>The ordinary case, so the three refusals below are refusals and not a broken store.</summary>
    [Fact]
    public void An_approved_tool_is_remembered_for_the_workspace_it_was_granted_in()
    {
        var granted = Workspace("granted");
        var other = Workspace("other");

        _store.Approve(granted, "read_file");

        Assert.True(_store.Approves(granted, "read_file"));
        Assert.False(_store.Approves(granted, "write_file"), "An approval for one tool covered another.");
        Assert.False(_store.Approves(other, "read_file"), "An approval leaked into a different workspace.");
    }

    // ── Rule 1: authority is keyed to the PATH ──────────────────────────────

    /// <summary>
    /// A folder that arrives carrying somebody else's <c>.enactive/workspace.json</c> does NOT
    /// arrive carrying their approvals.
    ///
    /// <para>This is the whole reason attribution and authority are keyed differently, and the one
    /// rule here with a plausible attacker: a repository can be cloned from anywhere with that file
    /// already in it. The id inside it is what the run's history is filed under - and if it were
    /// also what permissions were filed under, a clone would grant itself whatever the original had
    /// been granted, silently, before anybody looked at it.</para>
    /// </summary>
    [Fact]
    public void A_folder_carrying_another_workspaces_id_does_not_inherit_its_approvals()
    {
        var original = Workspace("original");
        WorkspaceInfo.Adopt(original);              // writes .enactive/workspace.json
        _store.Approve(original, "read_file");

        // The clone: same marker file, different place on disk.
        var clone = Workspace("clone");
        Directory.CreateDirectory(Path.Combine(clone, WorkspaceGuard.ReservedFolder));
        File.Copy(WorkspaceIdentity.PathFor(original), WorkspaceIdentity.PathFor(clone));

        // The premise of the test: the two folders really do claim the same identity.
        Assert.Equal(WorkspaceInfo.For(original).Id, WorkspaceInfo.For(clone).Id);

        Assert.False(_store.Approves(clone, "read_file"),
            "A cloned folder inherited a standing approval by carrying the original's id file.");
    }

    // ── Rule 2: a shell is never remembered ─────────────────────────────────

    /// <summary>
    /// Asked for and refused — NOT WRITTEN, and not reported. "Allow (workspace)" for a shell was
    /// the same button as for <c>read_file</c> and meant unlimited command execution on the machine
    /// for as long as the workspace exists.
    ///
    /// <para>The second assertion is the one that earns its place. Written first with only the
    /// first, this test passed with the write-side check deleted — the read-side refusal covered
    /// for it, and a rule stated in two places was enforced in one. Found by reverting each half
    /// separately, which is the only way that kind of gap shows up: the name said "never
    /// remembered" and the test only checked "never reported".</para>
    /// </summary>
    [Theory]
    [InlineData("run_command")]
    [InlineData("run_powershell")]
    [InlineData("git")]
    [InlineData("RUN_COMMAND")]
    public void A_shell_is_never_remembered(string shell)
    {
        var workspace = Workspace("shell");
        var file = Path.Combine(_root, "permissions.json");

        _store.Approve(workspace, shell);

        Assert.False(_store.Approves(workspace, shell), "A shell was reported as approved.");

        var written = File.Exists(file) ? File.ReadAllText(file) : "";
        Assert.DoesNotContain(shell, written, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And an entry an EARLIER build wrote stops being honoured, without being deleted. The
    /// reversible way round: the file still says what it said, and nothing acts on it.
    /// </summary>
    [Fact]
    public void A_shell_written_by_an_older_build_stops_being_honoured()
    {
        var workspace = Workspace("legacy-shell");
        var key = WorkspaceInfo.IdFor(workspace).ToString("N");
        var file = Path.Combine(_root, "permissions.json");

        File.WriteAllText(file, $$"""{"{{key}}":["read_file","run_command"]}""");

        Assert.True(_store.Approves(workspace, "read_file"), "The rest of the file stopped being read.");
        Assert.False(_store.Approves(workspace, "run_command"),
            "A shell entry left by an older build was still honoured.");
    }

    // ── Rule 3: a legacy in-workspace file is ignored, never imported ───────

    /// <summary>
    /// The file the agent could write. It is noticed - so a UI can say why the person is being
    /// asked again - and it grants nothing.
    /// </summary>
    [Fact]
    public void A_permissions_file_inside_the_workspace_grants_nothing()
    {
        var workspace = Workspace("planted");
        Directory.CreateDirectory(Path.Combine(workspace, WorkspaceGuard.ReservedFolder));
        File.WriteAllText(
            Path.Combine(workspace, WorkspaceGuard.ReservedFolder, "permissions.json"),
            """{"whatever":["read_file","write_file","run_command"]}""");

        Assert.True(ApprovalStore.HasLegacyFile(workspace), "The legacy file was not even noticed.");

        Assert.False(_store.Approves(workspace, "read_file"),
            "A permissions file planted inside the workspace granted an approval.");
        Assert.False(_store.Approves(workspace, "run_command"),
            "A permissions file planted inside the workspace granted a shell.");
    }

    /// <summary>
    /// An unreadable record means NO approvals. Failing the other way would hand out standing
    /// permission because a file was corrupt.
    /// </summary>
    [Fact]
    public void A_corrupt_record_grants_nothing()
    {
        var workspace = Workspace("corrupt");
        File.WriteAllText(Path.Combine(_root, "permissions.json"), "{ this is not json");

        Assert.False(_store.Approves(workspace, "read_file"));
    }
}
