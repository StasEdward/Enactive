namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Tools;
using Xunit;

/// <summary>
/// What the tools say when a model looks at <c>.enactive</c>.
///
/// <para><b>Measured twice on 2026-09-21</b>, in two live runs of two different requests. The
/// worker is told it has a working area at <c>.enactive/scratch/</c>, so it looks at
/// <c>.enactive</c> to find it — and <c>list_dir</c> answered <i>"'.enactive' holds the
/// workspace's own state and is not writable by tools"</i>, about a call that was only looking.
/// Worse than the wrong word: the refusal came back as a failed call, so it held the step open as
/// unfinished work. In one of the two runs it was one of the two unresolved calls that failed
/// it.</para>
///
/// <para>The third of this shape in two days, after <c>list_dir</c> on a file and
/// <c>search_files</c> on a file: a message that describes a problem the caller does not have.
/// The rule itself is not in question — a tool has no business in the undo journal — only what is
/// said about it, and what kind of result it is.</para>
/// </summary>
public sealed class LookingAtTheReservedFolderTests
{
    private static string Path(string p)
        => $$"""{"path": {{JsonSerializer.Serialize(p)}} }""";

    /// <summary>
    /// A read refused here has ANSWERED: the model asked whether it could look and was told no,
    /// definitively. Nothing is half-done and there is nothing to retry, so it must not hold the
    /// step open — the same rule a lookup that finds nothing already gets.
    /// </summary>
    [Theory]
    [InlineData(".enactive/runs")]
    [InlineData(".enactive/undo")]
    public async Task Looking_inside_the_state_folder_is_answered_not_failed(string path)
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new ListDirectoryTool(), Path(path));

        Assert.False(result.Success);
        Assert.True(result.IsAnswer, "a refusal to look is an answer, not unfinished work");
    }

    /// <summary>
    /// THE WAY IN HAS TO BE WALKABLE. The root listing shows <c>.enactive/</c>, the worker's
    /// instructions say its own area is underneath it, and opening the folder in between was
    /// refused — the door shown and then shut. So listing the state folder answers with the part
    /// of it that belongs to the model.
    ///
    /// <para>Nothing is granted that was not reachable already; what changes is that it can be
    /// found by looking instead of only by knowing.</para>
    /// </summary>
    [Theory]
    [InlineData(".enactive")]
    [InlineData(".enactive/")]
    [InlineData("./.enactive")]
    [InlineData(".ENACTIVE")]
    [InlineData("sub/../.enactive")]
    public async Task Listing_the_state_folder_shows_the_working_area(string path)
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new ListDirectoryTool(), Path(path));

        Assert.True(result.Success, result.Error);
        Assert.Contains(WorkspaceGuard.ScratchFolder + "/", result.Output ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// And it shows ONLY that. A listing that leaked the undo journal's filenames would be the
    /// refusal undone by the message meant to explain it.
    /// </summary>
    [Fact]
    public async Task Listing_the_state_folder_shows_nothing_else()
    {
        using var fx = new EngineFixture();
        fx.Write(".enactive/scratch/mine.txt", "ok");

        fx.Write(".enactive/runs/1.json", "{}");
        fx.Write(".enactive/undo/journal.json", "{}");

        var result = await fx.Invoke(new ListDirectoryTool(), Path(".enactive"));

        // The ENTRIES, not a substring of the tool's own explanation - which mentions the undo
        // journal by name in the course of saying it is out of bounds.
        Assert.Equal(new[] { WorkspaceGuard.ScratchFolder + "/" }, Entries(result.Output));
    }

    /// <summary>The root listing still shows the folder, which is how a model gets here at all.</summary>
    [Fact]
    public async Task The_root_still_shows_the_state_folder()
    {
        using var fx = new EngineFixture();
        fx.Write(".enactive/scratch/probe.txt", "hello");

        var result = await fx.Invoke(new ListDirectoryTool(), Path("."));

        Assert.Contains(WorkspaceGuard.ReservedFolder, result.Output ?? "", StringComparison.Ordinal);
    }

    /// <summary>And it says the true thing, including where the model's own area actually is.</summary>
    [Fact]
    public async Task It_names_the_working_area_instead_of_talking_about_writing()
    {
        using var fx = new EngineFixture();

        var text = (await fx.Invoke(new ListDirectoryTool(), Path(".enactive/runs"))).Error ?? "";

        Assert.Contains(WorkspaceGuard.ScratchPrefix, text, StringComparison.Ordinal);
        Assert.DoesNotContain("not writable", text, StringComparison.OrdinalIgnoreCase);

        // The parameter name belongs in a stack trace, not in an instruction to a model.
        Assert.DoesNotContain("Parameter", text, StringComparison.Ordinal);
    }

    /// <summary>read_file and search_files are the same question and get the same answer.</summary>
    [Fact]
    public async Task The_other_read_tools_answer_the_same_way()
    {
        using var fx = new EngineFixture();

        var read = await fx.Invoke(new ReadFileTool(), Path(".enactive/state.json"));
        var search = await fx.Invoke(
            new SearchFilesTool(), """{"pattern":"x","path":".enactive"}""");

        Assert.True(read.IsAnswer, "read_file");
        Assert.True(search.IsAnswer, "search_files");
    }

    /// <summary>
    /// THE ONE MY OWN TEST MISSED. The first version of this listing printed "scratch/" whatever
    /// was there, and every test of it wrote a file into the area first - so a listing that
    /// promised a folder <c>list_dir</c> would then refuse went unnoticed. That is the defect the
    /// carve-out was added to remove, reintroduced by the fix for it.
    ///
    /// <para>A run makes the area before the worker is told it has one
    /// (<c>ScratchArea.Ensure</c>), so in a real run it is there. A bare fixture has not run
    /// anything, which is exactly the state that caught this.</para>
    /// </summary>
    [Fact]
    public async Task An_area_that_is_not_there_yet_is_not_claimed_to_be()
    {
        using var fx = new EngineFixture();

        var listing = await fx.Invoke(new ListDirectoryTool(), Path(".enactive"));

        Assert.True(listing.Success, listing.Error);

        // The ENTRIES, not the sentence beneath them - which names the area's path in the course
        // of saying it is not there yet, and so contains the very word being looked for.
        Assert.Empty(Entries(listing.Output));
        Assert.Contains("create it", listing.Output ?? "", StringComparison.Ordinal);
    }

    /// <summary>And once a run has begun, it is there to be walked into.</summary>
    [Fact]
    public async Task A_run_makes_the_working_area_before_it_promises_one()
    {
        using var fx = new EngineFixture();

        ScratchArea.Ensure(fx.Root);

        Assert.True(Directory.Exists(ScratchArea.PathIn(fx.Root)));
        Assert.Equal(
            new[] { WorkspaceGuard.ScratchFolder + "/" },
            Entries((await fx.Invoke(new ListDirectoryTool(), Path(".enactive"))).Output));

        // And walking in works, which is the whole point of showing it.
        var inside = await fx.Invoke(new ListDirectoryTool(), Path(WorkspaceGuard.ScratchPrefix));
        Assert.True(inside.Success, inside.Error);
    }

    /// <summary>
    /// The carve-out still works, and is the whole reason a model looks in here: its own working
    /// area is inside the reserved folder and is ordinary.
    /// </summary>
    [Fact]
    public async Task The_working_area_itself_is_listed_normally()
    {
        using var fx = new EngineFixture();
        fx.Write(".enactive/scratch/probe.txt", "hello");

        var result = await fx.Invoke(new ListDirectoryTool(), Path(WorkspaceGuard.ScratchPrefix));

        Assert.True(result.Success, result.Error);
        Assert.Contains("probe.txt", result.Output ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A WRITE refused here is still work that did not happen, and still holds the step. That is
    /// the whole reason this is one exception type read and write can answer differently.
    /// </summary>
    [Fact]
    public async Task Writing_into_the_reserved_folder_is_still_a_failure()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(
            new WriteFileTool(), """{"path":".enactive/runs/forged.json","content":"{}"}""");

        Assert.False(result.Success);
        Assert.False(result.IsAnswer, "a write that did not happen is unfinished work");
    }

    /// <summary>
    /// The listed entries, without the sentence explaining what is NOT listed. One per line; the
    /// note is the only line that opens with a bracket.
    /// </summary>
    private static string[] Entries(string? output)
        => (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.Trim())
                         .Where(l => l.Length > 0 && !l.StartsWith('('))
                         .ToArray();
}
