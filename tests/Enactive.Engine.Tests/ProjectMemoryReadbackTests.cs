namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Memory;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// What one run concluded, read back by the next.
///
/// <para>The development spec carried this as the gap that made the memory store a write-only log:
/// "RunRecorder folds decisions into IMemoryStore and the window renders it, but WorkContext has no
/// memory field and the orchestrator never loads one. The next run does NOT know what the previous
/// one concluded." Two halves were missing, and only one of them was the one written down —
/// memory also held decisions ONLY, so in a workspace where nothing ever needed approving it held
/// nothing to read back.</para>
/// </summary>
public sealed class ProjectMemoryReadbackTests
{
    private sealed class InMemoryStore : IMemoryStore
    {
        private readonly List<MemoryEntry> _entries = new();

        public IReadOnlyList<MemoryEntry> Entries => _entries.ToArray();

        public Task AppendAsync(MemoryEntry entry, CancellationToken ct)
        {
            lock (_entries) _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken ct)
        {
            lock (_entries) return Task.FromResult<IReadOnlyList<MemoryEntry>>(_entries.ToArray());
        }
    }

    /// <summary>A store that cannot be read. Memory is best-effort; a run must not fail for it.</summary>
    private sealed class BrokenStore : IMemoryStore
    {
        public Task AppendAsync(MemoryEntry entry, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken ct)
            => throw new IOException("the memory file is locked");
    }

    private static MemoryEntry Entry(string kind, string content, int minutesAgo)
        => new(Guid.NewGuid(), Guid.NewGuid(), kind, content, null,
               DateTimeOffset.UtcNow.AddMinutes(-minutesAgo));

    // ── the reading half ────────────────────────────────────────────────────

    /// <summary>The context provider loads what the project remembers, oldest first.</summary>
    [Fact]
    public async Task The_context_carries_what_the_project_remembers()
    {
        using var fx = new EngineFixture();

        var store = new InMemoryStore();
        await store.AppendAsync(Entry(MemoryKind.Decision, "allowed run_command for this workspace", 30), default);
        await store.AppendAsync(Entry(MemoryKind.Outcome, "\"Add parser tests\" — Completed", 10), default);

        var context = await new ContextProvider(fx.Workspace, null, store)
            .BuildAsync(new IntentFocus(fx.Workspace.Id), default);

        Assert.Equal(2, context.Memory.Count);
        Assert.Equal(MemoryKind.Decision, context.Memory[0].Kind);   // oldest first
        Assert.Equal(MemoryKind.Outcome, context.Memory[1].Kind);
    }

    /// <summary>
    /// And it is BOUNDED. These go into the prompt, where they compete with the work itself for the
    /// context window; a workspace with two hundred runs behind it must not spend its window on its
    /// own history. Newest wins, because what the last few runs concluded is what a new one can act
    /// on.
    /// </summary>
    [Fact]
    public async Task Only_the_most_recent_entries_reach_a_run()
    {
        using var fx = new EngineFixture();

        var store = new InMemoryStore();
        for (var i = 60; i >= 1; i--)
            await store.AppendAsync(Entry(MemoryKind.Outcome, $"run number {i}", i), default);

        var context = await new ContextProvider(fx.Workspace, null, store)
            .BuildAsync(new IntentFocus(fx.Workspace.Id), default);

        Assert.Equal(ContextProvider.MemoryLimit, context.Memory.Count);

        // The newest survive, and they are still in order.
        Assert.Equal("run number 1", context.Memory[^1].Content);
        Assert.Contains("run number 20", context.Memory[0].Content);
    }

    /// <summary>
    /// A memory store that cannot be read does not fail the run. Best-effort, like the environment
    /// probe beside it: remembering is worth having and never worth stopping work for.
    /// </summary>
    [Fact]
    public async Task A_memory_store_that_throws_does_not_stop_a_run()
    {
        using var fx = new EngineFixture();

        var context = await new ContextProvider(fx.Workspace, null, new BrokenStore())
            .BuildAsync(new IntentFocus(fx.Workspace.Id), default);

        Assert.Empty(context.Memory);
        Assert.Equal(fx.Workspace.Id, context.WorkspaceId);
    }

    /// <summary>Nothing remembered adds nothing to the context — and no empty heading either.</summary>
    [Fact]
    public async Task An_empty_memory_adds_nothing()
    {
        using var fx = new EngineFixture();

        var context = await new ContextProvider(fx.Workspace, null, new InMemoryStore())
            .BuildAsync(new IntentFocus(fx.Workspace.Id), default);

        Assert.Empty(context.Memory);
    }

