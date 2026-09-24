namespace Enactive.Engine.Tests;

using Enactive.Tools;
using Xunit;

/// <summary>
/// <c>file_stats</c> reports when a file last changed, and can order by it.
///
/// <para><b>Where it came from.</b> Reviewing <c>desktop-commander</c>'s 25 tools for anything
/// worth taking, 2026-09-24: seven we already had, four were the search family (0.6% of searches
/// ever hit the cap), five were its own configuration and telemetry, and six were interactive
/// processes — which a measurement of our own timeouts answered, since they were prompts and not
/// long work. What survived was one FIELD from <c>get_file_info</c>: a modification time, which
/// <c>file_stats</c> did not report.</para>
///
/// <para><b>Why it matters for the work this project actually does.</b> A drift audit asks "has the
/// code moved since the page was written". The page carries a date; until now the only way to
/// compare against it was <c>git log</c> through a shell.</para>
///
/// <para><b>And why a field was not enough.</b> Ordered by size, a small file edited an hour ago
/// sits below forty large ones that have not moved in months, and <c>MaxFilesListed</c> cuts it off
/// before the model ever sees it. The capability is the field AND the order; either alone answers
/// nothing.</para>
/// </summary>
public sealed class WhenAFileLastChangedTests
{
    private static Task<Enactive.Core.Tools.ToolResult> Stats(EngineFixture fx, string args = "{}")
        => fx.Invoke(new FileStatsTool(), args);

    [Fact]
    public async Task Every_file_says_when_it_changed()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "hello");

        var result = await Stats(fx);

        Assert.True(result.Success, result.Error);

        // yyyy-MM-dd HH:mm - a date that compares to a date in a document without arithmetic.
        Assert.Matches(@"a\.md.*\d{4}-\d{2}-\d{2} \d{2}:\d{2}", result.Output!);
    }

    /// <summary>
    /// THE ONE THAT MATTERS. The recently touched file comes first even though it is the smallest,
    /// which is the whole reason the order exists.
    /// </summary>
    [Fact]
    public async Task Newest_first_puts_the_small_recent_file_above_the_big_old_one()
    {
        using var fx = new EngineFixture();

        fx.Write("big-and-old.md", new string('x', 5000));
        File.SetLastWriteTime(Path.Combine(fx.Root, "big-and-old.md"), DateTime.Now.AddDays(-30));

        fx.Write("small-and-new.md", "just edited");

        var byChanged = await Stats(fx, """{"sort":"changed"}""");
        var bySize = await Stats(fx);

        Assert.True(byChanged.Success, byChanged.Error);

        Assert.True(byChanged.Output!.IndexOf("small-and-new.md", StringComparison.Ordinal)
                    < byChanged.Output!.IndexOf("big-and-old.md", StringComparison.Ordinal),
                    "sort=changed must put the recently edited file first");

        // And the default is untouched: every caller that existed before this asked for size.
        Assert.True(bySize.Output!.IndexOf("big-and-old.md", StringComparison.Ordinal)
                    < bySize.Output!.IndexOf("small-and-new.md", StringComparison.Ordinal),
                    "the default order is still largest first");
    }

    /// <summary>
    /// Each listing says which order it is in — and the size one names the other, because a
    /// capability nobody can find does not exist. This registry has learnt that six times (§9bt).
    /// </summary>
    [Fact]
    public async Task The_listing_says_which_order_it_is_in()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "hello");

        Assert.Contains("Largest first", (await Stats(fx)).Output!, StringComparison.Ordinal);
        Assert.Contains("sort", (await Stats(fx)).Output!, StringComparison.Ordinal);

        Assert.Contains("Most recently changed first",
                        (await Stats(fx, """{"sort":"changed"}""")).Output!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An order it does not know is the default rather than a refusal. A listing is a question, and
    /// answering the question with the usual order beats declining to answer over one word.
    /// </summary>
    [Fact]
    public async Task An_unknown_order_is_the_usual_one()
    {
        using var fx = new EngineFixture();
        fx.Write("a.md", "hello");

        var result = await Stats(fx, """{"sort":"alphabetical"}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains("Largest first", result.Output!, StringComparison.Ordinal);
    }
}
