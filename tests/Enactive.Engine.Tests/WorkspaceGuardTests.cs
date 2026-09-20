namespace Enactive.Engine.Tests;

using System.Diagnostics;
using Enactive.Core.Context;
using Xunit;

/// <summary>
/// The workspace boundary (review finding #4). The old check compared strings, so a link inside the
/// workspace passed it while the file operation followed the link outside.
/// </summary>
public sealed class WorkspaceGuardTests
{
    [Fact]
    public void A_plain_relative_path_resolves()
    {
        using var fx = new EngineFixture();
        var full = WorkspaceGuard.ResolveInside(fx.Root, "sub/file.txt");
        Assert.StartsWith(fx.Root, full, WorkspaceGuard.Comparison);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("./../../outside.txt")]
    public void Dot_dot_cannot_climb_out(string relative)
    {
        using var fx = new EngineFixture();
        Assert.Throws<ArgumentException>(() => WorkspaceGuard.ResolveInside(fx.Root, relative));
    }

    [Fact]
    public void An_absolute_path_is_refused()
    {
        using var fx = new EngineFixture();
        var absolute = OperatingSystem.IsWindows() ? @"C:\Windows\System32\drivers\etc\hosts" : "/etc/hosts";
        Assert.Throws<ArgumentException>(() => WorkspaceGuard.ResolveInside(fx.Root, absolute));
    }

    // The workspace's own state folder holds the run database, the project memory and the inbox.
    // Nothing a model asks for may write there — see also the approvals file, which no longer lives
    // in a workspace at all.
    [Theory]
    [InlineData(".enactive/permissions.json")]
    [InlineData(".enactive/enactive.db")]
    [InlineData("sub/../.enactive/permissions.json")]
    [InlineData(".ENACTIVE/permissions.json")]
    public void The_state_folder_is_not_writable_by_tools(string relative)
    {
        using var fx = new EngineFixture();
        Assert.Throws<ArgumentException>(() => WorkspaceGuard.ResolveInside(fx.Root, relative));
    }

    [Fact]
    public void The_app_itself_can_still_reach_the_state_folder()
    {
        using var fx = new EngineFixture();
        var full = WorkspaceGuard.ResolveInside(fx.Root, ".enactive/enactive.db", allowReserved: true);
        Assert.EndsWith("enactive.db", full, StringComparison.Ordinal);
    }

