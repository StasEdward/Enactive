namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Tools;
using Xunit;

/// <summary>
/// <c>write_file</c> can APPEND: a report is added to, not retyped.
///
/// <para><b>Measured 2026-09-24 13:12-13:18, run 98bc6302,</b> a local model at about 62 tokens a
/// second. After each wiki page the step wrote the whole report again - 4,502, then 7,689, then
/// 9,453 output tokens, 72, 123 and 152 seconds. There was no cheap way to ADD a section:
/// <c>edit_file</c> needs an exact passage copied back as an anchor, and six of those missed in one
/// run that morning.</para>
///
/// <para><b>What this file no longer tests.</b> For a few hours the same day, the third whole-file
/// write of one path in a step was REFUSED. It was withdrawn: the habit it policed turned out to be
/// caused by the engine (FIX_PLAN 9cs - the history showed the model its own report cut off
/// mid-word, so it believed its write had been truncated and wrote it again), and a rule aimed at
/// one model's behaviour has no place in an engine meant for any model. What stays is the capability
/// every model can use.</para>
/// </summary>
public sealed class AReportIsAddedToNotRetypedTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"write the report"}""";

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

    /// <summary>
    /// Through a real step: a report started once and added to page by page, which is the shape
    /// the capability exists for.
    /// </summary>
    [Fact]
    public async Task A_step_builds_a_report_by_appending_page_by_page()
    {
        using var fx = new EngineFixture();

        var turns = new List<Turn>
        {
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"report.md","content":"# Report"}""", "w0")
        };
        for (var i = 1; i <= 5; i++)
            turns.Add(Turn.Calls1("write_file",
                $$"""{"path":"report.md","content":"## Page {{i}}","append":true}""", $"a{i}"));
        turns.Add(Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(new FakeChatProvider(turns.ToArray())), "write the report");

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        Assert.Equal("# Report\n## Page 1\n## Page 2\n## Page 3\n## Page 4\n## Page 5",
                     File.ReadAllText(Path.Combine(fx.Root, "report.md")));
    }
}
