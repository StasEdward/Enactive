namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The worker's own working area, <c>.enactive/scratch/</c>.
///
/// <para>Every path a tool touched had to be inside the workspace or be refused, so a helper
/// script, a scratch copy or a command's captured output had nowhere to go but into the user's
/// project - where it appears in <c>git status</c>, goes through staging, is judged by the
/// reviewer as part of the work, and is tracked by revert. A throwaway became a change.</para>
///
/// <para>These assert the four properties that make the area worth having, and they are about
/// CONSEQUENCES rather than about which branch was taken: the file is really there, it is not
/// among what the step changed, a rejection leaves it alone, and a script written to it can
/// actually be run. A scratch area that is merely a tidier folder would pass none of them.</para>
/// </summary>
public sealed class ScratchAreaTests
{
    private static string Write(string path, string content)
        => $$"""{"path": {{JsonSerializer.Serialize(path)}}, "content": {{JsonSerializer.Serialize(content)}} }""";

    private const string ScriptPath = ".enactive/scratch/probe.txt";

    /// <summary>
    /// The one that makes the area usable at all. Staging holds a proposal and puts nothing on
    /// disk until somebody presses Apply, so a helper script staged that way does not exist for
    /// the command written to run it - and there is nothing useful for a person to approve in the
    /// diff of a throwaway either.
    /// </summary>
    [Fact]
    public async Task A_scratch_file_is_on_disk_at_once_even_under_staging()
    {
        using var fx = new EngineFixture();
        var staging = new StagingArtifactStore(fx.Root);

        var result = await fx.Invoke(new WriteFileTool(), Write(ScriptPath, "hello"), staging);

        Assert.True(result.Success, result.Error);
        Assert.True(fx.Exists(ScriptPath), "the scratch file was staged instead of written");
        Assert.Equal("hello", fx.Read(ScriptPath));
        Assert.Empty(staging.Changes);
        Assert.Empty(staging.PendingPaths);
    }

    /// <summary>
    /// What the reviewer is shown as "what this step changed" is the scope's touched paths. A
    /// helper the agent wrote in order to do the job is not a change to the project, and judging
    /// it as one is how sound work gets rejected over its own scaffolding.
    /// </summary>
    [Fact]
    public async Task A_scratch_file_is_not_among_the_paths_the_step_changed()
    {
        using var fx = new EngineFixture();
        var scope = fx.Artifacts.BeginStep();

        await fx.Invoke(new WriteFileTool(), Write(ScriptPath, "helper"), scope);
        await fx.Invoke(new WriteFileTool(), Write("src/real.txt", "the work"), scope);

        Assert.Equal(new[] { "src/real.txt" }, scope.TouchedPaths);
    }

    /// <summary>
    /// A rejected step puts the workspace back. It must not put the worker's own notes back with
    /// it: the scratch file was never part of the proposal, so there is nothing there to undo.
    /// </summary>
    [Fact]
    public async Task Reverting_a_rejected_step_leaves_the_scratch_file_alone()
    {
        using var fx = new EngineFixture();
        var scope = fx.Artifacts.BeginStep();

        await fx.Invoke(new WriteFileTool(), Write(ScriptPath, "helper"), scope);
        await fx.Invoke(new WriteFileTool(), Write("src/real.txt", "the work"), scope);

        await scope.RevertAsync(new[] { "src/real.txt", ScriptPath }, CancellationToken.None);

        Assert.False(fx.Exists("src/real.txt"), "the step's own write survived its revert");
        Assert.True(fx.Exists(ScriptPath), "the revert reached into the worker's scratch area");
        Assert.Equal("helper", fx.Read(ScriptPath));
    }

