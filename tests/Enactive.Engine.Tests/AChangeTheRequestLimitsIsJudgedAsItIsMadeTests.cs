namespace Enactive.Engine.Tests;

using System.Text;
using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Run bb77e810, 2026-10-09: "Do not change any source file to make a test pass". A step opened with "I need to fix the
/// actual bug", changed the source and rewrote 21 older tests for twenty minutes; only the step's review would have read
/// the limit, at the end. A request's limit on what may be changed is now in the contract (change_limits), and the
/// first change a step makes to a file the run found is put to the planning model against it - as the change is made.
/// Deliberately not code: a price list the request says to leave alone.
/// </summary>
public sealed class AChangeTheRequestLimitsIsJudgedAsItIsMadeTests
{
    private const string Limit = "Leave prices.txt as it is.";
    private const string Request = "Check the order totals against the price list. " + Limit + " Correct the totals in orders.txt.";

    private static readonly ToolDefinition Write = new("write_file", "write", "{\"type\":\"object\"}", ChangedPathArguments: ["path"]);

    private static ToolCall Call(string path, string content = "x")
        => new("c1", "write_file", JsonSerializer.Serialize(new { path, content }));

    // ── the contract ──────────────────────────────────────────────────────

    private static string Contract(object? changeLimits) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["sources"] = new[] { new { id = "O001", assessment = "the whole request" } },
        ["checks"] = Array.Empty<object>(), ["forbidden_effects"] = Array.Empty<object>(),
        ["change_limits"] = changeLimits, ["action_policy"] = null, ["unresolved"] = null
    });

    private static PlanContract Read(string answer)
        => PlanCheckContract.Validate(answer, complete: true, new PlanCheckInputs(Request, [], [], null, null, false));

    [Fact]
    public void A_limit_in_the_request_s_own_words_is_a_change_restriction()
    {
        var restriction = Assert.Single(Read(Contract(new[] { new { source_quote = Limit } })).Restrictions);

        Assert.Equal((ForbiddenTaskEffect.FileChange, Limit), (restriction.Effect, restriction.SourceQuote));
    }

    [Fact]
    public void A_limit_the_request_does_not_say_is_refused()
        => Assert.ThrowsAny<JsonException>(() => Read(Contract(new[] { new { source_quote = "Never touch the orders." } })));

    [Fact]
    public void Without_change_limits_there_is_no_limit()
    {
        Assert.Empty(Read(Contract(null)).Restrictions);
        using var doc = JsonDocument.Parse(Contract(null));
        Assert.Empty(Read(JsonSerializer.Serialize(doc.RootElement.EnumerateObject().Where(p => p.Name != "change_limits")
            .ToDictionary(p => p.Name, p => p.Value))).Restrictions);
    }

    // ── the guard ─────────────────────────────────────────────────────────

    private static ChangeLimitGuard Guard(IChatProvider provider)
        => new(Request, [Limit], provider, new ModelRef("planner", "strong"), new RunBudget(null, DateTimeOffset.UtcNow), 1000);

    private static Task<ChangeLimitGuard.Decision> Check(ChangeLimitGuard guard, EngineFixture fx, IArtifactScope store,
        string path, int step = 1)
        => guard.CheckAsync(step, "Correct the totals", "I will fix the price instead.", Call(path), Write, store, fx.Workspace.RootPath, default);

    [Fact]
    public async Task A_change_to_a_file_the_run_found_is_refused_when_the_request_limits_it()
    {
        using var fx = new EngineFixture();
        fx.Write("prices.txt", "apple 3");
        var planner = new FakeChatProvider(Turn.Says("""{"allow":false,"reason":"the request says to leave the price list as it is"}"""));

        var decision = await Check(Guard(planner), fx, new DiskArtifactStore(fx.Workspace).BeginStep(), "prices.txt");

        Assert.NotNull(decision.Refusal);
        Assert.Contains(Limit, decision.Refusal, StringComparison.Ordinal);
        Assert.Contains("the request says to leave the price list as it is", decision.Refusal, StringComparison.Ordinal);
        var question = string.Join("\n", planner.Requests[0].Messages.Select(m => m.Content));
        Assert.Contains("I will fix the price instead.", question, StringComparison.Ordinal);   // what the step said it was doing
        Assert.Contains("apple 3", question, StringComparison.Ordinal);                        // the file as it is now
    }

    /// <summary>A long file is shown in part, and the question says it was cut - not passed off as the whole file.</summary>
    [Fact]
    public async Task A_long_file_is_shown_in_part_and_says_so()
    {
        using var fx = new EngineFixture();
        fx.Write("prices.txt", new string('p', ChangeLimitGuard.ShownChars + 500) + "END");
        var planner = new FakeChatProvider(Turn.Says("""{"allow":true,"reason":"fine"}"""));

        await Check(Guard(planner), fx, new DiskArtifactStore(fx.Workspace).BeginStep(), "prices.txt");

        var question = string.Join("\n", planner.Requests[0].Messages.Select(m => m.Content));
        Assert.Contains("…(cut)", question, StringComparison.Ordinal);
        Assert.DoesNotContain("END", question, StringComparison.Ordinal);
    }

    /// <summary>Allowed once, the file is the step's for the rest of the step: a step is not asked about every edit.</summary>
    [Fact]
    public async Task A_file_allowed_once_is_not_asked_about_again_in_that_step()
    {
        using var fx = new EngineFixture();
        fx.Write("orders.txt", "total 5");
        var planner = new FakeChatProvider { WhenExhausted = Turn.Says("""{"allow":true,"reason":"correcting totals is the request"}""") };
        var guard = Guard(planner);
        var store = new DiskArtifactStore(fx.Workspace).BeginStep();

        Assert.Null((await Check(guard, fx, store, "orders.txt")).Refusal);
        Assert.Null((await Check(guard, fx, store, "orders.txt")).Refusal);
        Assert.Single(planner.Requests);

        await Check(guard, fx, store, "orders.txt", step: 2);                                   // another step is asked
        Assert.Equal(2, planner.Requests.Count);
    }

    /// <summary>A file that was not there, one the run made, and the engine's own folder are nothing the run found.</summary>
    [Fact]
    public async Task What_the_run_made_is_never_asked_about()
    {
        using var fx = new EngineFixture();
        var planner = new FakeChatProvider();
        var guard = Guard(planner);
        var disk = new DiskArtifactStore(fx.Workspace);
        var store = disk.BeginStep();
        await store.CreateAsync("made.txt", ArtifactKind.FileSet, "made", s => s.WriteAsync(Encoding.UTF8.GetBytes("new")).AsTask(), default);

        Assert.Null((await Check(guard, fx, store, "new.txt")).Refusal);
        Assert.Null((await Check(guard, fx, store, "made.txt")).Refusal);
        fx.Write(".enactive/scratch/notes.md", "n");
        Assert.Null((await Check(guard, fx, store, ".enactive/scratch/notes.md")).Refusal);
        Assert.Empty(planner.Requests);
    }

    /// <summary>Three refusals of one file in one step, and the limit stands without asking: the same change in new words.</summary>
    [Fact]
    public async Task A_file_refused_three_times_is_not_asked_about_again()
    {
        using var fx = new EngineFixture();
        fx.Write("prices.txt", "apple 3");
        var planner = new FakeChatProvider { WhenExhausted = Turn.Says("""{"allow":false,"reason":"leave the price list"}""") };
        var guard = Guard(planner);
        var store = new DiskArtifactStore(fx.Workspace).BeginStep();

        for (var i = 0; i < ChangeLimitGuard.AsksPerFile; i++)
            Assert.NotNull((await Check(guard, fx, store, "prices.txt")).Refusal);
        var fourth = await Check(guard, fx, store, "prices.txt");

        Assert.Equal(ChangeLimitGuard.AsksPerFile, planner.Requests.Count);
        Assert.Contains("not asked again", fourth.Refusal, StringComparison.Ordinal);
    }

    /// <summary>No usable answer: the change goes ahead as before, and the run says it went unchecked.</summary>
    [Fact]
    public async Task Without_an_answer_the_change_goes_ahead_and_is_said()
    {
        using var fx = new EngineFixture();
        fx.Write("prices.txt", "apple 3");
        var planner = new FakeChatProvider { WhenExhausted = Turn.Says("I cannot say.") };

        var decision = await Check(Guard(planner), fx, new DiskArtifactStore(fx.Workspace).BeginStep(), "prices.txt");

        Assert.Null(decision.Refusal);
        Assert.Contains("went ahead unchecked", decision.Note, StringComparison.Ordinal);
    }

    // ── a run ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The contract carries the limit; the worker's change to the price list is refused AS IT IS MADE, the file is left
    /// as it was, and the worker's change to the orders - which the request asks for - goes through.
    /// </summary>
    [Fact]
    public async Task In_a_run_the_change_is_refused_when_it_is_made()
    {
        using var fx = new EngineFixture { PlannerOverride = new Planner() };
        fx.Write("prices.txt", "apple 3");
        fx.Write("orders.txt", "2 apples: 5");
        var planner = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"orders"}"""),
            Turn.Says(Contract(new[] { new { source_quote = Limit } })))
        {
            Answering = r => r.Messages.Any(m => m.Content?.Contains("is about to change a file that was in the workspace", StringComparison.Ordinal) == true)
                ? Turn.Says(r.Messages.Any(m => m.Content?.Contains("\"path\":\"prices.txt\"", StringComparison.Ordinal) == true)
                    ? """{"allow":false,"reason":"the request says to leave the price list as it is"}"""
                    : """{"allow":true,"reason":"correcting the totals is what the request asks"}""").Reporting(300, 20)
                : null
        };
        var worker = new FakeChatProvider(
            Turn.Calls1("write_file", """{"path":"prices.txt","content":"apple 2.5"}""", "w1"),
            Turn.Calls1("write_file", """{"path":"orders.txt","content":"2 apples: 6"}""", "w2"),
            Turn.Says("Corrected the total in orders.txt."));

        var events = await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn()), Request);

        Assert.True(events.Any(e => e.Kind == EventKind.ToolResult
            && e.Summary.StartsWith("write_file -> refused: 'prices.txt' was not changed: the request limits what may be changed", StringComparison.Ordinal)),
            events.Text());
        Assert.Equal("apple 3", fx.Read("prices.txt"));
        Assert.Equal("2 apples: 6", fx.Read("orders.txt"));
        Assert.Contains("change_limits", planner.Requests[1].Messages[0].Content, StringComparison.Ordinal);   // the review is told to give them
        Assert.Contains(events, e => e.Kind == EventKind.UsageReported && e.Summary.StartsWith("tokens: 300 in, 20 out", StringComparison.Ordinal) && e.Summary.EndsWith(", review)", StringComparison.Ordinal));
    }

    /// <summary>A request that sets no limit asks nothing: the guard costs a run without one nothing.</summary>
    [Fact]
    public async Task A_run_whose_request_sets_no_limit_asks_nothing()
    {
        using var fx = new EngineFixture { PlannerOverride = new Planner() };
        fx.Write("orders.txt", "2 apples: 5");
        var planner = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"orders"}"""),
            Turn.Says(Contract(Array.Empty<object>())));
        var worker = new FakeChatProvider(
            Turn.Calls1("write_file", """{"path":"orders.txt","content":"2 apples: 6"}""", "w1"),
            Turn.Says("Corrected."));

        await fx.RunAsync(fx.Build(new MapProviderFactory(worker, (Routers.PlannerProviderId, planner)),
            router: Routers.WithPlannerOn()), "Correct the totals in orders.txt.");

        Assert.Equal(2, planner.Requests.Count);
        Assert.Equal("2 apples: 6", fx.Read("orders.txt"));
    }
}
