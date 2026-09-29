namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Providers;
using Enactive.Core.Workers;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// A tool the host registers and no role names is said out loud.
///
/// <para>The mirror of <see cref="McpReach"/>, and the side that has cost more. <c>Program.cs</c>
/// has carried the sentence for months without acting on it: <i>"A tool a role names but the host
/// does not register is the same defect as a tool the host registers and no role names, seen from
/// the other side."</i></para>
///
/// <para>Five times inside this registry — <c>edit_file</c>, <c>search_files</c>,
/// <c>create_directory</c>, <c>move_file</c>, <c>copy_file</c> — and once outside it,
/// <c>send_email</c>, which took three nights and an agent that read this project's own source to
/// build a mailer around the tool it was not offered. Every one of them was found by symptom.</para>
/// </summary>
public sealed class RegisteredAndNamedByNobodyTests
{
    private static string[][] Team(params string[][] roles) => roles;

    /// <summary>The reported case, in the shape the settings file had it.</summary>
    [Fact]
    public void A_tool_no_role_names_is_named()
    {
        var said = ToolReach.Unnamed(
            new[] { "read_file", "write_file", "run_command", "git", "docker", "send_email" },
            Team(
                new[] { "read_file", "write_file", "run_command", "git" },   // developer
                new[] { "read_file" },                                       // reviewer
                new[] { "read_file", "run_command", "git", "docker" }));     // ops

        Assert.NotNull(said);
        Assert.Contains("send_email", said, StringComparison.Ordinal);
        Assert.DoesNotContain("docker", said!, StringComparison.Ordinal);   // ops names it
        Assert.DoesNotContain("write_file", said!, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BOUNDARY, and the reason this is asked of the TEAM. A role lacking a tool is normal and
    /// usually deliberate — the writer may not run shells, the reviewer may not write — and warning
    /// about that every run would be noise nobody reads. Only a tool with no home at all is a
    /// defect.
    /// </summary>
    [Fact]
    public void A_role_that_lacks_a_tool_somebody_else_has_is_not_a_defect()
        => Assert.Null(ToolReach.Unnamed(
            new[] { "read_file", "write_file", "run_command" },
            Team(
                new[] { "read_file", "write_file", "run_command" },
                new[] { "read_file" },
                new[] { "read_file", "write_file" })));

    /// <summary>A role granted every tool carries every tool, so nothing is out of reach.</summary>
    [Fact]
    public void A_wildcard_role_reaches_everything()
        => Assert.Null(ToolReach.Unnamed(
            new[] { "read_file", "send_email", "docker" },
            Team(new[] { "read_file" }, new[] { "*" })));

    /// <summary>
    /// MCP tools belong to <see cref="McpReach"/>, which answers per SERVER with its own fix.
    /// Counting them here would say the same thing twice in different words, and send the reader
    /// to a different screen than the one that solves it.
    /// </summary>
    [Fact]
    public void Mcp_tools_are_the_other_checks_business()
    {
        var said = ToolReach.Unnamed(
            new[] { "read_file", "mcp__desktop-commander__read_file", "mcp__log-analyzer__analyze" },
            Team(new[] { "read_file" }));

        Assert.Null(said);
    }

    /// <summary>An empty team is not a clean bill of health.</summary>
    [Fact]
    public void Nobody_at_all_still_means_nobody()
    {
        var said = ToolReach.Unnamed(new[] { "read_file", "write_file" }, Team());

        Assert.NotNull(said);
        Assert.Contains("read_file", said, StringComparison.Ordinal);
        Assert.Contains("write_file", said!, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE ONE THAT WOULD HAVE CAUGHT ALL FIVE. Every tool this build registers is named by one
    /// of the roles it ships with - so a tool added to the host and forgotten in DefaultWorkers
    /// fails here, at the moment it is written, instead of months later when somebody notices the
    /// model working around a capability it was never told about.
    ///
    /// <para>Read against the real sets, not a list typed out here: a list would have to be kept
    /// in step with both, and it is the keeping in step that has failed five times.</para>
    /// </summary>
    [Fact]
    public void Our_own_defaults_leave_no_tool_stranded()
    {
        var registered = EngineFixture.ShippedTools().Select(t => t.Definition.Name);
        var roles = DefaultWorkers.Seed(new ModelRef("fake", "fake-model"))
                                  .Select(w => (IEnumerable<string>)w.ToolAllowlist);

        Assert.Null(ToolReach.Unnamed(registered, roles));
    }

    /// <summary>
    /// And it reaches a run. A check that is perfect and never emitted is the defect it exists to
    /// catch wearing a different hat - which is exactly what happened to the four tools above.
    /// </summary>
    [Fact]
    public async Task The_run_says_it()
    {
        using var fx = new EngineFixture();

        // A team of ONE, built by hand, because the fixture deliberately surrounds a test worker
        // with the shipped roles - and with those present nothing is stranded, which is what the
        // test above asserts.
        var alone = EngineFixture.WorkerWith("write_file", "read_file");

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says("""{"disposition":"quick_action","title":"write a note"}"""),
                    Turn.Calls1("write_file", """{"path":"note.txt","content":"hello"}"""),
                    Turn.Says("Written.")),
                alone,
                team: new[] { alone }),
            "write a note");

        Assert.Contains("registered and named by no role", events.Text(), StringComparison.Ordinal);
    }

    /// <summary>Once a run, not at every step: it is about the team (run fba4d6: eleven identical warnings in one run).</summary>
    [Fact]
    public async Task A_run_of_several_steps_says_it_once()
    {
        using var fx = new EngineFixture();
        var alone = EngineFixture.WorkerWith("write_file", "read_file");
        var worker = new ByStepChatProvider("""
            {"disposition":"task","title":"notes","steps":[{"title":"First note","dependsOn":[]},{"title":"Second note","dependsOn":[0]}]}
            """);

        var events = await fx.RunAsync(fx.Build(worker, alone, team: new[] { alone }), "write two notes");

        Assert.Single(events, e => e.Summary.Contains("registered and named by no role", StringComparison.Ordinal));
        Assert.Equal(2, events.Count(e => e.Kind == EventKind.StepCompleted));
    }
}