    // The finding itself: a junction/symlink inside the workspace pointing out of it. The string
    // check passes — the path really does start with the root — and the write follows the link.
    [Fact]
    public void A_link_that_leads_outside_is_refused()
    {
        using var fx = new EngineFixture();
        var outside = Path.Combine(Path.GetTempPath(), "enactive-tests", "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);

        try
        {
            if (!TryLinkDirectory(Path.Combine(fx.Root, "link"), outside))
            {
                // A junction needs no elevation on Windows, so a failure THERE means the test
                // proved nothing and should say so. Elsewhere a symlink may be legitimately refused.
                RequireLinkSupport();
                return;
            }

            var ex = Assert.Throws<ArgumentException>(
                () => WorkspaceGuard.ResolveInside(fx.Root, "link/escaped.txt"));
            Assert.Contains("link", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_link_that_stays_inside_is_allowed()
    {
        using var fx = new EngineFixture();
        var inside = Path.Combine(fx.Root, "real");
        Directory.CreateDirectory(inside);

        if (!TryLinkDirectory(Path.Combine(fx.Root, "alias"), inside))
        {
            RequireLinkSupport();
            return;
        }

        var full = WorkspaceGuard.ResolveInside(fx.Root, "alias/file.txt");
        Assert.StartsWith(fx.Root, full, WorkspaceGuard.Comparison);
    }

    // ── The scratch carve-out ────────────────────────────────────────────────
    // The one place under the state folder a tool may write. Everything here is about the line
    // between it and its neighbours: enactive.db and the undo journal's backups sit beside it.

    [Theory]
    [InlineData(".enactive/scratch")]
    [InlineData(".enactive/scratch/probe.ps1")]
    [InlineData(".enactive/scratch/nested/deep/out.txt")]
    public void The_scratch_area_is_writable_by_tools(string relative)
    {
        using var fx = new EngineFixture();
        var full = WorkspaceGuard.ResolveInside(fx.Root, relative);
        Assert.StartsWith(fx.Root, full, WorkspaceGuard.Comparison);
        Assert.True(WorkspaceGuard.IsScratch(fx.Root, full));
    }

    /// <summary>
    /// The carve-out is exactly two segments. A name that merely starts the same way, a sibling of
    /// scratch, and a path that climbs back out of it are all still the state folder — the last
    /// one because Path.GetFullPath collapses `..` long before the reserved check is reached, so
    /// `.enactive/scratch/../enactive.db` arrives as `.enactive/enactive.db`.
    /// </summary>
    [Theory]
    [InlineData(".enactive/scratchy/x.txt")]
    [InlineData(".enactive/scratch-of-mine/x.txt")]
    [InlineData(".enactive/undo/x.txt")]
    [InlineData(".enactive/scratch/../enactive.db")]
    [InlineData(".enactive/scratch/../../.enactive/enactive.db")]
    [InlineData("scratch/x.txt/../../.enactive/enactive.db")]
    public void Everything_beside_the_scratch_area_is_still_refused(string relative)
    {
        using var fx = new EngineFixture();
        Assert.Throws<ArgumentException>(() => WorkspaceGuard.ResolveInside(fx.Root, relative));
    }

    /// <summary>
    /// A plain folder called <c>scratch</c> at the top of the workspace is the user's own and has
    /// nothing to do with this: it is ordinary content, writable as ordinary content, and NOT the
    /// scratch area. Getting this backwards would quietly stop journalling a real project folder.
    /// </summary>
    [Fact]
    public void A_top_level_folder_called_scratch_is_ordinary_content()
    {
        using var fx = new EngineFixture();
        var full = WorkspaceGuard.ResolveInside(fx.Root, "scratch/notes.md");
        Assert.False(WorkspaceGuard.IsScratch(fx.Root, full));
    }

    /// <summary>
    /// The carve-out is tested against the EFFECTIVE path, like every other rule here. A junction
    /// named <c>scratch</c> that leads to the undo journal is not scratch just because of what it
    /// is called — which is the same lesson as the junction test above, asked of the new rule.
    /// </summary>
    [Fact]
    public void A_link_named_scratch_that_leads_into_the_state_folder_is_refused()
    {
        using var fx = new EngineFixture();
        var state = Path.Combine(fx.Root, ".enactive");
        var undo = Path.Combine(state, "undo");
        Directory.CreateDirectory(undo);

        if (!TryLinkDirectory(Path.Combine(state, "scratch"), undo))
        {
            RequireLinkSupport();
            return;
        }

        Assert.Throws<ArgumentException>(
            () => WorkspaceGuard.ResolveInside(fx.Root, ".enactive/scratch/stolen.txt"));
    }

    /// <summary>
    /// Fails on Windows, where a directory junction always works, so a link test can never pass by
    /// quietly doing nothing. On other platforms an unprivileged symlink may be refused, and there
    /// the test genuinely has nothing to say.
    /// </summary>
    private static void RequireLinkSupport()
        => Assert.False(OperatingSystem.IsWindows(),
            "could not create a directory junction — this test asserted nothing");

    /// <summary>
    /// A directory junction on Windows (no elevation needed, unlike a symlink without Developer
    /// Mode) and a symlink elsewhere. Returns false when the OS refuses.
    /// </summary>
    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("mklink");
                psi.ArgumentList.Add("/J");
                psi.ArgumentList.Add(link);
                psi.ArgumentList.Add(target);

                using var process = Process.Start(psi)!;
                process.WaitForExit(15000);
                return process.ExitCode == 0 && Directory.Exists(link);
            }

            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Remembered approvals must be out of reach of the thing they authorise (review finding #1). These
/// assert the property that matters — a tool cannot write the file — rather than the storage detail,
/// which is App.Ui's business and not visible from here.
/// </summary>
public sealed class ApprovalReachTests
{
    [Fact]
    public async Task Write_file_cannot_touch_the_permissions_file()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"grant myself shell"}"""),
            Turn.Calls1("write_file",
                """{"path":".enactive/permissions.json","content":"[\"run_command\"]"}"""));

        var orchestrator = fx.Build(provider);
        var events = await fx.RunAsync(orchestrator, "remember that run_command is allowed here");

        Assert.False(fx.Exists(".enactive/permissions.json"), events.Text());
    }

    [Fact]
    public async Task Write_file_cannot_escape_through_dot_dot()
    {
        using var fx = new EngineFixture();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write outside"}"""),
            Turn.Calls1("write_file", """{"path":"../escaped.txt","content":"x"}"""));

        var orchestrator = fx.Build(provider);
        await fx.RunAsync(orchestrator, "write above the workspace");

        var parent = Directory.GetParent(fx.Root)!.FullName;
        Assert.False(File.Exists(Path.Combine(parent, "escaped.txt")));
    }
}