    /// <summary>
    /// The round trip the honesty rules ask for: write it, then read it back. Without this the
    /// area is write-only, and a model told to verify its own writes cannot.
    /// </summary>
    [Fact]
    public async Task A_scratch_file_can_be_read_back()
    {
        using var fx = new EngineFixture();
        await fx.Invoke(new WriteFileTool(), Write(ScriptPath, "line one\nline two"));

        var read = await fx.Invoke(
            new ReadFileTool(), $$"""{"path": {{JsonSerializer.Serialize(ScriptPath)}} }""");

        Assert.True(read.Success, read.Error);
        Assert.Contains("line two", read.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole point, end to end: the agent writes a script and runs it. Named with a path
    /// separator rather than by bare name on purpose - a bare name is looked up through PATH and
    /// the current directory, and whether the current directory is searched depends on an
    /// environment variable the host may have set.
    /// </summary>
    [WindowsFact]
    public async Task A_script_written_to_scratch_can_be_run()
    {

        using var fx = new EngineFixture();
        await fx.Invoke(new WriteFileTool(),
            Write(".enactive/scratch/hello.cmd", "@echo off\r\necho ran-from-scratch\r\n"));

        var run = await fx.Invoke(
            new RunCommandTool(), """{"command": ".enactive\\scratch\\hello.cmd"}""");

        Assert.True(run.Success, run.Error);
        Assert.Contains("ran-from-scratch", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tidying up after itself is not a change either. Journalling a scratch deletion would put it
    /// in front of the reviewer as something the step removed, and make a rejected step restore a
    /// file nobody wanted back.
    /// </summary>
    [Fact]
    public async Task Deleting_a_scratch_file_is_not_a_change_either()
    {
        using var fx = new EngineFixture();
        var scope = fx.Artifacts.BeginStep();

        await fx.Invoke(new WriteFileTool(), Write(ScriptPath, "helper"), scope);
        var deleted = await fx.Invoke(
            new DeleteFileTool(), $$"""{"path": {{JsonSerializer.Serialize(ScriptPath)}} }""", scope);

        Assert.True(deleted.Success, deleted.Error);
        Assert.False(fx.Exists(ScriptPath));
        Assert.Empty(scope.TouchedPaths);
    }

    /// <summary>
    /// The reason the area is worth having for large output at all. A captured build log is the
    /// case the honesty rules now point at, and <c>read_file</c> returns 8000 characters at a
    /// time - half a megabyte of log is sixty calls. Searching it is the only usable way, so a
    /// search AIMED at the area has to work.
    /// </summary>
    [Fact]
    public async Task A_search_aimed_at_the_scratch_area_finds_what_is_there()
    {
        using var fx = new EngineFixture();
        await fx.Invoke(new WriteFileTool(),
            Write(".enactive/scratch/build.log", "ok\nok\nerror CS1002: ; expected\nok"));

        var found = await fx.Invoke(new SearchFilesTool(),
            $$"""{"pattern": "error CS", "path": {{JsonSerializer.Serialize(WorkspaceGuard.ScratchPrefix)}} }""");

        Assert.True(found.Success, found.Error);
        Assert.Contains("CS1002", found.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the other half of the same rule: a sweep of the whole workspace leaves it out. A long
    /// captured log would otherwise spend the match and character caps on the worker's own notes
    /// and crowd out the hits in the code somebody asked about.
    /// </summary>
    [Fact]
    public async Task A_sweep_of_the_whole_workspace_leaves_the_scratch_area_out()
    {
        using var fx = new EngineFixture();
        await fx.Invoke(new WriteFileTool(), Write(".enactive/scratch/notes.txt", "needle"));
        await fx.Invoke(new WriteFileTool(), Write("src/real.txt", "needle"));

        var found = await fx.Invoke(new SearchFilesTool(), """{"pattern": "needle"}""");

        Assert.True(found.Success, found.Error);
        Assert.Contains("real.txt", found.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", found.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Staging cannot express a deletion, so <c>delete_file</c> is refused under it - but the
    /// scratch area was never staged, so there is no proposal there to be unable to express. An
    /// agent that may create a working file under staging must be able to clear it up again.
    /// </summary>
    [Fact]
    public async Task A_scratch_file_can_be_deleted_even_under_staging()
    {
        using var fx = new EngineFixture();
        var staging = new StagingArtifactStore(fx.Root);

        await fx.Invoke(new WriteFileTool(), Write(ScriptPath, "helper"), staging);
        var deleted = await fx.Invoke(
            new DeleteFileTool(), $$"""{"path": {{JsonSerializer.Serialize(ScriptPath)}} }""", staging);

        Assert.True(deleted.Success, deleted.Error);
        Assert.False(fx.Exists(ScriptPath));
    }

    /// <summary>
    /// The guard against that: the workspace proper is still untouchable under staging, and the
    /// refusal still explains itself rather than half-doing the deletion.
    /// </summary>
    [Fact]
    public async Task A_workspace_file_still_cannot_be_deleted_under_staging()
    {
        using var fx = new EngineFixture();
        var staging = new StagingArtifactStore(fx.Root);
        fx.Write("src/real.txt", "the work");

        var deleted = await fx.Invoke(
            new DeleteFileTool(), """{"path": "src/real.txt"}""", staging);

        Assert.False(deleted.Success);
        Assert.True(fx.Exists("src/real.txt"));
    }

    /// <summary>
    /// The run keeps a SECOND record of what was written, separate from the scope's touched paths:
    /// the artifact refs a tool hands back. That list is what the reviewer is given as "Files
    /// changed", what the run's closing line counts, and what the artifact panel shows. Keeping
    /// scratch out of the stores and not out of this was the leak - and the Code Review template,
    /// whose goal says "change nothing except the report", would have been shown a second changed
    /// file and could have failed a step over the agent's own notes.
    /// </summary>
    [Fact]
    public async Task A_scratch_write_is_not_an_artifact_of_the_run()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"use a helper"}"""),
            Turn.Calls1("write_file", """{"path":".enactive/scratch/helper.ps1","content":"echo hi"}""", "w1"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider), "use a helper");

        Assert.DoesNotContain(events, e => e.Kind == EventKind.ArtifactProduced);
        Assert.True(fx.Exists(".enactive/scratch/helper.ps1"), "the helper was not written at all");
    }

    /// <summary>The guard against that: a real write is still reported as one.</summary>
    [Fact]
    public async Task A_workspace_write_is_still_an_artifact_of_the_run()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write the note"}"""),
            Turn.Calls1("write_file", """{"path":"note.txt","content":"hello"}""", "w1"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider), "write a note");

        Assert.Contains(events, e => e.Kind == EventKind.ArtifactProduced);
    }

    /// <summary>
    /// The route the honesty rules now prescribe for output too long to come back: redirect into
    /// scratch, then copy_file into place. The copy is the deliverable, so it IS a change and must
    /// be reported as one — the source being scratch does not make the destination scratch.
    ///
    /// <para>copy_file rather than read_file plus write_file because that pair truncates: the read
    /// stops at 8000 characters and the "copy" comes out a fraction of the original, looking
    /// whole. That is why copy_file exists, and it is the same failure the old rule here caused by
    /// telling the agent to write_file a cut command result.</para>
    /// </summary>
    [Fact]
    public async Task A_long_output_saved_in_scratch_can_be_copied_into_place_whole()
    {
        using var fx = new EngineFixture();
        var scope = fx.Artifacts.BeginStep();

        // Comfortably past read_file's 8000-character window, so a read-then-write copy would lose
        // most of it and this test would catch that rather than pass by being small.
        var log = string.Join("\n", Enumerable.Range(1, 2000).Select(i => $"line {i} of the build log"));
        fx.Write(".enactive/scratch/build.log", log);

        var copied = await fx.Invoke(new CopyFileTool(),
            """{"from": ".enactive/scratch/build.log", "to": "report.log"}""", scope);

        Assert.True(copied.Success, copied.Error);
        Assert.Equal(log, fx.Read("report.log"));
        Assert.Equal(new[] { "report.log" }, scope.TouchedPaths);
    }

    /// <summary>
    /// The state folder beside it is still closed. This is the guard against the carve-out rather
    /// than for it: the run database and the undo journal's backups live one folder over.
    /// </summary>
    [Fact]
    public async Task The_state_folder_beside_it_is_still_refused()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new WriteFileTool(), Write(".enactive/enactive.db", "tampered"));

        Assert.False(result.Success);
        Assert.False(fx.Exists(".enactive/enactive.db"));
    }
}

/// <summary>
/// Clearing up after itself. Nothing ever removed anything from the scratch area, so a workspace
/// used for a month kept every helper script and every captured build log any run had written,
/// inside the user's project folder.
/// </summary>
public sealed class ScratchSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static string Put(EngineFixture fx, string relative, int daysAgo)
    {
        var full = Path.Combine(fx.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        File.SetLastWriteTimeUtc(full, Now.UtcDateTime.AddDays(-daysAgo));
        return full;
    }

    [Fact]
    public void What_an_earlier_run_left_a_fortnight_ago_goes()
    {
        using var fx = new EngineFixture();
        Put(fx, ".enactive/scratch/old.ps1", daysAgo: 14);

        Assert.Equal(1, ScratchArea.Sweep(fx.Root, Now));
        Assert.False(fx.Exists(".enactive/scratch/old.ps1"));
    }

    [Fact]
    public void What_this_run_is_using_stays()
    {
        using var fx = new EngineFixture();
        Put(fx, ".enactive/scratch/today.ps1", daysAgo: 0);

        Assert.Equal(0, ScratchArea.Sweep(fx.Root, Now));
        Assert.True(fx.Exists(".enactive/scratch/today.ps1"));
    }

    /// <summary>
    /// The reason age is taken from the newest thing INSIDE a folder rather than from the folder
    /// itself: a directory's own timestamp only moves when its immediate children change, so a
    /// tree being written deeper down looks stale and would be deleted while in use.
    /// </summary>
    [Fact]
    public void A_folder_whose_deep_contents_are_fresh_is_not_stale()
    {
        using var fx = new EngineFixture();
        Put(fx, ".enactive/scratch/tree/deep/fresh.txt", daysAgo: 0);
        Directory.SetLastWriteTimeUtc(
            Path.Combine(fx.Root, ".enactive", "scratch", "tree"), Now.UtcDateTime.AddDays(-30));

        Assert.Equal(0, ScratchArea.Sweep(fx.Root, Now));
        Assert.True(fx.Exists(".enactive/scratch/tree/deep/fresh.txt"));
    }

    [Fact]
    public void A_workspace_that_never_used_the_area_is_not_an_error()
    {
        using var fx = new EngineFixture();
        Assert.Equal(0, ScratchArea.Sweep(fx.Root, Now));
    }

    /// <summary>The guard against the sweep: it reaches nothing but its own folder.</summary>
    [Fact]
    public void It_touches_nothing_outside_the_scratch_area()
    {
        using var fx = new EngineFixture();
        Put(fx, "src/ancient.cs", daysAgo: 400);
        Put(fx, ".enactive/scratch/old.txt", daysAgo: 400);

        ScratchArea.Sweep(fx.Root, Now);

        Assert.True(fx.Exists("src/ancient.cs"), "the sweep reached into the workspace proper");
        Assert.False(fx.Exists(".enactive/scratch/old.txt"));
    }

    /// <summary>
    /// And it happens on its own, without a host remembering to call it: the first scratch write
    /// of a run sweeps what the last one left.
    /// </summary>
    [Fact]
    public async Task The_first_scratch_write_of_a_run_sweeps_what_the_last_one_left()
    {
        using var fx = new EngineFixture();
        Put(fx, ".enactive/scratch/last-week.ps1", daysAgo: 30);

        await fx.Invoke(new WriteFileTool(),
            $$"""{"path": ".enactive/scratch/now.ps1", "content": "fresh"}""");

        Assert.False(fx.Exists(".enactive/scratch/last-week.ps1"));
        Assert.True(fx.Exists(".enactive/scratch/now.ps1"));
    }
}

/// <summary>
/// The other half of the same one-line change in <c>search_files</c>, and a defect that was there
/// long before the scratch area: the skipped-folder names were matched against the ABSOLUTE path,
/// so they also matched segments of the workspace's own address.
///
/// <para>A project living under <c>C:\dev\packages\thing</c>, <c>~/bin/tool</c> or a checkout in a
/// folder called <c>dist</c> was therefore unsearchable - every file matched a skip rule on a
/// segment nobody was talking about, and every search answered "No matches in 0 file(s)". A wrong
/// answer stated as a fact, and indistinguishable from a real absence, which is precisely what the
/// skipped-file counters in that tool exist to prevent.</para>
/// </summary>
public sealed class SearchRootTests
{
    [Theory]
    [InlineData("packages")]
    [InlineData("bin")]
    [InlineData("dist")]
    [InlineData("node_modules")]
    public async Task A_workspace_living_under_a_skipped_name_is_still_searchable(string folder)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "enactive-tests", folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "thing.cs"), "class Needle { }");

        try
        {
            var workspace = WorkspaceInfo.For(root);
            var context = new Enactive.Core.Tools.ToolContext(
                TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: workspace.Id,
                Context: null!, PermissionPolicy: Enactive.Core.Permissions.PermissionPolicy.PermissiveDefault,
                WorkspaceRoot: root, Artifacts: new DiskArtifactStore(workspace));

            var found = await new SearchFilesTool().InvokeAsync(
                """{"pattern": "Needle"}""", context, CancellationToken.None);

            Assert.True(found.Success, found.Error);
            Assert.Contains("thing.cs", found.Output, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
