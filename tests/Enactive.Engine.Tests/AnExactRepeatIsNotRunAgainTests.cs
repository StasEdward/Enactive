namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Tools;
using Xunit;

/// <summary>Shell effects are unknown: retries run, while the independent stall guard stays bounded.</summary>
public sealed class AnExactRepeatIsNotRunAgainTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"run it"}""";

    // ── the measured case ────────────────────────────────────────────────────

    [Fact]
    public async Task An_exact_repeat_of_an_unknown_shell_command_is_executed()
    {
        using var fx = new EngineFixture();
        fx.Write("marker.txt", "");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run_command", """{"command":"echo hit>>marker.txt"}""", "c1"),
            Turn.Calls1("run_command", """{"command":"echo hit>>marker.txt"}""", "c2"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "run it");

        // Unknown shell effects cannot justify refusing a retry.
        var lines = fx.Read("marker.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        Assert.DoesNotContain(events, e => e.Kind == EventKind.DecisionResolved
                                     && e.Summary.Contains("refused — an exact repeat", StringComparison.Ordinal));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>The escape hatch: force:true runs it anyway.</summary>
    [Fact]
    public async Task Force_true_runs_the_exact_repeat_anyway()
    {
        using var fx = new EngineFixture();
        fx.Write("marker.txt", "");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run_command", """{"command":"echo hit>>marker.txt"}""", "c1"),
            Turn.Calls1("run_command", """{"command":"echo hit>>marker.txt","force":true}""", "c2"),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "run it");

        var lines = fx.Read("marker.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }

    /// <summary>THE BOUNDARY. A write in between means the workspace may have changed: the repeat runs normally.</summary>
    [Fact]
    public async Task A_write_in_between_lets_the_same_command_run_again()
    {
        using var fx = new EngineFixture();
        fx.Write("marker.txt", "");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run_command", """{"command":"echo hit>>marker.txt"}""", "c1"),
            Turn.Calls1("write_file", """{"path":"note.txt","content":"something changed"}""", "w1"),
            Turn.Calls1("run_command", """{"command":"echo hit>>marker.txt"}""", "c2"),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "run it");

        var lines = fx.Read("marker.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_write_in_the_same_batch_allows_a_repeat_that_observes_the_new_file(bool repeatNextTurn)
    {
        using var fx = new EngineFixture();
        fx.Write("marker.txt", "");
        const string command = """{"command":"if exist note.txt (type note.txt>>marker.txt) else (echo missing>>marker.txt)"}""";
        var turns = new List<Turn>
        {
            Turn.Says(QuickAction),
            Turn.Calls1("run_command", command, "c1"),
            new(Calls: new[]
            {
                new ToolCall("w1", "write_file", """{"path":"note.txt","content":"changed\n"}"""),
                new ToolCall("c2", "run_command", command)
            })
        };
        if (repeatNextTurn)
            turns.Add(Turn.Calls1("run_command", command, "c3"));
        turns.Add(Turn.Says("Done."));
        var provider = new FakeChatProvider(turns.ToArray());

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "run it");

        Assert.Equal(repeatNextTurn ? new[] { "missing", "changed", "changed" } : new[] { "missing", "changed" },
            fx.Read("marker.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Equal(0, events.Count(e => e.Kind == EventKind.DecisionResolved
            && e.Summary.Contains("refused — an exact repeat", StringComparison.Ordinal)));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    [Fact]
    public async Task An_unknown_shell_repeat_before_a_write_is_allowed()
    {
        using var fx = new EngineFixture();
        fx.Write("marker.txt", "");
        const string command = """{"command":"echo hit>>marker.txt"}""";
        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run_command", command, "c1"),
            new Turn(Calls: new[]
            {
                new ToolCall("c2", "run_command", command),
                new ToolCall("w1", "write_file", """{"path":"note.txt","content":"changed"}""")
            }),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "run it");

        Assert.Equal(2, fx.Read("marker.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal("changed", fx.Read("note.txt"));
        Assert.DoesNotContain(events, e => e.Kind == EventKind.DecisionResolved
            && e.Summary.Contains("refused — an exact repeat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_shell_repeat_after_a_failed_write_is_allowed()
    {
        using var fx = new EngineFixture();
        fx.Write("marker.txt", "");
        const string command = """{"command":"echo hit>>marker.txt"}""";
        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run_command", command, "c1"),
            new Turn(Calls: new[]
            {
                new ToolCall("w1", "write_file", """{"path":"note.txt"}"""),
                new ToolCall("c2", "run_command", command)
            }),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "run it");

        Assert.Equal(2, fx.Read("marker.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains(events, e => e.Kind == EventKind.ToolResult
            && e.Summary.Contains("write_file -> failed", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Kind == EventKind.DecisionResolved
            && e.Summary.Contains("refused — an exact repeat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Successes_in_the_current_batch_do_not_block_calls_in_that_batch()
    {
        using var fx = new EngineFixture();
        fx.Write("marker.txt", "");
        const string command = """{"command":"echo hit>>marker.txt"}""";
        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            new Turn(Calls: new[]
            {
                new ToolCall("c1", "run_command", command),
                new ToolCall("c2", "run_command", command)
            }),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "run it");

        Assert.Equal(2, fx.Read("marker.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.DecisionResolved
            && e.Summary.Contains("refused — an exact repeat", StringComparison.Ordinal));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>THE BOUNDARY. A DIFFERENT command in between is not itself blocked - only the literal repeat is.</summary>
    [Fact]
    public async Task A_different_command_in_between_is_not_blocked()
    {
        using var fx = new EngineFixture();
        fx.Write("marker.txt", "");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run_command", """{"command":"echo hit>>marker.txt"}""", "c1"),
            Turn.Calls1("run_command", """{"command":"echo other>>other.txt"}""", "c2"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "run it");

        Assert.DoesNotContain(events, e => e.Kind == EventKind.DecisionResolved
                                           && e.Summary.Contains("refused — an exact repeat", StringComparison.Ordinal));
        Assert.Contains("other", fx.Read("other.txt"));
    }

    /// <summary>run_powershell is covered the same way.</summary>
    [Fact]
    public async Task An_exact_repeat_of_unknown_powershell_is_executed()
    {
        using var fx = new EngineFixture();
        fx.Write("marker.txt", "");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run_powershell", """{"script":"Add-Content marker.txt hit"}""", "c1"),
            Turn.Calls1("run_powershell", """{"script":"Add-Content marker.txt hit"}""", "c2"),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "run it");

        var lines = fx.Read("marker.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }

    /// <summary>THE BOUNDARY. A read-only file tool is untouched by this gate - only slow, process-spawning tools are.</summary>
    [Fact]
    public async Task Read_file_repeated_exactly_is_not_blocked()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "content");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("read_file", """{"path":"a.md"}""", "r1"),
            Turn.Calls1("read_file", """{"path":"a.md"}""", "r2"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "run it");

        Assert.DoesNotContain(events, e => e.Kind == EventKind.DecisionResolved
                                           && e.Summary.Contains("refused — an exact repeat", StringComparison.Ordinal));
    }
}
