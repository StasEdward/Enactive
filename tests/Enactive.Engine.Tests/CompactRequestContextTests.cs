namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Execution;

public sealed class CompactRequestContextTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Ten_steps_keep_one_verbatim_request_and_one_plan_per_conversation(int parallelism)
    {
        using var fx = new EngineFixture();
        const string request = "Unique request: проверь C:\\src\\a.cs.\nRun exactly \"dotnet test\".\n\nKeep all files.";
        var plan = JsonSerializer.Serialize(new { disposition = "task", title = "Ten scopes",
            steps = Enumerable.Range(1, 10).Select(i => new { title = "Scope " + i,
                dependsOn = i == 1 ? Array.Empty<int>() : new[] { i - 2 } }) });
        var provider = new FakeChatProvider(Turn.Says(plan)) { WhenExhausted = Turn.Says("done") };
        await fx.RunAsync(fx.Build(provider, maxParallelSteps: parallelism), request);
        var turns = provider.Requests.Where(r => r.Messages.Any(m =>
            m.Content?.StartsWith("Proceed with this step of the plan:") == true)).ToArray();
        Assert.Equal(10, turns.Length);
        foreach (var turn in turns)
        {
            var text = string.Join("\n", turn.Messages.Select(m => m.Content));
            Assert.Equal(1, Count(text, request));
            Assert.Equal(1, Count(text, "## Plan scopes"));
            Assert.Contains("S10: Scope 10", text);
            Assert.Contains("O002: lines 2–3", text);
            Assert.DoesNotContain("Request obligations (verbatim source units", text);
            Assert.DoesNotContain("Check every requirement within each unit", text);
        }
        var planning = string.Join("\n", provider.Requests[0].Messages.Select(m => m.Content));
        Assert.Equal(1, Count(planning, request));
        Assert.DoesNotContain("Request obligations (verbatim source units", planning);
    }

    [Theory]
    [InlineData("\r\nfirst \"quoted\" C:\\test\r\n\r\nsecond\r\n")]
    [InlineData("\n\nfirst\nsecond")]
    [InlineData("  \r\n\n")]
    public void Execution_view_preserves_raw_text_and_reviewer_ids(string request)
    {
        var prompt = RequestObligations.ExecutionPrompt(request);
        Assert.Contains("Original request (verbatim):\n" + request + "\n\nRequirement IDs", prompt);
        var obligations = RequestObligations.Create(request);
        foreach (var item in obligations.Items) Assert.Contains(item.Id + ": lines ", prompt);
        // The review contract remains JSON, including scopes and exact source units.
        Assert.Contains("\"Items\":", obligations.Describe());
        Assert.DoesNotContain("\"Items\":", prompt);
    }

    private static int Count(string text, string needle)
        => (text.Length - text.Replace(needle, "", StringComparison.Ordinal).Length) / needle.Length;
}
