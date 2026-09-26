namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

public sealed class AutomaticGitSafetyTests
{
    [Theory]
    [InlineData(".enactive::$INDEX_ALLOCATION/state.txt")]
    [InlineData("file.txt::$DATA")]
    [InlineData(".enactive/scratch/file:secret")]
    public void Stream_syntax_cannot_alias_a_guarded_file_on_Windows(string path)
    {
        using var fx = new EngineFixture();
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<ArgumentException>(() => WorkspaceGuard.ResolveInside(fx.Root, path));
            Assert.Throws<ArgumentException>(() => WorkspaceGuard.ResolveInside(fx.Root, path, allowReserved: true));
        }
        else Assert.Contains(':', WorkspaceGuard.ResolveInside(fx.Root, path));
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".git/config")]
    [InlineData("nested/.git/config")]
    [InlineData(".enactive/scratch/.git/config")]
    public void Git_metadata_is_reserved_even_in_scratch(string path)
    {
        using var fx = new EngineFixture();
        Assert.Throws<ArgumentException>(() => WorkspaceGuard.ResolveInside(fx.Root, path));
        Assert.NotNull(WorkspaceGuard.ResolveInside(fx.Root, "normal.txt"));
    }

    [Theory]
    [InlineData("fsmonitor", false)]
    [InlineData("filter", false)]
    [InlineData("filter", true)]
    [InlineData("diff", false)]
    public async Task Automatic_observation_never_executes_repository_helpers(string kind, bool include)
    {
        using var fx = new EngineFixture();
        var init = await fx.Invoke(new GitTool(), """{"args":["init","-q"]}""");
        Assert.True(init.Success, init.Error);
        File.WriteAllText(fx.PathOf("probe.sh"), "#!/bin/sh\necho executed > canary.txt\ncat\n");
        File.WriteAllText(fx.PathOf("file.txt"), "original\n");
        File.WriteAllText(fx.PathOf(".gitattributes"), "*.txt filter=probe diff=probe\n");
        var config = kind switch
        {
            "fsmonitor" => "[core]\n fsmonitor = sh ./probe.sh\n",
            "filter" => "[filter \"probe\"]\n clean = sh ./probe.sh\n required = true\n",
            _ => "[diff \"probe\"]\n textconv = sh ./probe.sh\n"
        };
        var configFile = fx.PathOf(".git/config");
        if (include)
        {
            File.WriteAllText(fx.PathOf(".git/included"), config);
            config = "[include]\n path = included\n";
        }
        File.AppendAllText(configFile, config);
        var originalConfig = File.ReadAllBytes(configFile);
        using var changes = new WorkspaceChanges(fx.Root);
        var before = await changes.TakeAsync(default);
        Assert.NotNull(before);
        if (kind != "fsmonitor") Assert.NotNull(before.Files); // filters/drivers cause FS fallback
        File.WriteAllText(fx.PathOf("file.txt"), "changed\n");
        var after = await changes.TakeAsync(default);
        Assert.NotNull(after);
        Assert.Contains((await changes.CompareAsync(before, after, default))!, c => c.Path == "file.txt");
        await new ContextProvider(fx.Workspace).BuildAsync(new(fx.Workspace.Id), default);
        Assert.False(File.Exists(fx.PathOf("canary.txt")));
        Assert.Equal(originalConfig, File.ReadAllBytes(configFile));
        Assert.False(File.Exists(fx.PathOf(".git/index"))); // only the private snapshot index may change
    }

    [Fact]
    public async Task Cancellation_is_not_converted_into_a_successful_empty_snapshot()
    {
        using var fx = new EngineFixture();
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutomaticGit.RunAsync(fx.Root, ["status"], ct.Token));
    }
}
