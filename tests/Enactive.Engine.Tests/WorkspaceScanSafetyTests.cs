namespace Enactive.Engine.Tests;

using Enactive.Workspace;
using Xunit;

public sealed class WorkspaceScanSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_junction_cycle_and_external_target_are_not_measured(bool hasFile)
    {
        using var fx = new EngineFixture();
        using var outside = new EngineFixture();
        if (hasFile) fx.Write("inside.txt", "inside");
        outside.Write("private.txt", "outside");
        var cycle = fx.PathOf("cycle");
        var external = fx.PathOf("external");
        Assert.True(WorkspaceGuardTests.TryLinkDirectory(cycle, fx.Root));
        try
        {
            Assert.True(WorkspaceGuardTests.TryLinkDirectory(external, outside.Root));
            try
            {
                using var changes = new WorkspaceChanges(fx.Root);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var snapshot = await changes.TakeAsync(deadline.Token).WaitAsync(deadline.Token);
                Assert.NotNull(snapshot?.Files);
                if (hasFile) Assert.Equal("inside.txt", Assert.Single(snapshot.Files).Key);
                else Assert.Empty(snapshot.Files);
                var paths = await changes.PathsAsync(snapshot, default);
                if (hasFile) Assert.Equal("inside.txt", Assert.Single(paths!));
                else Assert.Empty(paths!);
            }
            finally { Directory.Delete(external); }
        }
        finally { Directory.Delete(cycle); }
    }

    [Fact]
    public async Task Empty_directories_count_toward_the_limit_and_never_return_a_partial_snapshot()
    {
        using var fx = new EngineFixture();
        fx.Write("inside.txt", "inside");
        for (var i = 0; i < 7; i++) Directory.CreateDirectory(fx.PathOf($"empty-{i}"));
        var exact = await WorkspaceChanges.ScanAsync(fx.Root, default, maxDirectories: 8);
        Assert.NotNull(exact);
        Assert.Equal("inside.txt", Assert.Single(exact).Key);
        Directory.CreateDirectory(fx.PathOf("one-too-many"));
        Assert.Null(await WorkspaceChanges.ScanAsync(fx.Root, default, maxDirectories: 8));
    }

    [Fact]
    public async Task Cancellation_interrupts_hashing_and_a_later_scan_can_complete()
    {
        using var fx = new EngineFixture();
        var block = new byte[512 * 1024];
        for (var i = 0; i < 128; i++) await File.WriteAllBytesAsync(fx.PathOf($"file-{i}.bin"), block);
        using var cancellation = new CancellationTokenSource();
        var scan = WorkspaceChanges.ScanAsync(fx.Root, cancellation.Token);
        Assert.False(scan.IsCompleted, "The cancellation fixture must interrupt an active scan.");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scan);
        var next = await WorkspaceChanges.ScanAsync(fx.Root, default);
        Assert.NotNull(next);
        Assert.Equal(128, next.Count);
        Assert.All(next.Values, value => Assert.NotNull(value.Hash));
    }

    [Fact]
    public async Task Cancelled_snapshot_does_not_poison_the_session_gate()
    {
        using var fx = new EngineFixture();
        fx.Write("file.txt", "content");
        using var changes = new WorkspaceChanges(fx.Root);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => changes.TakeAsync(new CancellationToken(true)));
        var snapshot = await changes.TakeAsync(default);
        Assert.NotNull(snapshot?.Files);
        Assert.Equal("file.txt", Assert.Single(snapshot.Files).Key);
    }
}
