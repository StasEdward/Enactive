namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Reported with a screenshot, 2026-09-07: "что-то UI поломался — какие-то дубли в разных местах."
///
/// <para>Nothing was duplicated. Every card in the run list was showing the WHOLE request, line
/// breaks intact, so a template's four-paragraph goal filled four lines of every card — and six runs
/// of the same template were six identical blocks of text. A list whose entries cannot be told apart
/// reads exactly like a list showing the same thing several times.</para>
///
/// <para>The title came from the IntentReceived summary, which was fine for as long as a request was
/// a sentence somebody typed. Templates ended that, and nothing in between noticed because a title
/// was never required to be one line.</para>
/// </summary>
public sealed class RunTitleTests
{
    private static ResolvedTaskSpec Spec(string templateName)
        => new("code-review", 1, templateName, Guid.NewGuid(), "ws", @"c:\ws",
               "Review this and write your findings to review.md:\n\nEverything that has changed.",
               new Dictionary<string, string>(),
               new PermissionPolicy(PermissionLevel.Execute, new[] { "*" }, Array.Empty<string>()),
               Array.Empty<SuccessCriterionDefinition>(), ExecutionLimits.None, null, true);

    private static RunRecord Record(string title, ResolvedTaskSpec? spec = null)
    {
        var at = DateTimeOffset.Now;
        return new RunRecord(
            Guid.NewGuid(), Guid.NewGuid(), title, "a-model", at, at, "Completed",
            Array.Empty<RunEventRecord>(), Array.Empty<string>(), Array.Empty<string>(),
            Spec: spec?.Snapshot());
    }

    /// <summary>The request that broke it, verbatim from the screenshot.</summary>
    private const string TheGoal =
        "Review this and write your findings to review.md:\n\n"
        + "Everything that has changed since the last commit.\n\n"
        + "Report only what you can point at in the code — file and line. For each finding say what "
        + "breaks and under what input, not that it could be tidier.\n\n"
        + "Change nothing except the report.";

    // ── one line, always ────────────────────────────────────────────────────

    /// <summary>The defect itself: a title is one line or it is not a title.</summary>
    [Fact]
    public void A_title_is_one_line_however_many_the_request_had()
    {
        var title = RunTitle.OneLine(TheGoal);

        Assert.DoesNotContain('\n', title);
        Assert.DoesNotContain('\r', title);
        Assert.StartsWith("Review this and write your findings", title);
    }

    [Theory]
    [InlineData("one\ntwo", "one two")]
    [InlineData("one\r\ntwo", "one two")]
    [InlineData("one\t\ttwo", "one two")]
    [InlineData("  padded  ", "padded")]
    [InlineData("one\n\n\nfour", "one four")]
    public void Whitespace_of_every_kind_collapses_to_a_single_space(string input, string expected)
        => Assert.Equal(expected, RunTitle.OneLine(input));

    [Fact]
    public void A_long_title_is_cut_and_says_it_was()
    {
        var title = RunTitle.OneLine(new string('x', 500));

        Assert.Equal(RunTitle.MaxChars + 1, title.Length);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void A_short_title_is_left_exactly_alone()
        => Assert.Equal("Fix the build", RunTitle.OneLine("Fix the build"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Nothing_gives_nothing_rather_than_throwing(string? input)
        => Assert.Equal("", RunTitle.OneLine(input));

    // ── a templated run is named by its template ────────────────────────────

    /// <summary>
    /// "Code Review" is what somebody scanning a list is looking for, and it is the truth about
    /// where the run came from. Six runs of it are told apart by their time and their status, which
    /// the line underneath already carries — the way every other list in this application works.
    /// </summary>
    [Fact]
    public void A_run_from_a_template_is_called_by_the_template()
        => Assert.Equal("Code Review", RunTitle.For(Record(TheGoal, Spec("Code Review"))));

    [Fact]
    public void A_typed_run_is_still_called_by_what_was_asked_for()
        => Assert.Equal("Fix the failing build", RunTitle.For(Record("Fix the failing build")));

    /// <summary>
    /// Repaired on the way OUT. Every run already in somebody's history was stored with the broken
    /// title, and rewriting their run history in place to fix a display problem is the wrong trade.
    /// </summary>
    [Fact]
    public void History_recorded_before_the_fix_reads_properly_anyway()
    {
        var title = RunTitle.For(Record(TheGoal));

        Assert.DoesNotContain('\n', title);
        Assert.True(title.Length <= RunTitle.MaxChars + 1);
    }

    [Fact]
    public void A_record_with_no_title_at_all_says_so()
        => Assert.Equal("(untitled run)", RunTitle.For(Record("   ")));

    /// <summary>A spec that will not parse is not a title - it falls back rather than throwing.</summary>
    [Fact]
    public void An_unreadable_specification_does_not_take_the_title_with_it()
    {
        var at = DateTimeOffset.Now;
        var record = new RunRecord(
            Guid.NewGuid(), Guid.NewGuid(), "Fix the build", null, at, at, "Completed",
            Array.Empty<RunEventRecord>(), Array.Empty<string>(), Array.Empty<string>(),
            Spec: "{ not json");

        Assert.Equal("Fix the build", RunTitle.For(record));
    }

    // ── the property that matters ───────────────────────────────────────────

    /// <summary>
    /// Two runs of the same template are told apart by something. If the title is all a list shows,
    /// identical titles are identical rows — which is what "duplicates" meant.
    /// </summary>
    [Fact]
    public void Two_runs_of_one_template_share_a_title_and_that_is_the_point()
    {
        var one = RunTitle.For(Record(TheGoal, Spec("Code Review")));
        var two = RunTitle.For(Record(TheGoal, Spec("Code Review")));

        Assert.Equal(one, two);
        // ...which is only acceptable because it is short enough to leave room for the line that
        // does distinguish them. A title that filled the card left no such room.
        Assert.True(one.Length <= RunTitle.MaxChars);
    }
}
