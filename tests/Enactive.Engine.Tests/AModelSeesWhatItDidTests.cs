namespace Enactive.Engine.Tests;

using Xunit;

/// <summary>
/// The model's own tool calls come back to it exactly as it sent them.
///
/// <para><b>Measured 2026-09-24, twice, with two different wordings.</b> A long argument used to be
/// recorded shortened, to save prompt tokens. As its first 200 characters and a size, a model saw
/// its report stop mid-word and said "The file was truncated. Let me write it in parts". As a
/// bracketed note - <c>"content":"[Not repeated here: this call sent all 12,672 characters ...]"</c>
/// - a model said "Wait, that was a placeholder. Let me write the actual report", rewrote a 12 KB
/// report five times, hit the shrink guard, and deleted the file.</para>
///
/// <para>A history that differs from what the model did gets "corrected" by the model, whatever the
/// words. So it is not edited at record time at all; only the window trim rewrites calls, and never
/// the newest ones.</para>
/// </summary>
public sealed class AModelSeesWhatItDidTests
{
    [Fact]
    public async Task A_long_write_comes_back_verbatim_on_the_next_turn()
    {
        using var fx = new EngineFixture();
        var report = "# Drift Report\n\n" + new string('x', 12_000);
        var args = System.Text.Json.JsonSerializer.Serialize(new { path = "report.md", content = report });

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write the report"}"""),
            Turn.Calls1("write_file", args, "w1"),
            Turn.Says("Written."));

        await fx.RunAsync(fx.Build(provider), "write the report");

        var remembered = provider.Requests[^1].Messages
            .SelectMany(m => m.ToolCalls ?? Array.Empty<Enactive.Core.Tools.ToolCall>())
            .Single(c => c.Id == "w1");

        Assert.Equal(args, remembered.ArgumentsJson);
        Assert.DoesNotContain("Not repeated here", remembered.ArgumentsJson, StringComparison.Ordinal);
    }
}
