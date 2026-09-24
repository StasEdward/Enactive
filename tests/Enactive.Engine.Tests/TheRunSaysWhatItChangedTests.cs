namespace Enactive.Engine.Tests;

using Enactive.Core.Artifacts;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// A step sees the whole plan, so it knows where it ends; and the run's closing line says what the run
/// CHANGED - measured from its start to its end - not what it wrote along the way.
///
/// <para><b>Measured 2026-09-24 21:41-21:49, run bc3200, "add tests for the least-covered
/// behaviour".</b> Step 1 of 3 ("find coverage gaps") also wrote every test and checked them by
/// mutation; step 3 ("verify tests fail when behaviour broken") did the mutations again - 41 turns
/// and 19 edits of production code. And the run closed with
/// <c>2 artifact(s): Tests/MonitorClientAdditionalTests.cs, MonitorClient.cs</c>: the production file
/// had been broken and put back 34 times and ended exactly as it began, in a folder with no git.</para>
/// </summary>
public sealed class TheRunSaysWhatItChangedTests
{
    private const string ThreeSteps = """
        {"disposition":"task","title":"Add tests",
         "steps":[{"title":"Alpha finds the gaps","dependsOn":[]},
                  {"title":"Beta writes the tests","dependsOn":[0]},
                  {"title":"Gamma checks the tests fail when the code is broken","dependsOn":[1]}]}
        """;

    private const string Production = "public static class Prod\n{\n    public static int Port => 65535;\n}\n";

