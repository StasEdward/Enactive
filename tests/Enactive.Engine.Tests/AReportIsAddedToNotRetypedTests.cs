namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Tools;
using Xunit;

/// <summary>
/// A report is added to, not retyped: <c>write_file</c> can append, and the third whole-file
/// write of one path in a step is refused.
///
/// <para><b>Measured 2026-09-24 13:12-13:18, run 98bc6302,</b> a local model at about 62 tokens a
/// second. After each wiki page the step wrote the whole report again - 4,502, then 7,689, then
/// 9,453 output tokens, 72, 123 and 152 seconds - and to the person watching the run it was hung.
/// Only 106 of the first version's 295 lines survived into the second: the report was being
/// composed afresh every time, so findings for pages already checked could change or vanish.</para>
///
/// <para>There was no cheap way to ADD a section. <c>edit_file</c> needs an exact passage copied
/// back as an anchor, and six of those missed in one run that morning.</para>
/// </summary>
public sealed class AReportIsAddedToNotRetypedTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write the report"}""";

    // ── append, in the tool ─────────────────────────────────────────────────

    [Fact]
    public async Task Append_adds_to_the_end_and_leaves_the_rest_alone()
    {
        using var fx = new EngineFixture();
        fx.Write("r.md", "# Report\n\n## Page 1\nok\n");

        var result = await fx.Invoke(new WriteFileTool(),
            """{"path":"r.md","content":"## Page 2\nalso ok\n","append":true}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("APPENDED", result.Output, StringComparison.Ordinal);
        Assert.Equal("# Report\n\n## Page 1\nok\n## Page 2\nalso ok\n",
                     File.ReadAllText(Path.Combine(fx.Root, "r.md")));
    }

    /// <summary>A report that ends without a line break does not swallow the next heading.</summary>
    [Fact]
    public async Task Append_starts_on_a_line_of_its_own()
    {
        using var fx = new EngineFixture();
        fx.Write("r.md", "last line");

        await fx.Invoke(new WriteFileTool(), """{"path":"r.md","content":"## Next","append":true}""");

        Assert.Equal("last line\n## Next", File.ReadAllText(Path.Combine(fx.Root, "r.md")));
    }

    [Fact]
    public async Task Append_to_a_file_that_is_not_there_creates_it()
    {
        using var fx = new EngineFixture();

        var result = await fx.Invoke(new WriteFileTool(),
            """{"path":"new.md","content":"# First","append":true}""");

        Assert.True(result.Success, result.Error);
        Assert.Equal("# First", File.ReadAllText(Path.Combine(fx.Root, "new.md")));
    }

    /// <summary>The endings the file already uses, the same rule as a replacement.</summary>
    [Fact]
    public async Task Append_keeps_the_files_line_endings()
    {
        using var fx = new EngineFixture();
        File.WriteAllText(Path.Combine(fx.Root, "crlf.md"), "a\r\n");

        await fx.Invoke(new WriteFileTool(), """{"path":"crlf.md","content":"b\nc","append":true}""");

        Assert.Equal("a\r\nb\r\nc", File.ReadAllText(Path.Combine(fx.Root, "crlf.md")));
    }

    // ── the step ────────────────────────────────────────────────────────────

    /// <summary>
    /// Enough text in front of every whole write to make it the expensive case - past
    /// <c>ReadLedger.RewriteFloorChars</c>, as the measured rewrites (15,000 to 37,000 characters)
    /// were.
    /// </summary>
    private static readonly string Filler = new('x', 2_100);

    /// <summary>A whole-file write of the report. <paramref name="content"/> is JSON-escaped text.</summary>
    private static Turn Whole(string content, string id)
        => Turn.Calls1("write_file", $$"""{"path":"report.md","content":"{{Filler}}\n{{content}}"}""", id);

    private static string Said(FakeChatProvider provider)
        => string.Join("\n", provider.Requests.SelectMany(r => r.Messages).Select(m => m.Content));

    /// <summary>
    /// THE ONE THAT MATTERS. Created, revised, then a third whole write: refused, with the route
    /// named - and after the model takes it, the step finishes. A refusal that failed the step at
    /// the end would be the same defect 9co closed for <c>dir</c>.
    /// </summary>
    [Fact]
    public async Task The_third_whole_write_is_refused_and_the_step_still_finishes()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Whole("# Report\\n## Page 1", "w1"),
            Whole("# Report\\n## Page 1\\n## Page 2", "w2"),
            Whole("# Report\\n## Page 1\\n## Page 2\\n## Page 3", "w3"),
            Turn.Calls1("write_file", """{"path":"report.md","content":"## Page 3","append":true}""", "a1"),
            Turn.Says("Report written."));

        var events = await fx.RunAsync(fx.Build(provider), "write the report");

        var said = Said(provider);

        // Warned on the second, refused on the third.
        Assert.Contains("Another whole-file write of it will be refused", said, StringComparison.Ordinal);
        Assert.Contains("Not written: this step has already written 'report.md' whole 2 times", said, StringComparison.Ordinal);
        Assert.Contains("\"append\": true", said, StringComparison.Ordinal);

        // The refused rewrite never reached the disk; the append did.
        Assert.Equal(Filler + "\n# Report\n## Page 1\n## Page 2\n## Page 3",
                     File.ReadAllText(Path.Combine(fx.Root, "report.md")));

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    // ── the boundaries ──────────────────────────────────────────────────────

    /// <summary>The count is per PATH. Writing several files whole, once each, is ordinary work.</summary>
    [Fact]
    public async Task Different_files_written_whole_are_never_refused()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"a.md","content":"a"}""", "w1"),
            Turn.Calls1("write_file", """{"path":"b.md","content":"b"}""", "w2"),
            Turn.Calls1("write_file", """{"path":"c.md","content":"c"}""", "w3"),
            Turn.Says("Wrote three files."));

        await fx.RunAsync(fx.Build(provider), "write the report");

        Assert.DoesNotContain("Not written:", Said(provider), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fx.Root, "c.md")));
    }

    /// <summary>
    /// A cheap change in between resets the count. A model that edited the file has shown it can,
    /// and a later single rewrite - a final restructure - is its call to make.
    /// </summary>
    [Fact]
    public async Task An_edit_in_between_starts_the_count_again()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Whole("# Report\\nv1", "w1"),
            Whole("# Report\\nv2", "w2"),
            Turn.Calls1("edit_file", """{"path":"report.md","old_string":"v2","new_string":"v3"}""", "e1"),
            Whole("# Final\\nv4", "w3"),
            Turn.Says("Done."));

        // The shipping developer: the fixture's bare default has no edit_file, and a refused edit is
        // rightly no reason to reset anything - which is what the first draft of this test found.
        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write the report");

        Assert.DoesNotContain("Not written:", Said(provider), StringComparison.Ordinal);
        Assert.Equal(Filler + "\n# Final\nv4", File.ReadAllText(Path.Combine(fx.Root, "report.md")));
    }

    /// <summary>
    /// A short file written whole again and again is not the expensive case - the same judgement
    /// as the shrink guard's floor. Rewriting a few lines costs nothing worth refusing.
    /// </summary>
    [Fact]
    public async Task Small_files_rewritten_often_are_not_this_guards_business()
    {
        using var fx = new EngineFixture();

        var turns = new List<Turn> { Turn.Says(QuickAction) };
        for (var i = 1; i <= 4; i++)
            turns.Add(Turn.Calls1("write_file", $$"""{"path":"notes.md","content":"version {{i}}"}""", $"w{i}"));
        turns.Add(Turn.Says("Done."));

        var provider = new FakeChatProvider(turns.ToArray());
        await fx.RunAsync(fx.Build(provider), "write the report");

        Assert.DoesNotContain("Not written:", Said(provider), StringComparison.Ordinal);
        Assert.Equal("version 4", File.ReadAllText(Path.Combine(fx.Root, "notes.md")));
    }

    /// <summary>Appending is the cheap route, and is never counted against the file.</summary>
    [Fact]
    public async Task Appending_any_number_of_times_is_never_refused()
    {
        using var fx = new EngineFixture();

        var turns = new List<Turn> { Turn.Says(QuickAction), Whole("# Report", "w0") };
        for (var i = 1; i <= 5; i++)
            turns.Add(Turn.Calls1("write_file",
                $$"""{"path":"report.md","content":"## Page {{i}}","append":true}""", $"a{i}"));
        turns.Add(Turn.Says("Done."));

        var provider = new FakeChatProvider(turns.ToArray());
        await fx.RunAsync(fx.Build(provider), "write the report");

        Assert.DoesNotContain("Not written:", Said(provider), StringComparison.Ordinal);
        Assert.Contains("## Page 5", File.ReadAllText(Path.Combine(fx.Root, "report.md")), StringComparison.Ordinal);
    }
}
