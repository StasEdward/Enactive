namespace Enactive.Engine.Tests;

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Tools;
using Xunit;
using Xunit.Abstractions;

public sealed class ShortPathGuardTests(ITestOutputHelper output)
{
    [WindowsFact]
    public async Task Short_state_alias_cannot_read_or_write_existing_or_new_state()
    {
        using var fx = new EngineFixture();
        fx.Write(".enactive/private.txt", "secret");
        var alias = Short(fx.PathOf(".enactive"));
        Assert.NotEqual(".enactive", Path.GetFileName(alias));
        output.WriteLine($"Actual short alias: {alias}");
        output.WriteLine($"GetFullPath: {Path.GetFullPath(alias)}");
        Assert.Equal(fx.PathOf(".enactive"), Path.GetFullPath(alias), ignoreCase: true);
        var relative = Path.GetFileName(alias);
        Assert.Throws<ReservedPathException>(() => WorkspaceGuard.ResolveInside(fx.Root, relative + "/private.txt"));
        Assert.Throws<ReservedPathException>(() => WorkspaceGuard.ResolveInside(fx.Root, relative + "/new/child.txt"));
        var read = await fx.Invoke(new ReadFileTool(), JsonSerializer.Serialize(new { path = relative + "/private.txt" }));
        Assert.DoesNotContain("secret", read.Output ?? "");
        var batch = await fx.Invoke(new ReadFilesTool(), JsonSerializer.Serialize(new { paths = new[] { relative + "/private.txt" } }));
        Assert.DoesNotContain("secret", batch.Output ?? "");
        var listing = await fx.Invoke(new ListDirectoryTool(), JsonSerializer.Serialize(new { path = relative }));
        Assert.DoesNotContain("private.txt", listing.Output ?? "");
        var write = await fx.Invoke(new WriteFileTool(), JsonSerializer.Serialize(new { path = relative + "/new.txt", content = "bad" }));
        Assert.False(write.Success);
        var overwrite = await fx.Invoke(new WriteFileTool(), JsonSerializer.Serialize(new { path = relative + "/private.txt", content = "bad" }));
        Assert.False(overwrite.Success);
        Assert.False(fx.Exists(".enactive/new.txt"));
        Assert.Equal("secret", fx.Read(".enactive/private.txt"));
    }

    [WindowsFact]
    public void Short_junction_alias_cannot_reach_state()
    {
        using var fx = new EngineFixture();
        fx.Write(".enactive/private.txt", "secret");
        var link = fx.PathOf("long junction alias");
        Assert.True(WorkspaceGuardTests.TryLinkDirectory(link, Short(fx.PathOf(".enactive"))));
        try
        {
            var alias = Path.GetFileName(Short(link));
            Assert.NotEqual("long junction alias", alias);
            Assert.Throws<ReservedPathException>(() => WorkspaceGuard.ResolveInside(fx.Root, alias + "/private.txt"));
            Assert.Throws<ReservedPathException>(() => WorkspaceGuard.ResolveInside(fx.Root, alias + "/new/child.txt"));
        }
        finally { Directory.Delete(link); }
    }

    [WindowsFact]
    public void Short_root_and_ordinary_aliases_have_the_same_identity_and_scratch_stays_usable()
    {
        using var fx = new EngineFixture();
        fx.Write("long directory name/long filename.txt", "ok");
        fx.Write(".enactive/scratch/existing.txt", "ok");
        var shortRoot = Short(fx.Root);
        var file = Short(fx.PathOf("long directory name/long filename.txt"));
        var relative = Path.GetRelativePath(shortRoot, file);
        var resolved = WorkspaceGuard.ResolveInside(shortRoot, relative);
        Assert.Equal("long directory name/long filename.txt", WorkspaceGuard.KeyFor(shortRoot, resolved));
        var state = Path.GetFileName(Short(fx.PathOf(".enactive")));
        var scratch = WorkspaceGuard.ResolveInside(shortRoot, state + "/scratch/new/sub.txt");
        Assert.True(WorkspaceGuard.IsScratch(shortRoot, scratch));
        Assert.Throws<ReservedPathException>(() => WorkspaceGuard.ResolveInside(shortRoot, state + "/private.txt"));
    }

    private static string Short(string path)
    {
        var buffer = new StringBuilder(32768);
        var length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        Assert.True(length > 0 && length < buffer.Capacity, $"GetShortPathName failed: {Marshal.GetLastWin32Error()}. These Windows regressions require an NTFS volume with short names enabled.");
        return buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string path, StringBuilder buffer, uint capacity);
}
