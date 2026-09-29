namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// 2026-09-29: a disk report came back dated from the worker's memory - no model had a clock, and nothing in any prompt
/// said what day it was. Now the planner, the worker of every step and the step review are each told the local date
/// and time, with the offset. Deliberately not code: a disk report.
/// </summary>
public sealed class EveryModelIsToldTheTimeTests
{
    [Fact]
    public void The_line_says_the_date_the_time_the_day_and_the_offset()
    {
        var line = LocalTime.Line(new DateTimeOffset(2026, 9, 29, 20, 15, 42, TimeSpan.FromHours(3)));

        Assert.Equal("Local date and time now: 2026-09-29 20:15, Tuesday (UTC+03:00)", line);
        Assert.EndsWith("(UTC-05:30)", LocalTime.Line(new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.FromMinutes(-330))));
    }

    [Fact]
    public async Task The_planner_the_worker_and_the_step_review_are_told_it()
    {
        using var fx = new EngineFixture { ShortReview = true };
        var worker = new FakeChatProvider(
            [Turn.Says("""{"disposition":"task","title":"disk report","steps":[{"title":"Write the disk report","dependsOn":[]}]}"""),
             Turn.Calls1("write_file", """{"path":"report.md","content":"C: 120 GB free"}""", "w1"),
             Turn.Says("Report written.")]) { WhenExhausted = Turn.Says("Done.") };
        var reviewer = new FakeChatProvider(Turn.Says("""{"verdict":"pass","reason":"the report is written","calls":[1],"files":[]}"""));
        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(), reviewProvider: reviewer,
            checkSoundness: true), "check the disks and write a report");

        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
        var today = "Local date and time now: " + DateTimeOffset.Now.ToString("yyyy-MM-dd");
        string All(ChatRequest r) => string.Join("\n", r.Messages.Select(m => m.Content));
        Assert.Contains(today, All(worker.Requests[0]), StringComparison.Ordinal);                      // the plan
        Assert.Contains(today, All(worker.Requests[1]), StringComparison.Ordinal);                      // the step
        Assert.Contains(today, All(reviewer.Requests[0]), StringComparison.Ordinal);                    // its review
    }
}
