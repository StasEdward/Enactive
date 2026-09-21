namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Xunit;

/// <summary>
/// What a workspace gets before anything is asked of it.
///
/// <para>Two things, both of them about a folder the engine puts there itself: the agent's
/// working area has to exist, because the worker is told in every prompt that it has one; and
/// <c>.enactive/</c> has to be out of the person's next commit, because we created it and they
/// did not.</para>
/// </summary>
public sealed class WorkspaceSetupTests
{
    private static string Ignore(EngineFixture fx) => Path.Combine(fx.Root, ".gitignore");

    // ── the working area ────────────────────────────────────────────────────

    /// <summary>
    /// It used to appear only when something wrote into it, which left every instruction about it
    /// false until the moment it stopped mattering. An agent looked for it with <c>list_dir</c>,
    /// with <c>dir /b /s</c> and with <c>Get-ChildItem</c> on three separate days.
    /// </summary>
    [Fact]
    public void Preparing_a_workspace_makes_the_working_area()
    {
        using var fx = new EngineFixture();

        Assert.False(Directory.Exists(ScratchArea.PathIn(fx.Root)));

        WorkspaceSetup.Prepare(fx.Root);

        Assert.True(Directory.Exists(ScratchArea.PathIn(fx.Root)));
    }

    [Fact]
    public void Preparing_twice_is_the_same_as_preparing_once()
    {
        using var fx = new EngineFixture();

        WorkspaceSetup.Prepare(fx.Root);
        File.WriteAllText(Path.Combine(ScratchArea.PathIn(fx.Root), "mine.txt"), "kept");
        WorkspaceSetup.Prepare(fx.Root);

        Assert.Equal("kept", File.ReadAllText(Path.Combine(ScratchArea.PathIn(fx.Root), "mine.txt")));
    }

    // ── the ignore file ─────────────────────────────────────────────────────

    /// <summary>
    /// ONLY IF THEY KEEP ONE. Creating a .gitignore in a repository that chose not to have one is
    /// a decision about that project, and not ours.
    /// </summary>
    [Fact]
    public void A_project_with_no_ignore_file_is_not_given_one()
    {
        using var fx = new EngineFixture();

        WorkspaceSetup.Prepare(fx.Root);

        Assert.False(File.Exists(Ignore(fx)));
    }

    /// <summary>
    /// And it only ever APPENDS. The file is somebody's, often with comments and an order that
    /// means something, and it goes into their next diff.
    /// </summary>
    [Fact]
    public void The_entry_is_appended_and_everything_else_is_left_alone()
    {
        using var fx = new EngineFixture();
        var before = "# build output\nbin/\nobj/\n";
        File.WriteAllText(Ignore(fx), before);

        Assert.True(WorkspaceSetup.Ignore(fx.Root));

        var after = File.ReadAllText(Ignore(fx));
        Assert.StartsWith(before, after, StringComparison.Ordinal);
        Assert.Contains(WorkspaceSetup.Entry, after, StringComparison.Ordinal);

        // And it says what it is, because an unexplained line in somebody's .gitignore is a line
        // they will wonder about and eventually delete.
        Assert.Contains("Enactive", after, StringComparison.Ordinal);
    }

    /// <summary>A file that does not end in a newline does not get its last line glued to ours.</summary>
    [Fact]
    public void A_file_with_no_final_newline_still_ends_up_readable()
    {
        using var fx = new EngineFixture();
        File.WriteAllText(Ignore(fx), "bin/");

        WorkspaceSetup.Ignore(fx.Root);

        var lines = File.ReadAllLines(Ignore(fx));
        Assert.Equal("bin/", lines[0]);
        Assert.Contains(WorkspaceSetup.Entry, lines);
    }

    /// <summary>
    /// Every spelling git accepts for this folder counts as already done. Appending a second
    /// entry because the first was written differently is the kind of edit that makes people
    /// stop trusting a tool with their files.
    /// </summary>
    [Theory]
    [InlineData(".enactive")]
    [InlineData(".enactive/")]
    [InlineData("/.enactive")]
    [InlineData("/.enactive/")]
    [InlineData("  .enactive/  ")]
    [InlineData(".ENACTIVE/")]
    [InlineData(".enactive/   # ours")]
    public void An_entry_already_there_in_any_spelling_is_left_as_it_is(string existing)
    {
        using var fx = new EngineFixture();
        File.WriteAllText(Ignore(fx), $"bin/\n{existing}\n");

        Assert.False(WorkspaceSetup.Ignore(fx.Root));
        Assert.Equal($"bin/\n{existing}\n", File.ReadAllText(Ignore(fx)));
    }

    /// <summary>
    /// A negation is somebody saying the opposite ON PURPOSE. It is left alone rather than argued
    /// with — and only when it is about this folder.
    /// </summary>
    [Fact]
    public void Somebody_who_un_ignored_it_deliberately_is_not_overruled()
    {
        using var fx = new EngineFixture();
        File.WriteAllText(Ignore(fx), "!.enactive\n");

        Assert.False(WorkspaceSetup.Ignore(fx.Root));
    }

    [Fact]
    public void A_negation_about_something_else_does_not_stand_in_for_ours()
    {
        using var fx = new EngineFixture();
        File.WriteAllText(Ignore(fx), "*.log\n!important.log\n");

        Assert.True(WorkspaceSetup.Ignore(fx.Root));
    }

    /// <summary>A commented-out entry is not an entry.</summary>
    [Fact]
    public void A_comment_that_mentions_it_is_not_an_entry()
    {
        using var fx = new EngineFixture();
        File.WriteAllText(Ignore(fx), "# .enactive/ used to be here\n");

        Assert.True(WorkspaceSetup.Ignore(fx.Root));
    }

    /// <summary>
    /// None of this may fail a run. It happens on the way to doing what the person asked for.
    /// </summary>
    [Fact]
    public void Nothing_here_throws_on_a_path_that_makes_no_sense()
    {
        WorkspaceSetup.Prepare("");
        WorkspaceSetup.Prepare("   ");
        Assert.False(WorkspaceSetup.Ignore(Path.Combine(Path.GetTempPath(), "no-such-workspace-" + Guid.NewGuid())));
    }
}
