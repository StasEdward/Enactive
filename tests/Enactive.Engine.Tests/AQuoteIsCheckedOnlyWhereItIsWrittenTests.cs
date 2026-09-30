namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// Run f45e14, 2026-09-29, a wiki checked against its code: a step's findings named five places and quoted six things
/// on one line, and every quote was checked against every place - a column of "NOT in", each a quote held against a
/// file it was never about. The step was rejected twice on quotes matched to the wrong places. A quote is now checked only against
/// the place it is written beside, a file name several files share is told apart by that quote, "not found" says it is
/// not a verdict on the claim, and after a correction the earlier findings are history and the account of the fix is
/// not checked as a claim. Deliberately not code a reviewer would know: a wiki's settings and its two hosts.
/// </summary>
public sealed class AQuoteIsCheckedOnlyWhereItIsWrittenTests
{
    private static EngineFixture Workspace()
    {
        var fx = new EngineFixture();
        fx.Write("hosts/desk/Program.cs", "// desk\nvar settings = Settings.Load();\nvar pages = settings.Pages;\n");
        fx.Write("hosts/cli/Program.cs", "// cli\nvar url = Env(\"WIKI_URL\");\nvar settings = Settings.Load();\n");
        fx.Write("core/Settings.cs", "class Settings\n{\n    static string File() => Path.Combine(AppData, \"Wiki\", \"settings.json\");\n    public string Owner = Env(\"WIKI_OWNER\");\n}\n");
        return fx;
    }

    private static string At(EngineFixture fx, string text, string cited)
        => CitedPlaces.Observe(text, fx.Root, () => CitedPlaces.Sweep(fx.Root)).Single(p => p.Cited == cited).Observed;

