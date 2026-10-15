namespace Enactive.Engine.Tests;

using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Proposals kept in a folder (StagingArtifactStore's keepIn): they outlive the store that made them, and opened again
/// they are applied or rejected under the same rules as in memory - so a run from the service's browser can be staged,
/// and a closed tab or a restart does not lose what the history says is waiting.
/// </summary>
public sealed class KeptStagingTests
{
    private static string Kept(EngineFixture fx) => Path.Combine(fx.Root, ".enactive", "staging", "run");

    [Fact]
    public async Task Proposals_outlive_their_store_and_are_applied_later()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "old\n");
        using (var store = new StagingArtifactStore(fx.Root, Kept(fx)))
        {
            Assert.True((await fx.Invoke(new WriteFileTool(), """{"path":"notes.md","content":"new\n"}""", store)).Success);
            Assert.True((await fx.Invoke(new WriteFileTool(), """{"path":"added.md","content":"added\n"}""", store)).Success);
        }
        Assert.Equal("old\n", File.ReadAllText(fx.PathOf("notes.md")));

        using (var reopened = StagingArtifactStore.Reopen(fx.Root, Kept(fx)))
        {
            Assert.Equal(["notes.md", "added.md"], reopened.Changes.Select(c => c.RelativePath));
            Assert.Equal("old\n", reopened.Changes[0].OldContent);
            Assert.True(reopened.Apply(reopened.Changes[0].Id).Applied);
            Assert.True(reopened.Reject(reopened.Changes[1].Id).Rejected);
        }

        Assert.Equal("new\n", File.ReadAllText(fx.PathOf("notes.md")));
        Assert.False(File.Exists(fx.PathOf("added.md")));
        // What became of each is kept too: nothing applied twice, nothing rejected offered again.
        using var again = StagingArtifactStore.Reopen(fx.Root, Kept(fx));
        Assert.True(again.Changes[0].Applied);
        Assert.True(again.Changes[1].Rejected);
        Assert.Empty(again.PendingPaths);
    }

    /// <summary>The file was edited after the proposal was made: applying it later would erase that edit.</summary>
    [Fact]
    public async Task Opened_again_a_proposal_whose_file_changed_since_is_not_applied()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "old\n");
        using (var store = new StagingArtifactStore(fx.Root, Kept(fx)))
            await fx.Invoke(new WriteFileTool(), """{"path":"notes.md","content":"new\n"}""", store);
        fx.Write("notes.md", "edited by hand\n");

        using var reopened = StagingArtifactStore.Reopen(fx.Root, Kept(fx));
        var result = reopened.Apply(reopened.Changes.Single().Id);

        Assert.False(result.Applied);
        Assert.Contains("changed on disk", result.Conflict);
        Assert.Equal("edited by hand\n", File.ReadAllText(fx.PathOf("notes.md")));
    }

    /// <summary>A chain of proposals for one file keeps its order and its lineage across a reopen.</summary>
    [Fact]
    public async Task Opened_again_a_later_proposal_still_waits_for_the_earlier_one()
    {
        using var fx = new EngineFixture();
        fx.Write("notes.md", "one\n");
        using (var store = new StagingArtifactStore(fx.Root, Kept(fx)))
        {
            await fx.Invoke(new WriteFileTool(), """{"path":"notes.md","content":"two\n"}""", store);
            await fx.Invoke(new WriteFileTool(), """{"path":"notes.md","content":"three\n"}""", store);
        }

        using var reopened = StagingArtifactStore.Reopen(fx.Root, Kept(fx));
        var (first, second) = (reopened.Changes[0], reopened.Changes[1]);

        Assert.False(reopened.Apply(second.Id).Applied);
        Assert.False(reopened.Reject(first.Id).Rejected);
        Assert.True(reopened.Apply(first.Id).Applied);
        Assert.True(reopened.Apply(second.Id).Applied);
        Assert.Equal("three\n", File.ReadAllText(fx.PathOf("notes.md")));
    }

    /// <summary>A manifest pointing outside its folder is not one a store wrote, and is not followed.</summary>
    [Fact]
    public void A_manifest_naming_a_file_outside_its_folder_is_refused()
    {
        using var fx = new EngineFixture();
        Directory.CreateDirectory(Kept(fx));
        fx.Write("secret.txt", "s");
        File.WriteAllText(Path.Combine(Kept(fx), "proposals.json"),
            $$"""[{"Id":"{{Guid.NewGuid()}}","ParentId":null,"RelativePath":"x.md","Key":"x.md","Sequence":0,"Scope":-1,"BaseHash":null,"Old":null,"New":"..\\..\\..\\secret.txt","Applied":false,"Rejected":false}]""");

        Assert.Throws<IOException>(() => StagingArtifactStore.Reopen(fx.Root, Kept(fx)));
    }
}
