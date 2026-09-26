namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Core.Artifacts;
using Enactive.Tools;
using Xunit;

public sealed class AppendJournalTests
{
    [Fact]
    public async Task Append_records_length_without_copying_the_old_file_and_can_be_undone()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "base\n");
        var scope = fx.Artifacts.BeginStep();
        var result = await fx.Invoke(new WriteFileTool(),
            """{"path":"a.txt","append":true,"content":"tail"}""", scope);
        Assert.True(result.Success, result.Error);
        var write = Assert.Single(fx.Artifacts.Writes);
        Assert.Equal(5, write.AppendBeforeLength);
        Assert.Null(write.BackupPath);
        Assert.True(scope.CanRestore("a.txt"));
        Assert.Equal("base\ntail", fx.Read("a.txt"));
        Assert.Contains("a.txt", (await scope.RevertAsync(["a.txt"], default)).Reverted);
        Assert.Equal("base\n", fx.Read("a.txt"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Append_followed_by_replace_or_delete_keeps_the_original_prefix(bool delete)
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "base\n");
        var scope = fx.Artifacts.BeginStep();
        await fx.Invoke(new WriteFileTool(), """{"path":"a.txt","append":true,"content":"tail"}""", scope);
        if (delete) await scope.RemoveAsync("a.txt", default);
        else await scope.CreateAsync("a.txt", ArtifactKind.FileSet, "a",
            s => s.WriteAsync(Encoding.UTF8.GetBytes("replacement")).AsTask(), default);
        Assert.NotNull(fx.Artifacts.Writes[0].BackupPath);
        Assert.Contains("a.txt", (await scope.RevertAsync(["a.txt"], default)).Reverted);
        Assert.Equal("base\n", fx.Read("a.txt"));
    }

    [Fact]
    public async Task Replace_then_append_reverts_to_the_original_backup()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "original");
        var scope = fx.Artifacts.BeginStep();
        await scope.CreateAsync("a.txt", ArtifactKind.FileSet, "a",
            s => s.WriteAsync(Encoding.UTF8.GetBytes("replacement")).AsTask(), default);
        await fx.Invoke(new WriteFileTool(), """{"path":"a.txt","append":true,"content":"tail"}""", scope);
        Assert.Contains("a.txt", (await scope.RevertAsync(["a.txt"], default)).Reverted);
        Assert.Equal("original", fx.Read("a.txt"));
    }

    [Fact]
    public async Task External_prefix_edit_is_not_discarded_even_after_a_second_append()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "base\n");
        var scope = fx.Artifacts.BeginStep();
        await fx.Invoke(new WriteFileTool(), """{"path":"a.txt","append":true,"content":"one"}""", scope);
        fx.Write("a.txt", "EDIT\none");
        await fx.Invoke(new WriteFileTool(), """{"path":"a.txt","append":true,"content":"two"}""", scope);
        Assert.Contains("a.txt", (await scope.RevertAsync(["a.txt"], default)).Kept);
        Assert.Equal("EDIT\none\ntwo", fx.Read("a.txt"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_preparation_leaves_no_change_or_new_file(bool existed)
    {
        using var fx = new EngineFixture();
        if (existed) fx.Write("a.txt", "base");
        await Assert.ThrowsAsync<IOException>(() => fx.Artifacts.AppendAsync("a.txt", ArtifactKind.FileSet, "a",
            async (original, tail) =>
            {
                if (original is not null) Assert.False(original.CanWrite);
                await tail.WriteAsync(new byte[1024]);
                throw new IOException("prepare failed");
            }, default));
        Assert.Empty(fx.Artifacts.Writes);
        Assert.Equal(existed, fx.Exists("a.txt"));
        if (existed) Assert.Equal("base", fx.Read("a.txt"));
    }

    [Fact]
    public async Task Sibling_work_blocks_reverting_an_earlier_append()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "base");
        var first = fx.Artifacts.BeginStep();
        var second = fx.Artifacts.BeginStep();
        await fx.Invoke(new WriteFileTool(), """{"path":"a.txt","append":true,"content":"one"}""", first);
        await fx.Invoke(new WriteFileTool(), """{"path":"a.txt","append":true,"content":"two"}""", second);
        Assert.Contains("a.txt", (await first.RevertAsync(["a.txt"], default)).Kept);
        Assert.Contains("a.txt", (await second.RevertAsync(["a.txt"], default)).Reverted);
        Assert.Equal("base\none", fx.Read("a.txt"));
        Assert.Contains("a.txt", (await first.RevertAsync(["a.txt"], default)).Reverted);
        Assert.Equal("base", fx.Read("a.txt"));
    }
}