    /// <summary>THE ONE THAT MATTERS: several places on one line, each quote checked only against the one it is beside.</summary>
    [Fact]
    public void On_a_line_of_several_places_each_quote_is_checked_against_its_own()
    {
        using var fx = Workspace();
        const string line = "Both read `Settings.Load()` (hosts/desk/Program.cs:2), the owner comes from `Env(\"WIKI_OWNER\")` "
                          + "(core/Settings.cs:4), and the url from `Env(\"WIKI_URL\")` (hosts/cli/Program.cs:2).";

        var desk = At(fx, line, "hosts/desk/Program.cs:2");
        var settings = At(fx, line, "core/Settings.cs:4");
        var cli = At(fx, line, "hosts/cli/Program.cs:2");

        Assert.Contains("Quoted beside it: `Settings.Load()` - at line 2.", desk, StringComparison.Ordinal);
        Assert.Contains("Quoted beside it: `Env(\"WIKI_OWNER\")` - at line 4.", settings, StringComparison.Ordinal);
        Assert.Contains("Quoted beside it: `Env(\"WIKI_URL\")` - at line 2.", cli, StringComparison.Ordinal);
        foreach (var observed in new[] { desk, settings, cli })
        {
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(observed, "Quoted beside it"));
            Assert.DoesNotContain("not found word for word", observed, StringComparison.Ordinal);
        }
    }

    /// <summary>A quote exactly as near to two places is tied to neither: the lines are shown, and nothing is said about it.</summary>
    [Fact]
    public void A_quote_as_near_to_two_places_is_checked_against_neither()
    {
        using var fx = Workspace();
        const string line = "hosts/desk/Program.cs:3 `Settings.Load()` core/Settings.cs:3";

        var desk = At(fx, line, "hosts/desk/Program.cs:3");
        var settings = At(fx, line, "core/Settings.cs:3");

        Assert.Contains("3: var pages = settings.Pages;", desk, StringComparison.Ordinal);
        Assert.DoesNotContain("Quoted beside it", desk, StringComparison.Ordinal);
        Assert.DoesNotContain("Quoted beside it", settings, StringComparison.Ordinal);
    }

    /// <summary>A quote far from every place on its line is tied to none.</summary>
    [Fact]
    public void A_quote_far_from_every_place_is_checked_against_none()
    {
        using var fx = Workspace();
        var line = "The desk host reads core/Settings.cs:3 at start." + new string(' ', 20) + string.Concat(Enumerable.Repeat("and more words ", 8))
                   + "- it can also be overridden by `Env(\"WIKI_URL\")`.";

        Assert.DoesNotContain("Quoted beside it", At(fx, line, "core/Settings.cs:3"), StringComparison.Ordinal);
    }

    /// <summary>A name two files share is told apart by the quote beside it - in exactly the cited lines, and said so.</summary>
    [Fact]
    public void A_name_two_files_share_is_told_apart_by_the_quote_in_the_cited_lines()
    {
        using var fx = Workspace();

        var one = At(fx, "the url is read from `Env(\"WIKI_URL\")` (Program.cs:2)", "Program.cs:2");
        Assert.Contains("hosts/cli/Program.cs, lines 1-3 of 3 (cited as 'Program.cs')", one, StringComparison.Ordinal);
        Assert.Contains("The file was determined by the quote matching among 2 candidates", one, StringComparison.Ordinal);
        Assert.Contains("Quoted beside it: `Env(\"WIKI_URL\")` - at line 2.", one, StringComparison.Ordinal);

        // In both files, though on different lines: only the one with it at the cited line. On line 2 of one and 3 of the
        // other, "Program.cs:2" is the desk host; the lines around the cited one are not looked at.
        var byLine = At(fx, "both read `Settings.Load()` (Program.cs:2)", "Program.cs:2");
        Assert.Contains("hosts/desk/Program.cs", byLine, StringComparison.Ordinal);

        // Where more than one - or none - has it at the cited line, none is opened.
        var none = At(fx, "the pages are `settings.Pages` (Program.cs:1)", "Program.cs:1");
        Assert.Contains("'Program.cs' names more than one file", none, StringComparison.Ordinal);
        Assert.Contains("opened none of them", none, StringComparison.Ordinal);
    }

    /// <summary>Two of three files with the quote at the cited line: neither is chosen.</summary>
    [Fact]
    public void Two_files_holding_the_quote_at_the_cited_line_are_not_told_apart()
    {
        using var fx = new EngineFixture();
        fx.Write("wiki/a/index.md", "# Settings\nsee the owner key\n");
        fx.Write("wiki/b/index.md", "# Pages\nnothing here\n");
        fx.Write("wiki/c/index.md", "# Owner\nsee the owner key\n");

        var observed = At(fx, "the page says `see the owner key` (index.md:2)", "index.md:2");

        Assert.Contains("'index.md' names more than one file", observed, StringComparison.Ordinal);
        Assert.DoesNotContain("determined by the quote", observed, StringComparison.Ordinal);
    }

    /// <summary>
    /// More files with the name than are looked through: none is chosen, and it says why - a match among the files
    /// looked at says nothing about the rest. The first and the last of 51 hold the quote.
    /// </summary>
    [Fact]
    public void More_files_with_the_name_than_are_looked_through_are_not_told_apart()
    {
        using var fx = new EngineFixture();
        var count = CitedPlaces.MaxCandidates + 1;
        for (var i = 1; i <= count; i++)
            fx.Write($"wiki/p{i:D3}/index.md", i == 1 || i == count ? "# Page\nsee the owner key\n" : "# Page\nnothing here\n");

        var observed = At(fx, "the page says `see the owner key` (index.md:2)", "index.md:2");

        Assert.Contains("'index.md' names more than one file", observed, StringComparison.Ordinal);
        Assert.Contains($"More than {CitedPlaces.MaxCandidates} files have that name, so the engine did not look for the quote", observed, StringComparison.Ordinal);
        Assert.DoesNotContain("determined by the quote", observed, StringComparison.Ordinal);
    }

    /// <summary>A name several files share, with no quote tied to it, is not told apart at all.</summary>
    [Fact]
    public void Without_a_quote_tied_to_it_a_shared_name_is_not_resolved()
        => Assert.Contains("names more than one file",
            At(Workspace(), "Program.cs:2 `Settings.Load()` core/Settings.cs:1", "Program.cs:2"), StringComparison.Ordinal);

    /// <summary>
    /// A path the code computes, quoted as the value it comes to, is not the line's text: "not found word for word",
    /// said not to make the claim false - and apart from "elsewhere in the file".
    /// </summary>
    [Fact]
    public void A_value_a_line_computes_is_not_found_word_for_word_and_that_is_not_a_verdict()
    {
        using var fx = Workspace();

        var computed = At(fx, "settings live at `%APPDATA%\\Wiki\\settings.json` (core/Settings.cs:3)", "core/Settings.cs:3");
        Assert.Contains("`%APPDATA%\\Wiki\\settings.json` - not found word for word in core/Settings.cs. That does not make the claim false",
            computed, StringComparison.Ordinal);

        var elsewhere = At(fx, "the owner is `Env(\"WIKI_OWNER\")` (core/Settings.cs:3)", "core/Settings.cs:3");
        Assert.Contains("`Env(\"WIKI_OWNER\")` - not in the cited line 3; found word for word at line 4.", elsewhere, StringComparison.Ordinal);
    }

    /// <summary>
    /// A correction: the earlier findings the engine made are history and said to be, and the place the account of the
    /// fix names - "replaced X by Y" - is not opened as a claim; the result handed on is what is checked.
    /// </summary>
    [Fact]
    public async Task After_a_correction_the_old_findings_are_history_and_the_account_of_the_fix_is_not_checked()
    {
        using var fx = Workspace();
        fx.StepOutputs = true;
        var worker = new FakeChatProvider(
            [Turn.Says("""{"disposition":"task","title":"wiki","steps":[{"title":"Check the settings page","dependsOn":[],"output":{"findings":{"type":"text","description":"what the page gets wrong"}}}]}"""),
             Turn.Calls1(StepOutputContract.ToolName, """{"findings":"The page is wrong: the pages come from `settings.Pages` (core/Settings.cs:2)."}""", "s1"),
             Turn.Says("Checked the page."),
             Turn.Calls1(StepOutputContract.ToolName, """{"findings":"The page is wrong: the pages come from `settings.Pages` (hosts/desk/Program.cs:3)."}""", "s2"),
             Turn.Says("Corrected: replaced core/Settings.cs:2 by hosts/desk/Program.cs:3.")]) { WhenExhausted = Turn.Says("Done.") };
        var reviewer = new FakeChatProvider(
            Turn.Says("""{"verdict":"fail","reason":"settings.Pages is not at core/Settings.cs:2","calls":[],"files":[]}"""),
            Turn.Says("""{"verdict":"pass","reason":"the findings cite the right line","calls":[1],"files":[]}"""));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer), "check the settings page of the wiki");

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        var second = string.Join("\n", reviewer.Requests[1].Messages.Select(m => m.Content));
        Assert.Contains(CitedPlaces.Historical, second, StringComparison.Ordinal);
        Assert.Contains("Quoted beside it: `settings.Pages` - at line 3.", second, StringComparison.Ordinal);
        // The old place: once, as history - not opened again from "replaced core/Settings.cs:2 by ...".
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(second, @"engine_opened_cited_place \{""cited"":""core/Settings.cs:2""\}"));
        Assert.DoesNotContain("not found word for word in core/Settings.cs", second, StringComparison.Ordinal);
    }

    /// <summary>
    /// A first result that cited nothing leaves no finding behind to tell a correction by. The result handed on is what
    /// is checked at every attempt, so the account of the fix - naming an old place - is still not opened as a claim.
    /// </summary>
    [Fact]
    public async Task A_first_result_citing_nothing_does_not_let_the_account_of_the_fix_be_checked()
    {
        using var fx = Workspace();
        fx.StepOutputs = true;
        var worker = new FakeChatProvider(
            [Turn.Says("""{"disposition":"task","title":"wiki","steps":[{"title":"Check the settings page","dependsOn":[],"output":{"findings":{"type":"text","description":"what the page gets wrong"}}}]}"""),
             Turn.Calls1(StepOutputContract.ToolName, """{"findings":"The page is wrong about where the pages come from."}""", "s1"),
             Turn.Says("Checked the page."),
             Turn.Calls1(StepOutputContract.ToolName, """{"findings":"The page is wrong: the pages come from `settings.Pages` (hosts/desk/Program.cs:3)."}""", "s2"),
             Turn.Says("Corrected: the claim I had put on core/Settings.cs:2 is at hosts/desk/Program.cs:3.")]) { WhenExhausted = Turn.Says("Done.") };
        var reviewer = new FakeChatProvider(
            Turn.Says("""{"verdict":"fail","reason":"the findings name no place in the code","calls":[],"files":[]}"""),
            Turn.Says("""{"verdict":"pass","reason":"the findings cite the right line","calls":[1],"files":[]}"""));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer), "check the settings page of the wiki");

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        var second = string.Join("\n", reviewer.Requests[1].Messages.Select(m => m.Content));
        Assert.Contains("Quoted beside it: `settings.Pages` - at line 3.", second, StringComparison.Ordinal);
        Assert.DoesNotContain("""engine_opened_cited_place {"cited":"core/Settings.cs:2"}""", second, StringComparison.Ordinal);
    }
}
