namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Tools a saved role should have had all along.
///
/// <para>A role's tool list is saved, so adding a tool to the built-in roles reaches a new
/// installation and nobody else. It happened to <c>edit_file</c>, was fixed by a migration, and
/// then happened again to <c>search_files</c>, <c>create_directory</c> and <c>move_file</c> - three
/// tools that were written, tested, documented and reachable by no one.</para>
///
/// <para>It surfaced as a task started from a phone that could not rename a file. With shells
/// denied for a remote run and <c>move_file</c> never granted, the model had nothing that could
/// move anything and stopped, correctly. The tool existed; the role had never been told.</para>
///
/// <para>These live here rather than beside the migration because the migration is in a WinExe no
/// test project can reference - and a settings migration is the last place to accept that, since it
/// runs once, on somebody's real file, and a mistake in it is silent.</para>
/// </summary>
public sealed class WorkerToolsTests
{
    [Theory]
    [InlineData("write_file", "edit_file")]
    [InlineData("write_file", "create_directory")]
    [InlineData("write_file", "move_file")]
    [InlineData("read_file", "search_files")]
    public void A_capability_the_worker_already_has_is_named(string has, string implied)
        => Assert.Contains(implied, WorkerTools.WithImplied([has]), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// copy_file needs BOTH halves, and that is the one entry where it matters. A worker that can
    /// read a file and write another can already copy one by hand - badly, because reading stops at
    /// 8000 characters and the copy comes out partial and looks whole. Naming the capability is
    /// therefore free.
    /// </summary>
    [Fact]
    public void Copying_follows_from_being_able_to_read_and_write()
        => Assert.Contains("copy_file", WorkerTools.WithImplied(["write_file", "read_file"]),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// And with only one half it is NOT granted. Taking the requirement loosely would let a worker
    /// that may write but not read duplicate content it is not allowed to see - the one way this
    /// table could widen access instead of naming what is already held.
    /// </summary>
    [Theory]
    [InlineData("write_file")]
    [InlineData("read_file")]
    public void Copying_is_not_granted_on_half_the_requirement(string half)
        => Assert.DoesNotContain("copy_file", WorkerTools.WithImplied([half]), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The developer role as it was actually saved before those three tools existed - the list that
    /// produced a run which could only rename a file with a shell.
    /// </summary>
    [Fact]
    public void The_role_that_could_not_rename_a_file_can_now()
    {
        string[] saved =
        [
            "write_file", "edit_file", "read_file", "list_dir",
            "run_command", "run_powershell", "git", "docker"
        ];

        var granted = WorkerTools.WithImplied(saved);

        Assert.Contains("move_file", granted, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("create_directory", granted, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("search_files", granted, StringComparer.OrdinalIgnoreCase);

        // Everything it had, it keeps. A migration that granted by replacing would be taking git and
        // docker away from somebody in the same breath.
        Assert.All(saved, tool => Assert.Contains(tool, granted, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Nothing is implied by nothing. The reviewer may read and list, and must come out of this
    /// still unable to change a file - it is the role whose whole definition is that it cannot.
    /// </summary>
    [Fact]
    public void A_read_only_role_gains_nothing_that_writes()
    {
        var granted = WorkerTools.WithImplied(["read_file", "list_dir"]);

        Assert.DoesNotContain("write_file", granted, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("edit_file", granted, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("move_file", granted, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("create_directory", granted, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// delete_file is not granted either, and for a different reason than the shells: nothing a
    /// worker already has removes a file. Overwriting destroys a file's contents and leaves the
    /// path. Handing every saved role a new power inside a migration would be a worse failure than
    /// the one this class fixes, so it stays a decision somebody makes in Settings.
    /// </summary>
    [Fact]
    public void Deleting_is_not_granted_to_anybody()
    {
        var granted = WorkerTools.WithImplied(["write_file", "edit_file", "read_file", "list_dir"]);

        Assert.DoesNotContain("delete_file", granted, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A shell is implied by nothing, and that is the point. Every entry in this table is a
    /// capability the worker already has under another name; handing the operating system a command
    /// line is not one of them, for any role, ever.
    /// </summary>
    [Fact]
    public void A_shell_is_never_granted()
    {
        var granted = WorkerTools.WithImplied(["write_file", "read_file", "list_dir"]);

        Assert.DoesNotContain("run_command", granted, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("run_powershell", granted, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Run twice is run once. A migration that is not idempotent can only be applied safely one
    /// time, and nobody ever finds out which time was the second.
    /// </summary>
    [Fact]
    public void Applying_it_twice_changes_nothing()
    {
        var once = WorkerTools.WithImplied(["write_file", "read_file"]);
        var twice = WorkerTools.WithImplied(once);

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// "*" already means every tool. Expanding it into names would make the list say less than it
    /// did, and would silently pin a role to the tools that existed today.
    /// </summary>
    [Fact]
    public void A_role_that_already_has_everything_is_left_alone()
        => Assert.Equal(["*"], WorkerTools.WithImplied(["*"]));

    /// <summary>The built-in roles are already complete, so this changes none of them.</summary>
    [Fact]
    public void The_built_in_roles_need_nothing_added()
    {
        foreach (var worker in DefaultWorkers.Seed(new("ollama", "any-model")))
        {
            Assert.Equal(worker.ToolAllowlist, WorkerTools.WithImplied(worker.ToolAllowlist));
        }
    }
    // ── what the settings editor may offer ────────────────────────────────────────────

    /// <summary>
    /// The decisive one, and the one that would have caught this class of bug three times now.
    ///
    /// <para>Every tool the host registers has to be tickable. The editor used to carry its own
    /// hand-written array, so a tool could be shipped, registered, documented and impossible to
    /// grant - which is what happened to create_directory, move_file, search_files, copy_file and
    /// delete_file. This asserts the list against the ACTUAL registered set rather than against a
    /// list written next to it, because two lists written by the same hand agree with each other
    /// and with nothing else.</para>
    /// </summary>
    [Fact]
    public void Every_tool_the_host_registers_can_be_granted()
    {
        var registered = new ToolRegistry(EngineFixture.ShippedTools())
            .Definitions.Select(d => d.Name).ToArray();

        var offerable = WorkerTools.Offerable(registered, []);

        foreach (var tool in registered)
            Assert.Contains(tool, offerable, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Including the one no migration hands out. delete_file is deliberately absent from the
    /// implication table, so the editor is the ONLY way anybody gets it - which made it the one
    /// tool that was completely unreachable.
    /// </summary>
    [Fact]
    public void Deleting_can_be_granted_even_though_it_is_never_implied()
    {
        var registered = new ToolRegistry(EngineFixture.ShippedTools())
            .Definitions.Select(d => d.Name).ToArray();

        Assert.DoesNotContain("delete_file", WorkerTools.WithImplied(["write_file", "read_file"]),
                              StringComparer.OrdinalIgnoreCase);
        Assert.Contains("delete_file", WorkerTools.Offerable(registered, []),
                        StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"*" is not a tool and is in no registry, so it has to be added on purpose.</summary>
    [Fact]
    public void The_wildcards_are_offered_too()
    {
        var offerable = WorkerTools.Offerable(["read_file"], []);

        Assert.Contains("*", offerable);
        Assert.Contains("mcp__*", offerable);
    }

    /// <summary>
    /// A tool this worker carries that the host does not register survives. Dropping it would
    /// disarm a role because somebody opened the dialog and pressed Save without touching anything.
    /// </summary>
    [Fact]
    public void A_tool_the_host_does_not_know_survives_the_round_trip()
        => Assert.Contains("mcp__weather__forecast",
                           WorkerTools.Offerable(["read_file"], ["read_file", "mcp__weather__forecast"]));

    /// <summary>And nothing is offered twice, whichever list it came from.</summary>
    [Fact]
    public void Nothing_is_offered_twice()
    {
        var offerable = WorkerTools.Offerable(["read_file", "write_file"], ["write_file", "*"]);

        Assert.Equal(offerable.Distinct(StringComparer.OrdinalIgnoreCase).Count(), offerable.Count);
    }

}