    // ── and what the model is NOT shown (2026-09-28) ────────────────────────

    /// <summary>
    /// Runs are self-contained: what the project remembers is kept, and shown in the window and the run
    /// history, but it is not put in front of the worker. It was all written by the engine - how earlier runs
    /// ended, approvals "for this run" - and run 9c1a061b was told "start from scratch, do not use earlier
    /// reports" beside fifteen earlier attempts at the same request, and went to read their report.
    /// </summary>
    [Fact]
    public async Task What_the_project_remembers_is_not_put_in_front_of_the_worker()
    {
        using var fx = new EngineFixture();

        var store = new InMemoryStore();
        await store.AppendAsync(Entry(MemoryKind.Outcome, "\"Add parser tests\" — Completed. Changed: tests/ParserTests.cs", 10), default);
        await store.AppendAsync(Entry(MemoryKind.Decision, "run_powershell: allowed outside, for this run", 5), default);

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"do the thing"}"""),
            Turn.Says("Done."));

        await fx.RunAsync(fx.Build(provider), "CURRENT_REQUEST_123", memory: store);

        var worker = string.Join("\n", provider.Requests[^1].Messages.Select(m => m.Content ?? ""));
        Assert.DoesNotContain("What this project has already decided", worker);
        Assert.DoesNotContain("tests/ParserTests.cs", worker);
        Assert.DoesNotContain("allowed outside", worker);
        Assert.Contains("CURRENT_REQUEST_123", worker);
        var planner = string.Join("\n", provider.Requests[0].Messages.Select(m => m.Content ?? ""));
        Assert.DoesNotContain("tests/ParserTests.cs", planner);
    }

    /// <summary>And the memory is still written: the next run's window and history have it.</summary>
    [Fact]
    public async Task The_next_run_does_not_see_the_previous_runs_outcome_but_the_store_keeps_it()
    {
        using var fx = new EngineFixture();
        var store = new InMemoryStore();
        var first = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"create remembered notes"}"""),
            Turn.Calls1("write_file", """{"path":"remembered.md","content":"hello"}"""),
            Turn.Says("Written."));
        await fx.RunAsync(fx.Build(first), "create remembered notes", memory: store);
        var second = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"explain notes"}"""), Turn.Says("Explained."));
        await fx.RunAsync(fx.Build(second), "explain notes", memory: store);

        Assert.Contains(store.Entries, e => e.Kind == MemoryKind.Outcome && e.Content.Contains("remembered.md"));
        Assert.DoesNotContain("remembered.md", string.Join("\n", second.Requests.SelectMany(r => r.Messages).Select(m => m.Content)));
    }

    // ── the writing half, which was thinner than §11 said ───────────────────

    /// <summary>
    /// A finished run writes down HOW IT ENDED. Until this, memory held decisions only — so a
    /// workspace where nothing ever needed approving remembered nothing at all, and the thing a
    /// later run most wants to know was the one thing never recorded.
    /// </summary>
    [Fact]
    public async Task A_finished_run_records_what_it_concluded()
    {
        using var fx = new EngineFixture();

        var store = new InMemoryStore();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write the notes"}"""),
            Turn.Calls1("write_file", """{"path":"notes.md","content":"hello"}"""),
            Turn.Says("Written."));

        await fx.RunAsync(fx.Build(provider), "write the notes", memory: store);

        var outcome = Assert.Single(store.Entries, e => e.Kind == MemoryKind.Outcome);
        Assert.Contains("notes.md", outcome.Content);
        Assert.Contains("Completed", outcome.Content);
    }

    /// <summary>And a run that failed says so — the memory is a record, not a highlight reel.</summary>
    [Fact]
    public async Task A_failed_run_records_that_it_failed_and_why()
    {
        using var fx = new EngineFixture();

        var store = new InMemoryStore();
        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"read what is not there"}"""),
            Turn.Calls1("read_file", """{"path":"nowhere.txt"}"""),
            Turn.Says("It is not there."));

        await fx.RunAsync(fx.Build(provider), "read what is not there", memory: store);

        var outcome = Assert.Single(store.Entries, e => e.Kind == MemoryKind.Outcome);
        Assert.DoesNotContain("Completed", outcome.Content);
    }
}
