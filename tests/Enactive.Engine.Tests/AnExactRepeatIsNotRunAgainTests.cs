namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Xunit;

/// <summary>
/// An exact repeat of a slow command - the same tool, the same arguments, nothing WRITTEN since it
/// last ran in this step - is refused before it is spawned, not run again and only afterwards told it
/// was pointless.
///
/// <para><b>Measured 2026-09-24, run 4f779e.</b> A step ran <c>dotnet test</c>, then three read-only
/// checks (two <c>git diff</c>, one <c>git status</c>) that themselves showed nothing had changed,
/// then ran the exact same <c>dotnet test</c> again - 1.7 seconds of wall time and a full test-output
/// reply for no new information. The existing stall guard already recorded this as "a call you have
/// already made in this step" and told the model so, AFTER the process had already run to
/// completion. This stops it before the process starts, for the tools slow enough to be worth not
/// paying for twice.</para>
/// </summary>
public sealed class AnExactRepeatIsNotRunAgainTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"run it"}""";

    private static string Said(FakeChatProvider provider)
        => string.Join("\n", provider.Requests.SelectMany(r => r.Messages).Select(m => m.Content));

    // ── the measured case ────────────────────────────────────────────────────

    [Fact]
    public async Task An_exact_repeat_of_run_command_is_refused_and_not_executed()
    {
        using var fx = new EngineFixture();
        fx.Write("marker.txt", "");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run_command", """{"command":"echo hit>>marker.txt"}""", "c1"),
            Turn.Calls1("run_command", """{"command":"echo hit>>marker.txt"}""", "c2"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "run it");

        // Executed once, not twice: the marker was appended to exactly once.
        var lines = fx.Read("marker.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);

        Assert.Contains("already ran with these exact arguments", Said(provider), StringComparison.Ordinal);
        Assert.Contains("\"force\": true", Said(provider), StringComparison.Ordinal);
        Assert.Contains(events, e => e.Kind == EventKind.DecisionResolved
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
    public async Task An_exact_repeat_of_run_powershell_is_refused()
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
        Assert.Single(lines);
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
