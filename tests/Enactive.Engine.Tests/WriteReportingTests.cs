namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// What write_file REPORTS, not just what it does. Found in a real run on 2026-09-06: the tool said
/// "Created 'WSFC_Setup_Windows.md'" for a file that already existed and was overwritten — and that
/// sentence is exactly what the reviewer is handed as ground truth, so it cited a false statement in
/// its PASS. It also reported "chars" while its metadata reported bytes, which differed by four on a
/// file that had picked up some stray CJK characters — the only visible trace of the contamination.
/// </summary>
public sealed class WriteReportingTests
{
    [Fact]
    public async Task Creating_a_new_file_says_created()
    {
        using var fx = new EngineFixture();
        var result = await WriteViaEngine(fx, "brand-new.md", "hello");

        Assert.Contains("Created new file", result, StringComparison.Ordinal);
        Assert.DoesNotContain("REPLACED", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overwriting_says_replaced_and_mentions_the_previous_version()
    {
        using var fx = new EngineFixture();
        fx.Write("already-here.md", "the user's version");

        var result = await WriteViaEngine(fx, "already-here.md", "the agent's version");

        Assert.Contains("REPLACED", result, StringComparison.Ordinal);
        Assert.Contains("previous version", result, StringComparison.OrdinalIgnoreCase);
    }

    // The size in the message and the size in the metadata have to be the same number, or the log
    // reads as if two different files were written.
    [Fact]
    public async Task The_reported_size_is_bytes_and_matches_the_file()
    {
        using var fx = new EngineFixture();

        // Non-ASCII on purpose: this is where UTF-16 length and UTF-8 byte count part company.
        const string content = "# Guide\n\nVIP_res虚拟IP — Corosync\n";
        var result = await WriteViaEngine(fx, "unicode.md", content);

        var expected = System.Text.Encoding.UTF8.GetByteCount(content);
        Assert.Contains($"{expected} bytes", result, StringComparison.Ordinal);
        Assert.Equal(expected, new FileInfo(fx.PathOf("unicode.md")).Length);
    }

    // A staged proposal is content as far as this run is concerned, so writing over one is a
    // replacement too — the next read would have returned the proposal, not the disk.
    [Fact]
    public async Task Writing_over_a_staged_proposal_counts_as_replacing()
    {
        using var fx = new EngineFixture();
        var staging = new StagingArtifactStore(fx.Root);

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write twice"}"""),
            Turn.Calls1("write_file", """{"path":"draft.md","content":"first"}""", "c1"),
            Turn.Calls1("write_file", """{"path":"draft.md","content":"second"}""", "c2"),
            Turn.Says("Done."));

        var orchestrator = fx.Build(provider, artifacts: staging);
        var events = await fx.RunAsync(orchestrator, "write draft.md twice");

        var results = events.OfKind(EventKind.ToolResult).ToArray();
        Assert.Contains(results, e => e.Summary.Contains("Created new file", StringComparison.Ordinal));
        Assert.Contains(results, e => e.Summary.Contains("REPLACED", StringComparison.Ordinal));
    }

    /// <summary>Runs one write through the real engine and returns the tool result line.</summary>
    private static async Task<string> WriteViaEngine(EngineFixture fx, string path, string content)
    {
        var arguments = System.Text.Json.JsonSerializer.Serialize(new { path, content });

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write a file"}"""),
            Turn.Calls1("write_file", arguments),
            Turn.Says("Done."));

        var orchestrator = fx.Build(provider);
        var events = await fx.RunAsync(orchestrator, "write a file");

        return events.OfKind(EventKind.ToolResult).Last().Summary;
    }
}
