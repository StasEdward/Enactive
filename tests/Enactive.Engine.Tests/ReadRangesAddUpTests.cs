namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// What a step has read of a file is every window it read, joined up by the engine - in whatever
/// order it read them.
///
/// <para>It used to be one number: the highest line reached without a gap from line 1. A window
/// that began beyond it was dropped on the floor, so reading 401-518 and then 1-400 - the whole file -
/// counted as 1-400, and a whole-file write of a file read in full was refused. Plan, Phase 1.3:
/// "full coverage can come from one complete read or from several ranges that together cover the
/// object; the engine aggregates the ranges".</para>
/// </summary>
public sealed class ReadRangesAddUpTests
{
    private static readonly ToolDefinition Read = new ReadFileTool().Definition;
    private static readonly ToolDefinition Write = new WriteFileTool().Definition;

    private static ReadLedger Reading(int total, params (int From, int To)[] windows)
    {
        var ledger = new ReadLedger();
        foreach (var (from, to) in windows)
            ledger.Saw(new("r", "read_file", "{}"), ToolResults.Ok(metadata: new Dictionary<string, object?>
                { ["path"] = "notes.md", ["firstLine"] = from, ["lastLine"] = to, ["totalLines"] = total }), Read);
        return ledger;
    }

    private static string? Refusal(ReadLedger ledger) => ledger.Refuse(new("w", "write_file", "{}"), "notes.md", Write);

    /// <summary>THE ONE THAT MATTERS: the end first, then the start - the whole file.</summary>
    [Fact]
    public void Windows_read_out_of_order_that_cover_the_file_are_the_whole_file()
        => Assert.Null(Refusal(Reading(518, (401, 518), (1, 400))));

    [Fact]
    public void Overlapping_windows_add_up_too()
        => Assert.Null(Refusal(Reading(100, (50, 100), (1, 60))));

    /// <summary>A hole between two windows is still a hole, and the refusal names it rather than sending the model back to line 1.</summary>
    [Fact]
    public void A_hole_between_windows_is_named_as_the_lines_not_seen()
    {
        var refusal = Refusal(Reading(518, (1, 200), (401, 518)));

        Assert.NotNull(refusal);
        Assert.Contains("has not seen line(s) 201-400 of 518", refusal, StringComparison.Ordinal);
        Assert.Contains("\"offset\": 201", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_holes_are_all_named()
        => Assert.Contains("10-19, 31-40", Refusal(Reading(40, (1, 9), (20, 30)))!, StringComparison.Ordinal);

    /// <summary>The common case reads as it always did: read from the top, stopped short.</summary>
    [Fact]
    public void Read_from_the_top_and_stopped_short_says_where_to_go_on()
        => Assert.Contains("read only lines 1-400 of 518", Refusal(Reading(518, (1, 400)))!, StringComparison.Ordinal);
}
