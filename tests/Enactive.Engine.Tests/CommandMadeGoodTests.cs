namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Xunit;

/// <summary>
/// A failed command, the fix, and the verification — through the engine, not through the rule.
///
/// <para><c>ShellOperationTests</c> checks what a command line NAMES. This checks that naming it
/// reaches the thing that decides a run's outcome, which is a different question and the one that
/// was actually wrong: the identity rule can be perfect and still be wired to nothing.</para>
///
/// <para>The shape is the reported run of 2026-09-20, reduced to what made it fail. A command
/// fails because the thing it needs is not there; the step creates it; the step verifies with the
/// same command spelled differently. That run wrote 63 passing tests and came out Incomplete.</para>
/// </summary>
[Collection("processes")]
public sealed class CommandMadeGoodTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"make it work"}""";

    private static string Cmd(string command)
        => $$"""{"command": {{System.Text.Json.JsonSerializer.Serialize(command)}} }""";

    private static string Write(string path, string content)
        => $$"""{"path": {{System.Text.Json.JsonSerializer.Serialize(path)}}, "content": {{System.Text.Json.JsonSerializer.Serialize(content)}} }""";

    // The SHIPPING developer role, not a hand-written allowlist. The harness's default worker
    // has no run_powershell, so the cross-shell test's recovery was refused by the role and
    // opened a second failure - the test failed for a reason that had nothing to do with what
    // it was asking. WorkerWith's own remark says why: "a tool missing from a SHIPPING role is
    // invisible to it by construction".

    private static string Outcome(IEnumerable<WorkEvent> events)
        => events.LastOrDefault(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed)
                 ?.Kind.ToString() ?? "(neither)";

    /// <summary>
    /// The reported case. `type probe.txt 2>&amp;1` fails because the file is not there; the step
    /// writes it; the step verifies with `type probe.txt`, which is the same operation wearing no
    /// redirection. Before this, the failure stayed open and the run was Incomplete over work that
    /// had been done.
    /// </summary>
    [Fact]
    public async Task A_command_verified_under_a_different_spelling_closes_its_failure()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run_command", Cmd("type probe.txt 2>&1"), "c1"),
            Turn.Calls1("write_file", Write("probe.txt", "here now"), "w1"),
            Turn.Calls1("run_command", Cmd("type probe.txt"), "c2"),
            Turn.Says("The file was missing; I created it and read it back."));

        var events = await fx.RunAsync(fx.Build(provider, worker: EngineFixture.Role("developer")), "make it work");

        Assert.Equal("TaskCompleted", Outcome(events));
    }

    /// <summary>
    /// The guard against it, and the whole risk of loosening the rule: a success against a
    /// DIFFERENT target must not clear the failure. Same program, same subcommand, another
    /// operand — the step never made good what it broke, and the run has to say so.
    /// </summary>
    [Fact]
    public async Task A_success_against_another_target_leaves_the_failure_open()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fx = new EngineFixture();
        fx.Write("other.txt", "a different file entirely");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run_command", Cmd("type probe.txt 2>&1"), "c1"),
            Turn.Calls1("run_command", Cmd("type other.txt"), "c2"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, worker: EngineFixture.Role("developer")), "make it work");

        Assert.Equal("TaskFailed", Outcome(events));
    }

    /// <summary>
    /// And the rule is about the OPERATION, not the tool: the agent is told to prefer
    /// run_powershell for pipes and switches between the two freely, so a fix verified through
    /// the other shell is still the fix.
    /// </summary>
    [Fact]
    public async Task A_failure_in_one_shell_is_made_good_in_the_other()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run_command", Cmd("type probe.txt 2>&1"), "c1"),
            Turn.Calls1("write_file", Write("probe.txt", "here now"), "w1"),
            Turn.Calls1("run_powershell", """{"script": "type probe.txt"}""", "p1"),
            Turn.Says("Created and verified."));

        var events = await fx.RunAsync(fx.Build(provider, worker: EngineFixture.Role("developer")), "make it work");

        Assert.Equal("TaskCompleted", Outcome(events));
    }

    /// <summary>
    /// A failure nobody ever came back to is still a failure. Without this the loosening could be
    /// read as "commands stopped counting", which is the opposite of what it does.
    /// </summary>
    [Fact]
    public async Task A_failure_never_returned_to_still_fails_the_run()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run_command", Cmd("type probe.txt 2>&1"), "c1"),
            Turn.Calls1("write_file", Write("probe.txt", "here now"), "w1"),
            Turn.Says("I created the file."));

        var events = await fx.RunAsync(fx.Build(provider, worker: EngineFixture.Role("developer")), "make it work");

        Assert.Equal("TaskFailed", Outcome(events));
    }
}
