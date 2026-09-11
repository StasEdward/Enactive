namespace Enactive.Engine.Tests;

using Enactive.Core.Memory;
using Xunit;

/// <summary>
/// Found by Stas on 2026-09-11, reading the assembled prompt in his own exported log and asking
/// what the block headed "What this project has already decided and done" was FOR. In a workspace
/// rooted at ArcMapConditions.App it held, among twenty entries:
///
/// <code>
/// - [outcome] "Запусти калькулятор" — Completed
/// - [outcome] "запусти Total Commander" — Incomplete: unresolved tool call: run_command …
/// - [decision] git: denied      (six times)
/// </code>
///
/// <para>He had been using the chat window as a console. The chat window belongs to whichever
/// workspace is open, so the app filed his errands as things ArcMapConditions had decided and
/// done — and those entries then ride in the prompt of every step of the next twenty runs.</para>
///
/// <para>The rule is NOT "is this related to the project", which cannot be answered without
/// guessing at intent. It is <b>did the run touch the workspace</b>. §9ar.</para>
/// </summary>
public sealed class ProjectFactsTests
{
    private static readonly string[] Nothing = Array.Empty<string>();

    // ── the errands, which is what this is for ──────────────────────────────

    /// <summary>"Запусти калькулятор" — the shell ran, the workspace was never opened.</summary>
    [Fact]
    public void A_run_that_only_ran_a_shell_command_learned_nothing_about_the_project()
        => Assert.False(ProjectFacts.WorthRemembering(new[] { "run_command" }, Nothing));

    [Fact]
    public void The_other_shell_counts_no_differently()
        => Assert.False(ProjectFacts.WorthRemembering(new[] { "run_powershell" }, Nothing));

    /// <summary>
    /// The run that died on its first model call because Ollama was off (§9aq). It never reached a
    /// tool. A failure of the ENVIRONMENT is not a fact about the project, and falls out here
    /// rather than needing a rule of its own.
    /// </summary>
    [Fact]
    public void A_run_that_never_got_as_far_as_a_tool_is_not_a_fact_about_the_project()
        => Assert.False(ProjectFacts.WorthRemembering(Array.Empty<string>(), Nothing));

    // ── real project work, which must survive ───────────────────────────────

    /// <summary>
    /// The case the whole rule is shaped around: a run that only READ and concluded something
    /// produced real knowledge and no artifact at all. Filtering on "changed a file" is the
    /// obvious shortcut and would throw exactly this away.
    /// </summary>
    [Fact]
    public void A_run_that_only_read_the_workspace_is_worth_remembering()
        => Assert.True(ProjectFacts.WorthRemembering(new[] { "read_file" }, Nothing));

    [Fact]
    public void So_is_one_that_only_listed_it()
        => Assert.True(ProjectFacts.WorthRemembering(new[] { "list_dir" }, Nothing));

    /// <summary>git and docker can address nothing but the workspace, so using one IS reaching in.</summary>
    [Theory]
    [InlineData("git")]
    [InlineData("docker")]
    public void The_workspace_scoped_tools_count(string tool)
        => Assert.True(ProjectFacts.WorthRemembering(new[] { tool }, Nothing));

    /// <summary>
    /// "сходи на сайт … — Completed Changed: news2.md" was an errand too, and it is KEPT: it left a
    /// file in the project, and this entry is then the only surviving explanation of why that file
    /// is there.
    /// </summary>
    [Fact]
    public void An_errand_that_left_a_file_behind_is_kept()
        => Assert.True(ProjectFacts.WorthRemembering(new[] { "run_command" }, new[] { "news2.md" }));

    /// <summary>One reaching tool among many shells is enough — it reached.</summary>
    [Fact]
    public void One_tool_that_reached_in_is_enough()
        => Assert.True(ProjectFacts.WorthRemembering(
            new[] { "run_command", "run_powershell", "read_file" }, Nothing));

    // ── records written before the tool name was a value ────────────────────

    /// <summary>
    /// Null is UNKNOWN, not "none". A run whose events predate the tool name being carried as a
    /// value says nothing here, and saying nothing must not be read as saying no.
    /// </summary>
    [Fact]
    public void An_unnamed_tool_does_not_count_as_a_no_by_itself()
        => Assert.True(ProjectFacts.WorthRemembering(new string?[] { null, "read_file" }, Nothing));

    [Fact]
    public void Names_are_matched_without_regard_to_case_or_padding()
        => Assert.True(ProjectFacts.WorthRemembering(new[] { "  Read_File " }, Nothing));

    // ── a decision re-recorded is not a second decision ─────────────────────

    private static MemoryEntry Decision(string content)
        => new(Guid.NewGuid(), Guid.NewGuid(), MemoryKind.Decision, content, null, DateTimeOffset.Now);

    private static MemoryEntry Outcome(string content)
        => new(Guid.NewGuid(), Guid.NewGuid(), MemoryKind.Outcome, content, null, DateTimeOffset.Now);

    /// <summary>"git: denied" occupied six of the twenty places a run is given.</summary>
    [Fact]
    public void A_decision_already_in_memory_is_not_written_again()
        => Assert.True(ProjectFacts.AlreadyDecided(new[] { Decision("git: denied") }, "git: denied"));

    [Fact]
    public void A_decision_not_in_memory_is_written()
        => Assert.False(ProjectFacts.AlreadyDecided(new[] { Decision("git: denied") }, "docker: denied"));

    /// <summary>
    /// Two OUTCOMES with identical text are two real events — the same task run twice — and are
    /// both kept. Only decisions collapse, because a decision is a standing answer.
    /// </summary>
    [Fact]
    public void An_identical_outcome_is_not_a_duplicate_decision()
        => Assert.False(ProjectFacts.AlreadyDecided(new[] { Outcome("git: denied") }, "git: denied"));

    [Fact]
    public void Empty_memory_holds_no_duplicates()
        => Assert.False(ProjectFacts.AlreadyDecided(Array.Empty<MemoryEntry>(), "git: denied"));
}
