namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Workers;
using Xunit;

/// <summary>
/// A worker's own autonomy level has to narrow what a run may do without asking.
///
/// <c>Worker.DefaultLevel</c> was read when settings loaded, shown in the role editor, and then
/// dropped: the run used the workspace policy alone, and the tool context was handed
/// <c>PermissionPolicy.PermissiveDefault</c> outright. A role deliberately set to Observe could
/// still write files unprompted. The rule here is one-directional — a role may only ever ask for
/// LESS than the workspace grants, never more.
/// </summary>
public sealed class WorkerAutonomyTests
{
    private const string WriteAFile =
        """{"path":"note.md","content":"written by the agent"}""";

    private static FakeChatProvider Writer()
        => new(
            Turn.Says("""{"disposition":"quick_action","title":"write a note"}"""),
            Turn.Calls1("write_file", WriteAFile, "c1"),
            Turn.Says("Wrote it."))
        {
            WhenExhausted = Turn.Says("done")
        };

    private static FakeChatProvider Reader()
        => new(
            Turn.Says("""{"disposition":"quick_action","title":"list the folder"}"""),
            Turn.Calls1("list_dir", """{"path":"."}""", "c1"),
            Turn.Says("Listed it."))
        {
            WhenExhausted = Turn.Says("done")
        };

    private static Worker RoleAt(PermissionLevel level)
        => new("developer", "Developer", "You are a developer.",
               new[] { "write_file", "read_file", "list_dir", "run_command" },
               level,
               new ModelPolicy(new ModelRef("fake", "fake-model")));

    [Fact]
    public async Task An_Observe_role_must_ask_before_writing_a_file()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(Writer(), worker: RoleAt(PermissionLevel.Observe));
        await fx.RunAsync(orchestrator, "write a note");

        Assert.Contains(fx.Decisions.Requests, r =>
            string.Equals(r.Subject, "write_file", StringComparison.OrdinalIgnoreCase));
    }

    // The same role, doing something within its level, must not be interrupted — narrowing has to
    // gate the calls above the line and nothing else.
    [Fact]
    public async Task An_Observe_role_still_reads_without_asking()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(Reader(), worker: RoleAt(PermissionLevel.Observe));
        await fx.RunAsync(orchestrator, "list the folder");

        Assert.DoesNotContain(fx.Decisions.Requests, r =>
            string.Equals(r.Subject, "list_dir", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_Execute_role_writes_without_asking()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(Writer(), worker: RoleAt(PermissionLevel.Execute));
        await fx.RunAsync(orchestrator, "write a note");

        Assert.DoesNotContain(fx.Decisions.Requests, r =>
            string.Equals(r.Subject, "write_file", StringComparison.OrdinalIgnoreCase));
    }

    // The role can only ever lower the ceiling. An Autonomous role inside an Observe workspace is
    // still an Observe run — otherwise a role definition would be a way around the workspace policy.
    [Fact]
    public async Task An_Autonomous_role_cannot_raise_an_Observe_workspace()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(
            Writer(),
            worker: RoleAt(PermissionLevel.Autonomous),
            policy: new PermissionPolicy(
                PermissionLevel.Observe, new[] { "*" }, Array.Empty<string>()));

        await fx.RunAsync(orchestrator, "write a note");

        Assert.Contains(fx.Decisions.Requests, r =>
            string.Equals(r.Subject, "write_file", StringComparison.OrdinalIgnoreCase));
    }

    // The workspace's own allow / ask-before lists must survive the narrowing: only the level moves.
    [Fact]
    public async Task An_explicit_ask_before_entry_still_asks_under_a_permissive_role()
    {
        using var fx = new EngineFixture();

        var orchestrator = fx.Build(
            Reader(),
            worker: RoleAt(PermissionLevel.Autonomous),
            policy: new PermissionPolicy(
                PermissionLevel.Autonomous, new[] { "*" }, new[] { "list_dir" }));

        await fx.RunAsync(orchestrator, "list the folder");

        Assert.Contains(fx.Decisions.Requests, r =>
            string.Equals(r.Subject, "list_dir", StringComparison.OrdinalIgnoreCase));
    }
}
