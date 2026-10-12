namespace Enactive.Server.Tests;

using System.Diagnostics;
using Enactive.Server.Runs;

/// <summary>
/// The folders the service runs tasks in for the people in its browser (server.json WorkspaceRoots): under the roots,
/// in fact and not only by name.
/// </summary>
public sealed class WorkspaceRootsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-roots", Guid.NewGuid().ToString("N"));
    private readonly string _work;

    public WorkspaceRootsTests()
    {
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(Path.Combine(_work, "project"));
        Directory.CreateDirectory(Path.Combine(_root, "elsewhere"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    [Fact]
    public void A_folder_under_a_root_is_one_and_one_outside_is_not()
    {
        var roots = new WorkspaceRoots([_work]);

        Assert.Null(roots.Refusal(Path.Combine(_work, "project")));
        Assert.Null(roots.Refusal(_work));
        Assert.Contains("is not under", roots.Refusal(Path.Combine(_root, "elsewhere")));
        Assert.Contains("is not under", roots.Refusal(_work + "-sibling"));
        Assert.Contains("is not under", roots.Refusal(Path.Combine(_work, "..", "elsewhere")));
    }

    /// <summary>
    /// A drive named as a root holds every folder on it. Its full path keeps its separator ("D:\"), and the check for
    /// "under it" added another, so no folder on the drive was under it - every run and every folder refused.
    /// </summary>
    [Fact]
    public void A_drive_named_as_a_root_holds_the_folders_on_it()
    {
        var roots = new WorkspaceRoots([Path.GetPathRoot(_work)!]);

        Assert.Null(roots.Refusal(_work));
        Assert.Contains(Path.Combine(_work, "project"), roots.Below(_work));
    }

    /// <summary>No roots, no runs from the browser: a box to type any path into is every folder one Run from a shell.</summary>
    [Fact]
    public void With_no_roots_no_folder_is_one()
        => Assert.Contains("names none", new WorkspaceRoots([]).Refusal(Path.Combine(_work, "project")));

    /// <summary>A junction under the root to a folder outside it is inside by name and outside in fact.</summary>
    [Fact]
    public void A_folder_reached_through_a_junction_is_not_one_and_is_not_offered()
    {
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_work, "escape")}\" \"{Path.Combine(_root, "elsewhere")}\"")
               { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!)
            mklink.WaitForExit();
        var roots = new WorkspaceRoots([_work]);

        Assert.Contains("is a link", roots.Refusal(Path.Combine(_work, "escape")));
        Assert.Equal([Path.Combine(_work, "project")], roots.Below(_work));
    }

    [Fact]
    public void Nothing_is_listed_below_a_folder_outside_the_roots()
        => Assert.Empty(new WorkspaceRoots([_work]).Below(_root));
}
