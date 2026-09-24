namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Xunit;

/// <summary>
/// What a step read of a file stops counting once the file is gone - deleted, or moved away - and a
/// file written and then removed is named as such, not as "not measured".
///
/// <para><b>Measured 2026-09-24 23:04-23:08, run 9ecf0e.</b> A step wrote a 36 KB test file, read part
/// of it to find a duplicate, deleted it (approved), generated it again from scratch - 8,010 tokens,
/// about two minutes on that machine - and the write was refused as "the file has only been read in
/// part", about a file that no longer existed. The handover that followed then named it as "written
/// where the engine does not measure", when it had simply been created and removed.</para>
/// </summary>
public sealed class AFileThatIsGoneWasNotReadTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write the tests"}""";

    private static string Long() => string.Join("\n", Enumerable.Range(1, 600).Select(i => $"// line {i:D4} of a long generated file"));

    /// <summary>
    /// A whole-file write that keeps almost every line - so the ONLY guard that can refuse it is the
    /// one about having read the file in part, and not the ones about a file losing its content.
    /// </summary>
    private static string LongWithOneLineChanged() => Long().Replace("// line 0001 of", "// LINE ONE of");

    private static string ClosingLine(IEnumerable<WorkEvent> events)
        => events.Last(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed).Summary;

    private static string Said(FakeChatProvider provider)
        => string.Join("\n", provider.Requests.SelectMany(r => r.Messages).Select(m => m.Content));

    /// <summary>THE MEASURED CASE: read in part, deleted, written again.</summary>
    [Fact]
    public async Task A_file_deleted_after_a_partial_read_can_be_written_again()
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "allow";   // delete_file always asks; in the run the person said yes
        fx.Write("Tests/Edge.cs", Long());

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("read_file", """{"path":"Tests/Edge.cs","offset":450}""", "r1"),
            Turn.Calls1("delete_file", """{"path":"Tests/Edge.cs"}""", "d1"),
            Turn.Calls1("write_file", """{"path":"Tests/Edge.cs","content":"// written again, whole"}""", "w1"),
            Turn.Says("Rewrote the file."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write the tests");

        Assert.DoesNotContain("has read only lines", Said(provider), StringComparison.Ordinal);
        Assert.Equal("// written again, whole", fx.Read("Tests/Edge.cs"));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>Deleted by a COMMAND, which the ledger never sees: the file is not there, so the write creates it.</summary>
    [Fact]
    public async Task A_file_deleted_by_a_command_can_be_written_again()
    {
        using var fx = new EngineFixture();
        fx.Write("Edge.cs", Long());

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("read_file", """{"path":"Edge.cs","offset":450}""", "r1"),
            Turn.Calls1("run_command", """{"command":"del Edge.cs"}""", "c1"),
            Turn.Calls1("write_file", """{"path":"Edge.cs","content":"// written again, whole"}""", "w1"),
            Turn.Says("Rewrote the file."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write the tests");

        Assert.Equal("// written again, whole", fx.Read("Edge.cs"));
    }

    /// <summary>THE BOUNDARY. A file still there, read in part, is still guarded.</summary>
    [Fact]
    public async Task A_file_still_there_and_read_in_part_is_still_refused()
    {
        using var fx = new EngineFixture();
        fx.Write("Edge.cs", Long());

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("read_file", """{"path":"Edge.cs","offset":450}""", "r1"),
            Turn.Calls1("write_file", System.Text.Json.JsonSerializer.Serialize(new { path = "Edge.cs", content = LongWithOneLineChanged() }), "w1"),
            Turn.Says("Tried."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write the tests");

        Assert.Equal(Long(), fx.Read("Edge.cs"));
    }

    /// <summary>A file seen in part does not become rewritable by being renamed: what was read moves with it.</summary>
    [Fact]
    public async Task A_file_moved_after_a_partial_read_is_still_guarded_under_its_new_name()
    {
        using var fx = new EngineFixture();
        fx.Write("old.cs", Long());

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("read_file", """{"path":"old.cs","offset":450}""", "r1"),
            Turn.Calls1("move_file", """{"from":"old.cs","to":"new.cs"}""", "m1"),
            Turn.Calls1("write_file", System.Text.Json.JsonSerializer.Serialize(new { path = "new.cs", content = LongWithOneLineChanged() }), "w1"),
            Turn.Calls1("write_file", """{"path":"old.cs","content":"// a new file where the old one was"}""", "w2"),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write the tests");

        Assert.Equal(Long(), fx.Read("new.cs"));                                  // refused: seen in part
        Assert.Equal("// a new file where the old one was", fx.Read("old.cs"));   // allowed: a new file
    }

    // ── the words ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_file_written_and_then_removed_is_named_as_such()
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "allow";

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"draft.md","content":"a draft"}""", "w1"),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1"),
            Turn.Says("Wrote a draft and removed it."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write the tests");

        var line = ClosingLine(events);
        Assert.Contains("written and then removed: draft.md", line, StringComparison.Ordinal);
        Assert.DoesNotContain("not measure", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_handover_names_a_file_written_and_then_removed()
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "allow";

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"draft.md","content":"a draft"}""", "w1").Reporting(prompt: 3_000),
            Turn.Calls1("delete_file", """{"path":"draft.md"}""", "d1").Reporting(prompt: 8_000),
            Turn.Says("Note: the draft is gone."),
            Turn.Says("Done."))
        { Window = 10_000, HandoverAt = 75 };

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write the tests");

        var resumed = provider.Requests.SelectMany(r => r.Messages).Select(m => m.Content ?? "")
                              .Last(c => c.Contains("started again from your own notes", StringComparison.Ordinal));
        Assert.Contains("Written by you and then removed - not on disk now: draft.md", resumed, StringComparison.Ordinal);
        Assert.DoesNotContain("does not measure", resumed, StringComparison.Ordinal);
    }
}