    private static string ClosingLine(IEnumerable<WorkEvent> events)
        => events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed).Summary;

    // ── the plan, in every step ──────────────────────────────────────────────

    [Fact]
    public async Task A_step_is_told_the_whole_plan_and_which_step_it_is()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(Turn.Says(ThreeSteps)) { WhenExhausted = Turn.Says("done") };

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "add tests");

        var first = string.Join("\n", provider.Requests.First(r => r.Messages.Any(
            m => m.Content?.Contains("Proceed with this step of the plan: Alpha", StringComparison.Ordinal) == true))
            .Messages.Select(m => m.Content));

        Assert.Contains("This is step 1 of 3", first, StringComparison.Ordinal);
        Assert.Contains("1. Alpha finds the gaps   <- THIS STEP", first, StringComparison.Ordinal);
        Assert.Contains("2. Beta writes the tests", first, StringComparison.Ordinal);
        Assert.Contains("3. Gamma checks the tests fail when the code is broken", first, StringComparison.Ordinal);
        Assert.Contains("What the other steps name is theirs", first, StringComparison.Ordinal);
    }

    /// <summary>THE BOUNDARY. A quick action has no plan to show, and is not given one.</summary>
    [Fact]
    public async Task A_quick_action_is_not_shown_a_plan()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"say hi"}"""))
            { WhenExhausted = Turn.Says("hi") };

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "say hi");

        var said = string.Join("\n", provider.Requests.SelectMany(r => r.Messages).Select(m => m.Content));
        Assert.DoesNotContain("<- THIS STEP", said, StringComparison.Ordinal);
    }

    // ── the closing line ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_file_broken_and_put_back_is_not_named_as_changed()
    {
        using var fx = new EngineFixture();
        fx.Write("Prod.cs", Production);

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"check the tests catch it"}"""),
            Turn.Calls1("edit_file", """{"path":"Prod.cs","old_string":"65535","new_string":"65534"}""", "e1"),
            Turn.Calls1("edit_file", """{"path":"Prod.cs","old_string":"65534","new_string":"65535"}""", "e2"),
            Turn.Says("Broke it, saw it caught, put it back."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the tests catch it");

        Assert.Equal(Production, fx.Read("Prod.cs"));
        var line = ClosingLine(events);
        Assert.Contains("no files changed", line, StringComparison.Ordinal);
        Assert.Contains("written and left as it was: Prod.cs", line, StringComparison.Ordinal);
    }

    /// <summary>The measured run, whole: new tests written, production code broken and restored.</summary>
    [Fact]
    public async Task New_tests_are_named_and_restored_production_code_apart()
    {
        using var fx = new EngineFixture();
        fx.Write("Prod.cs", Production);

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"add tests"}"""),
            Turn.Calls1("write_file", """{"path":"Tests/ProdTests.cs","content":"// a test"}""", "w1"),
            Turn.Calls1("edit_file", """{"path":"Prod.cs","old_string":"65535","new_string":"65534"}""", "e1"),
            Turn.Calls1("edit_file", """{"path":"Prod.cs","old_string":"65534","new_string":"65535"}""", "e2"),
            Turn.Says("Added a test."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "add tests");

        var line = ClosingLine(events);
        Assert.Contains("1 artifact(s): Tests/ProdTests.cs", line, StringComparison.Ordinal);
        Assert.Contains("written and left as it was: Prod.cs", line, StringComparison.Ordinal);
    }

    /// <summary>THE ONE THIS IS FOR: a breakage left in is a change, and the line says so.</summary>
    [Fact]
    public async Task A_breakage_left_in_is_named_as_changed()
    {
        using var fx = new EngineFixture();
        fx.Write("Prod.cs", Production);

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"check the tests catch it"}"""),
            Turn.Calls1("edit_file", """{"path":"Prod.cs","old_string":"65535","new_string":"65534"}""", "e1"),
            Turn.Says("Broke it."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the tests catch it");

        var line = ClosingLine(events);
        Assert.Contains("1 artifact(s): Prod.cs", line, StringComparison.Ordinal);
        Assert.DoesNotContain("left as it was", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Across_steps_the_line_measures_the_whole_run()
    {
        using var fx = new EngineFixture();
        fx.Write("Prod.cs", Production);

        var provider = new FakeChatProvider(
            Turn.Says(ThreeSteps),
            Turn.Says("Gaps found."),
            Turn.Calls1("write_file", """{"path":"Tests/ProdTests.cs","content":"// a test"}""", "w1"),
            Turn.Says("Tests written."),
            Turn.Calls1("edit_file", """{"path":"Prod.cs","old_string":"65535","new_string":"65534"}""", "e1"),
            Turn.Calls1("edit_file", """{"path":"Prod.cs","old_string":"65534","new_string":"65535"}""", "e2"),
            Turn.Says("They fail when it is broken."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "add tests");

        var line = ClosingLine(events);
        Assert.Contains("1 artifact(s): Tests/ProdTests.cs", line, StringComparison.Ordinal);
        Assert.Contains("written and left as it was: Prod.cs", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_changed_by_a_command_is_named()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write a report"}"""),
            Turn.Calls1("run_command", """{"command":"echo disk-report-line>disks.txt"}""", "c1"),
            Turn.Says("Written."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write a report");

        Assert.Contains("changed by commands: disks.txt", ClosingLine(events), StringComparison.Ordinal);
    }

    // ── the measurement outside git ──────────────────────────────────────────

    [Fact]
    public async Task Outside_git_a_file_written_back_as_it_was_is_not_a_change()
    {
        using var fx = new EngineFixture();
        fx.Write("Prod.cs", Production);
        using var changes = new WorkspaceChanges(fx.Root);

        var before = await changes.TakeAsync(CancellationToken.None);
        fx.Write("Prod.cs", Production.Replace("65535", "65534"));
        File.SetLastWriteTimeUtc(Path.Combine(fx.Root, "Prod.cs"), DateTime.UtcNow.AddMinutes(1));
        fx.Write("Prod.cs", Production);
        File.SetLastWriteTimeUtc(Path.Combine(fx.Root, "Prod.cs"), DateTime.UtcNow.AddMinutes(2));
        var after = await changes.TakeAsync(CancellationToken.None);

        Assert.Empty((await changes.CompareAsync(before!, after!, CancellationToken.None))!);
    }

    /// <summary>
    /// WorkspaceChanges.MaxHashedFileBytes / MaxHashedBytesPerSnapshot - THE BOUNDARY. Past what a
    /// snapshot reads, a file is known by size and write time as before: rewritten with the same
    /// bytes, it counts as changed. The limits trade that for not reading a folder of media.
    /// </summary>
    [Fact]
    public async Task Past_the_hashing_limits_write_time_decides()
    {
        using var fx = new EngineFixture();
        var big = new string('x', 1024 * 1024 + 1);
        fx.Write("big.dat", big);
        using var changes = new WorkspaceChanges(fx.Root);

        var before = await changes.TakeAsync(CancellationToken.None);
        fx.Write("big.dat", big);
        File.SetLastWriteTimeUtc(Path.Combine(fx.Root, "big.dat"), DateTime.UtcNow.AddMinutes(1));
        var after = await changes.TakeAsync(CancellationToken.None);

        var change = Assert.Single((await changes.CompareAsync(before!, after!, CancellationToken.None))!);
        Assert.Equal("big.dat", change.Path);
    }

    /// <summary>A real change of the same size is still a change: the content decides, not the size.</summary>
    [Fact]
    public async Task Outside_git_a_same_size_edit_is_a_change()
    {
        using var fx = new EngineFixture();
        fx.Write("Prod.cs", Production);
        using var changes = new WorkspaceChanges(fx.Root);

        var before = await changes.TakeAsync(CancellationToken.None);
        fx.Write("Prod.cs", Production.Replace("65535", "65534"));
        var after = await changes.TakeAsync(CancellationToken.None);

        Assert.Single((await changes.CompareAsync(before!, after!, CancellationToken.None))!);
    }
}
