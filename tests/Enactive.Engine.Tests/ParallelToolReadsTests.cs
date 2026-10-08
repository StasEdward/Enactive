namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

public sealed class ParallelToolReadsTests
{
    private const string Plan = """{"disposition":"quick_action","title":"read","steps":[]}""";
    private sealed class Probe(Func<string, ToolContext, CancellationToken, Task<ToolResult>> run,
        bool parallel = true, bool approval = false) : ITool
    {
        public ToolDefinition Definition => new("probe", "read probe", "{}",
            WorkspaceEffect: WorkspaceEffect.None, ParallelRead: parallel);
        public PermissionLevel RequiredLevel => PermissionLevel.Observe;
        public bool RequiresApproval => approval;
        public Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
            => run(argumentsJson, context, ct);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Reads_overlap_but_write_is_a_barrier_and_transcript_keeps_call_order()
    {
        using var fx = new EngineFixture();
        fx.Write("value.txt", "OLD");
        var arrivals = new int[2];
        var entered = new[] { Signal(), Signal() };
        var secondDone = new[] { Signal(), Signal() };
        var probe = new Probe(async (args, _, ct) =>
        {
            using var json = JsonDocument.Parse(args);
            var id = json.RootElement.GetProperty("id").GetInt32();
            var group = id / 2;
            if (Interlocked.Increment(ref arrivals[group]) == 2) entered[group].TrySetResult();
            await entered[group].Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var text = fx.Read("value.txt");
            if (id % 2 == 0) await secondDone[group].Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            else secondDone[group].TrySetResult();
            return ToolResults.Ok($"{id}:{text}");
        });
        fx.ToolsOverride = [probe, new WriteFileTool()];
        var calls = new[] { new ToolCall("a", "probe", "{\"id\":0}"), new("b", "probe", "{\"id\":1}"),
            new("write", "write_file", """{"path":"value.txt","content":"NEW"}"""),
            new("c", "probe", "{\"id\":2}"), new("d", "probe", "{\"id\":3}") };
        var provider = new FakeChatProvider(Turn.Says(Plan), new Turn(Calls: calls), Turn.Says("done"));
        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.WorkerWith("probe", "write_file")), "Read and change");
        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        var results = provider.Requests.Last().Messages.Where(m => m.Role == ChatRole.Tool).ToArray();
        Assert.Equal(new[] { "a", "b", "write", "c", "d" }, results.Select(m => m.ToolCallId));
        Assert.Equal(new[] { "0:OLD", "1:OLD", "2:NEW", "3:NEW" },
            results.Where(m => m.ToolCallId != "write").Select(m => m.Content));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Unmarked_or_approval_requiring_reads_are_not_started_speculatively(bool parallel, bool approval)
    {
        using var fx = new EngineFixture();
        var first = Signal();
        var release = Signal();
        var count = 0;
        fx.Decisions.Answer = "allow";
        fx.ToolsOverride = [new Probe(async (_, _, ct) =>
        {
            if (Interlocked.Increment(ref count) == 1)
            { first.TrySetResult(); await release.Task.WaitAsync(ct); }
            return ToolResults.Ok("read");
        }, parallel, approval)];
        var provider = new FakeChatProvider(Turn.Says(Plan), new Turn(Calls:
            [new("a", "probe", "{}"), new("b", "probe", "{}")]), Turn.Says("done"));
        var run = fx.RunAsync(fx.Build(provider, EngineFixture.WorkerWith("probe")), "Read");
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.Equal(1, Volatile.Read(ref count)); }
        finally { release.TrySetResult(); }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, count);
        Assert.Equal(approval ? 2 : 0, fx.Decisions.Requests.Count);
    }

    [Fact]
    public async Task Large_read_response_runs_in_bounded_groups()
    {
        using var fx = new EngineFixture();
        var arrivals = new int[3];
        var ready = new[] { Signal(), Signal(), Signal() };
        var active = 0;
        var peak = 0;
        var sync = new object();
        fx.ToolsOverride = [new Probe(async (args, _, ct) =>
        {
            using var json = JsonDocument.Parse(args);
            var group = json.RootElement.GetProperty("id").GetInt32() / 4;
            lock (sync) { active++; peak = Math.Max(peak, active); }
            try
            {
                if (Interlocked.Increment(ref arrivals[group]) == (group == 2 ? 1 : 4)) ready[group].TrySetResult();
                await ready[group].Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                return ToolResults.Ok("read");
            }
            finally { lock (sync) active--; }
        })];
        var calls = Enumerable.Range(0, 9).Select(i => new ToolCall(i.ToString(), "probe", $"{{\"id\":{i}}}")).ToArray();
        var provider = new FakeChatProvider(Turn.Says(Plan), new Turn(Calls: calls), Turn.Says("done"));
        await fx.RunAsync(fx.Build(provider, EngineFixture.WorkerWith("probe")), "Read nine files");
        Assert.Equal(4, peak);
        Assert.Equal(new[] { 4, 4, 1 }, arrivals);
        Assert.Equal(9, provider.Requests.Last().Messages.Count(m => m.Role == ChatRole.Tool));
    }

    [Fact]
    public async Task Cancellation_waits_for_all_started_read_cleanup()
    {
        using var fx = new EngineFixture();
        using var cancellation = new CancellationTokenSource();
        var started = Signal();
        var cleaning = Signal();
        var release = Signal();
        var count = 0;
        var cleaned = 0;
        var registry = new ToolRegistry([new Probe(async (_, _, ct) =>
        {
            if (Interlocked.Increment(ref count) == 4) started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return ToolResults.Ok("never"); }
            finally { cleaning.TrySetResult(); await release.Task; Interlocked.Increment(ref cleaned); }
        })]);
        var run = ParallelToolReads.ExecuteAsync(Enumerable.Range(0, 4)
            .Select(i => new ToolCall(i.ToString(), "probe", "{}")).ToArray(), registry, fx.ContextFor(), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.False(run.IsCompleted); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(4, cleaned);
    }

    /// <summary>
    /// Every read of a batch is admitted before the batch runs, and one the admission does not let through is not made.
    /// The batch used to run first: the same read twice in a turn - made once, says the admission - was made twice and
    /// the second result thrown away. The others still run together, and the answers keep the calls' order.
    /// </summary>
    [Fact]
    public async Task A_read_the_admission_refuses_is_not_made_ahead_in_its_batch()
    {
        using var fx = new EngineFixture();
        var made = new List<int>();
        var bothIn = Signal();
        var arrived = 0;
        var probe = new Probe(async (args, _, ct) =>
        {
            using var json = JsonDocument.Parse(args);
            var id = json.RootElement.GetProperty("id").GetInt32();
            lock (made) made.Add(id);
            if (Interlocked.Increment(ref arrived) == 2) bothIn.TrySetResult();
            // The two reads that are made run together: each waits for the other to have started.
            await bothIn.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            return ToolResults.Ok($"read {id}");
        });
        fx.ToolsOverride = [probe];
        var calls = new[] { new ToolCall("a", "probe", "{\"id\":0}"), new("again", "probe", "{\"id\":0}"), new("b", "probe", "{\"id\":1}") };
        var provider = new FakeChatProvider(Turn.Says(Plan), new Turn(Calls: calls), Turn.Says("done"));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.WorkerWith("probe")), "Read twice");

        Assert.Equal(RunOutcomeKind.Completed, events.Last().Outcome());
        Assert.Equal(new[] { 0, 1 }, made.Order());
        var answers = provider.Requests.Last().Messages.Where(m => m.Role == ChatRole.Tool).ToArray();
        Assert.Equal(new[] { "a", "again", "b" }, answers.Select(m => m.ToolCallId));
        Assert.StartsWith("Not run: this is the same call", answers[1].Content);
        Assert.Equal(new[] { "read 0", "read 1" }, new[] { answers[0].Content, answers[2].Content });
    }
}
