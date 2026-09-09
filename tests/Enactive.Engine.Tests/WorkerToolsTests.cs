namespace Enactive.Engine.Tests;

using Enactive.Agents;
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
}
