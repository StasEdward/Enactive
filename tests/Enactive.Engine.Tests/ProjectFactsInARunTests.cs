namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Memory;
using Xunit;

/// <summary>
/// The rule of <see cref="ProjectFacts"/> as a REAL run reaches it. §9ar.
///
/// <para>The sibling file checks the rule in isolation. This one checks that a run arrives at it —
/// which is the half that was actually broken, and the same lesson as §9aq: a sentence that is
/// tested and never reached is a test of nothing.</para>
/// </summary>
public sealed class ProjectFactsInARunTests
{
    private sealed class InMemoryStore : IMemoryStore
    {
        private readonly List<MemoryEntry> _entries = new();

        public IReadOnlyList<MemoryEntry> Entries { get { lock (_entries) return _entries.ToArray(); } }

        public Task AppendAsync(MemoryEntry entry, CancellationToken ct)
        {
            lock (_entries) _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult(Entries);
    }

    // ── the errands ─────────────────────────────────────────────────────────

    /// <summary>
    /// "Запусти калькулятор" — answered without opening the workspace at all. The project learned
    /// nothing and is told nothing.
    /// </summary>
    [Fact]
    public async Task A_run_that_never_opened_the_workspace_leaves_no_trace()
    {
        using var fx = new EngineFixture();
        var store = new InMemoryStore();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"launch the calculator"}"""),
            Turn.Says("Launched."));

        await fx.RunAsync(fx.Build(provider), "запусти калькулятор", memory: store);

        Assert.Empty(store.Entries);
    }

    /// <summary>
    /// And the same when a shell DID run. The shell always starts in the workspace root, so
    /// counting it would make every run "touch the workspace" and the test would decide nothing.
    /// </summary>
    [Fact]
    public async Task A_run_that_only_shelled_out_leaves_no_trace()
    {
        using var fx = new EngineFixture();
        var store = new InMemoryStore();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"find total commander"}"""),
            Turn.Calls1("run_command", """{"command":"where totalcmd"}"""),
            Turn.Says("Not found."));

        await fx.RunAsync(fx.Build(provider), "найди на компе Total Commander", memory: store);

        Assert.Empty(store.Entries);
    }

    // ── real project work ───────────────────────────────────────────────────

    /// <summary>
    /// The case the rule is shaped around: read, conclude, write nothing to disk. Real project
    /// knowledge with no artifact at all — which "changed a file" would have thrown away.
    /// </summary>
    [Fact]
    public async Task A_run_that_only_read_the_workspace_is_remembered()
    {
        using var fx = new EngineFixture();
        var store = new InMemoryStore();

        await File.WriteAllTextAsync(Path.Combine(fx.Root, "README.md"), "# the project");

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"check the readme"}"""),
            Turn.Calls1("read_file", """{"path":"README.md"}"""),
            Turn.Says("The README describes a flag the code no longer has."));

        await fx.RunAsync(fx.Build(provider), "check the readme", memory: store);

        var outcome = Assert.Single(store.Entries, e => e.Kind == MemoryKind.Outcome);
        Assert.Contains("check the readme", outcome.Content);
    }

    /// <summary>
    /// "сходи на сайт … — Completed Changed: news2.md" was an errand and IS kept: it left a file in
    /// the project, and this entry is the only surviving explanation of why that file is there.
    /// </summary>
    [Fact]
    public async Task An_errand_that_left_a_file_in_the_project_is_remembered()
    {
        using var fx = new EngineFixture();
        var store = new InMemoryStore();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"save the article"}"""),
            Turn.Calls1("write_file", """{"path":"news2.md","content":"the article"}"""),
            Turn.Says("Saved."));

        await fx.RunAsync(fx.Build(provider), "сходи на сайт и сохрани статью", memory: store);

        var outcome = Assert.Single(store.Entries, e => e.Kind == MemoryKind.Outcome);
        Assert.Contains("news2.md", outcome.Content);
    }

    // ── a standing answer is written once ───────────────────────────────────

    /// <summary>
    /// Two runs, same approval, one entry. In his log <c>git: denied</c> held six of the twenty
    /// places a run is given — six copies of one fact, crowding out five others.
    ///
    /// <para>The outcomes are NOT collapsed, and that is asserted here too: two runs of the same
    /// task are two real events, and a rule that folded them would be hiding history rather than
    /// de-duplicating a standing answer.</para>
    /// </summary>
    [Fact]
    public async Task A_standing_decision_is_recorded_once_however_often_it_is_asked()
    {
        using var fx = new EngineFixture();
        var store = new InMemoryStore();

        await File.WriteAllTextAsync(Path.Combine(fx.Root, "README.md"), "# the project");

        for (var run = 0; run < 2; run++)
        {
            // Shell first, so the approval is asked; then a read, so the run reaches the workspace
            // and its memory is written at all.
            var provider = new FakeChatProvider(
                Turn.Says("""{"disposition":"quick_action","title":"survey the project"}"""),
                Turn.Calls1("run_command", """{"command":"echo hello"}"""),
                Turn.Calls1("read_file", """{"path":"README.md"}"""),
                Turn.Says("Surveyed."));

            // Tier 2 — the tier the product is meant to be lived in, and the one that actually
            // ASKS about a command line. Under the harness's permissive default nothing is asked,
            // so no decision is recorded and this test would have had nothing to de-duplicate.
            await fx.RunAsync(
                fx.Build(provider, policy: AutonomyTiers.PolicyFor(2)),
                "survey the project", memory: store);
        }

        var decisions = store.Entries.Where(e => e.Kind == MemoryKind.Decision).ToArray();

        // Asserted BEFORE the uniqueness check, and not by folding the two together. Written as
        // Distinct().Count() == Length alone it is 0 == 0 for a run that recorded no decision at
        // all — green while proving nothing, which is how the wrong kind of test looks right.
        Assert.NotEmpty(decisions);
        Assert.Equal(decisions.Length, decisions.Select(d => d.Content).Distinct().Count());

        Assert.Equal(2, store.Entries.Count(e => e.Kind == MemoryKind.Outcome));
    }
}
