namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Intents;
using Xunit;

/// <summary>
/// Plan Phase 3.1, anti-tamper. The worker's file tools cannot write <c>.enactive/</c>, but a shell command can - so a
/// run could change the manifests the NEXT run is judged by. Whatever a run adds, changes or deletes there is put back
/// when it ends, however it ends, and the run says which files and why. Through the real shell tool.
/// </summary>
public sealed class ARunCannotChangeTheChecksTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"tidy up"}""";

    private const string Lint = """{"id":"lint","command":"npm run lint","matches":["src/**"],"severity":"Blocking"}""";
    private const string Audit = """{"id":"audit","command":"npm audit","fireOn":"Run"}""";

    private static string Checks(string name) => Path.Combine(".enactive", "checks", name);

    private static EngineFixture WithManifests()
    {
        var fx = new EngineFixture { ProposeChecks = false };
        fx.Write(Checks("lint.json"), Lint);
        fx.Write(Checks("audit.json"), Audit);
        return fx;
    }

    private static Turn Shell(string command, string id) => Turn.Calls1("run_command", $$"""{"command":"{{command}}"}""", id);

    /// <summary>The run's sentence about the manifests, if it said one.</summary>
    private static WorkEvent? Restored(IEnumerable<WorkEvent> events)
        => events.SingleOrDefault(e => e.Kind == EventKind.ErrorObserved && e.Summary.Contains("check manifests", StringComparison.Ordinal));

    private static void AssertPutBack(EngineFixture fx)
    {
        Assert.Equal(Lint, fx.Read(Checks("lint.json")));
        Assert.Equal(Audit, fx.Read(Checks("audit.json")));
        Assert.False(fx.Exists(Checks("added.json")));
        // What lives below the folder is not a manifest, and is left alone.
        Assert.Equal(["audit.json", "lint.json"], Directory.GetFiles(fx.PathOf(Checks(""))).Select(Path.GetFileName).Order());
    }

    /// <summary>THE ONE THAT MATTERS: added, edited and deleted by shell commands - all three put back, all three named.</summary>
    [Fact]
    public async Task Manifests_a_shell_command_adds_edits_or_deletes_are_restored_and_named()
    {
        using var fx = WithManifests();
        fx.Write(Checks(Path.Combine("runs", "keep.txt")), "a check's output, not a manifest");
        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Shell(@"echo {}> .enactive\\checks\\added.json", "c1"),
            Shell(@"echo loosened> .enactive\\checks\\lint.json", "c2"),
            Shell(@"del .enactive\\checks\\audit.json", "c3"),
            Shell(@"echo more>> .enactive\\checks\\runs\\keep.txt", "c4"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), reviewRetries: 0), "tidy up");

        AssertPutBack(fx);
        var said = Restored(events);
        Assert.NotNull(said);
        Assert.Contains("added added.json", said!.Summary, StringComparison.Ordinal);
        Assert.Contains("changed lint.json", said.Summary, StringComparison.Ordinal);
        Assert.Contains("deleted audit.json", said.Summary, StringComparison.Ordinal);
        Assert.Contains("restored", said.Summary, StringComparison.Ordinal);
        Assert.Contains("a run may not change the checks that judge runs", said.Summary, StringComparison.Ordinal);
        // Said before the run's ending, so a host that stops reading at the end still hears it.
        Assert.True(events.IndexOf(said) < events.FindIndex(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed));
        Assert.Contains("more", fx.Read(Checks(Path.Combine("runs", "keep.txt"))), StringComparison.Ordinal);

        // What the run wrote is kept, below the folder where it judges nothing, and the run says where.
        var kept = Path.Combine("runs", events.Last().RunId.ToString(), "reverted");
        Assert.Contains($".enactive/checks/runs/{events.Last().RunId}/reverted/", said.Summary, StringComparison.Ordinal);
        Assert.Equal("{}", fx.Read(Checks(Path.Combine(kept, "added.json"))).Trim());
        Assert.Equal("loosened", fx.Read(Checks(Path.Combine(kept, "lint.json"))).Trim());
        Assert.False(fx.Exists(Checks(Path.Combine(kept, "audit.json"))));                 // deleted: nothing to keep
    }

    /// <summary>
    /// A resumed run is judged by its checkpoint's checks; manifests a person changed while it was interrupted are said -
    /// named - and left as the person left them: the snapshot keeps hashes, not bytes, and the change is theirs.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_says_which_manifests_changed_while_it_was_interrupted_and_leaves_them()
    {
        using var fx = WithManifests();
        fx.Checks = new Enactive.Workspace.CheckStore(fx.Root, fx.Root + "-no-global").Snapshot();
        fx.Pause = new RunPause();
        fx.Pause.Request();
        var store = new MemoryCheckpoints();
        var paused = await fx.RunAsync(fx.Build(new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") },
            EngineFixture.Role("developer"), reviewRetries: 0, checkpoints: store), "two things");
        Assert.Equal(RunOutcomeKind.Paused, paused.Last().Outcome());
        Assert.Null(Restored(paused));
        var checkpoint = Assert.Single(await store.LoadAllAsync(default));

        // While it waits, a person loosens one check, removes another and adds a third.
        fx.Write(Checks("lint.json"), Lint.Replace("Blocking", "Advisory"));
        File.Delete(fx.PathOf(Checks("audit.json")));
        fx.Write(Checks("spell.json"), """{"id":"spell","command":"cspell .","fireOn":"Run"}""");
        fx.Pause = null;

        var resumed = await fx.ResumeAsync(fx.Build(new FakeChatProvider { WhenExhausted = Turn.Says("step done") },
            EngineFixture.Role("developer"), reviewRetries: 0, checkpoints: store), checkpoint);

        var said = Assert.Single(resumed, e => e.Kind == EventKind.ErrorObserved
                                               && e.Summary.Contains("changed while the run was interrupted", StringComparison.Ordinal));
        Assert.Contains("changed lint.json", said.Summary, StringComparison.Ordinal);
        Assert.Contains("deleted audit.json", said.Summary, StringComparison.Ordinal);
        Assert.Contains("added spell.json", said.Summary, StringComparison.Ordinal);
        Assert.Null(Restored(resumed));                                                    // nothing put back
        Assert.Contains("Advisory", fx.Read(Checks("lint.json")), StringComparison.Ordinal);
        Assert.False(fx.Exists(Checks("audit.json")));
        Assert.True(fx.Exists(Checks("spell.json")));
    }

    [Fact]
    public async Task A_resumed_run_whose_manifests_did_not_change_says_nothing_about_them()
    {
        using var fx = WithManifests();
        fx.Checks = new Enactive.Workspace.CheckStore(fx.Root, fx.Root + "-no-global").Snapshot();
        fx.Pause = new RunPause();
        fx.Pause.Request();
        var store = new MemoryCheckpoints();
        await fx.RunAsync(fx.Build(new FakeChatProvider(Turn.Says(TwoStepPlan)) { WhenExhausted = Turn.Says("step done") },
            EngineFixture.Role("developer"), reviewRetries: 0, checkpoints: store), "two things");
        fx.Pause = null;

        var resumed = await fx.ResumeAsync(fx.Build(new FakeChatProvider { WhenExhausted = Turn.Says("step done") },
            EngineFixture.Role("developer"), reviewRetries: 0, checkpoints: store), Assert.Single(await store.LoadAllAsync(default)));

        Assert.DoesNotContain(resumed, e => e.Summary.Contains("changed while the run was interrupted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_that_does_not_touch_the_manifests_says_nothing_about_them()
    {
        using var fx = WithManifests();
        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Shell("echo hi> note.txt", "c1"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), reviewRetries: 0), "tidy up");

        Assert.True(fx.Exists("note.txt"));
        Assert.Null(Restored(events));
        AssertPutBack(fx);
    }

    [Fact]
    public async Task A_folder_created_by_a_run_where_there_was_none_is_emptied_again()
    {
        using var fx = new EngineFixture { ProposeChecks = false };
        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Shell(@"mkdir .enactive\\checks & echo {}> .enactive\\checks\\mine.json", "c1"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), reviewRetries: 0), "tidy up");

        Assert.False(fx.Exists(Checks("mine.json")));
        Assert.Contains("added mine.json", Restored(events)!.Summary, StringComparison.Ordinal);
    }

    private const string TwoStepPlan = """
        {"disposition":"task","title":"two steps",
         "steps":[{"title":"first","dependsOn":[]},{"title":"second","dependsOn":[0]}]}
        """;

    /// <summary>A run that ends PAUSED has ended too, for the manifests: what it changed is put back before it stops.</summary>
    [Fact]
    public async Task A_paused_run_puts_the_manifests_back()
    {
        using var fx = WithManifests();
        fx.Pause = new RunPause();
        var provider = new FakeChatProvider(
            Turn.Says(TwoStepPlan),
            Shell(@"del .enactive\\checks\\lint.json", "c1"),
            Turn.Says("first done"))
        { WhenExhausted = Turn.Says("step done") };
        // Asked for while the first step runs: it finishes, the second never starts.
        provider.Answering = r => { if (provider.Requests.Count == 2) fx.Pause!.Request(); return null; };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), reviewRetries: 0,
            checkpoints: new MemoryCheckpoints()), "two things");

        Assert.Equal(RunOutcomeKind.Paused, events.Last().Outcome());
        AssertPutBack(fx);
        Assert.Contains("deleted lint.json", Restored(events)!.Summary, StringComparison.Ordinal);
    }

    /// <summary>A run that is STOPPED unwinds with an exception, and still puts the manifests back and says so first.</summary>
    [Fact]
    public async Task A_stopped_run_puts_the_manifests_back()
    {
        using var fx = WithManifests();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Shell(@"del .enactive\\checks\\audit.json", "c1"));
        // The next turn after the command: the person presses Stop.
        provider.Answering = r => { if (provider.Requests.Count == 3) cts.Cancel(); return null; };

        var events = new List<WorkEvent>();
        var context = new WorkContext(fx.Workspace.Id, fx.Workspace.Name, null, null, null, [], []);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var ev in fx.Build(provider, EngineFixture.Role("developer"), reviewRetries: 0)
                               .SubmitIntentAsync(new Intent(Guid.NewGuid(), "tidy up", IntentSource.CommandBar, context, DateTimeOffset.UtcNow), cts.Token))
                events.Add(ev);
        });

        AssertPutBack(fx);
        Assert.Contains("deleted audit.json", Restored(events)!.Summary, StringComparison.Ordinal);
    }

    /// <summary>A person's own edit made BEFORE the run is what the run is held to - it is never "restored" away.</summary>
    [Fact]
    public async Task An_edit_made_before_the_run_stands()
    {
        using var fx = WithManifests();
        var orchestrator = fx.Build(new FakeChatProvider(Turn.Says(QuickAction), Turn.Says("Done.")), EngineFixture.Role("developer"), reviewRetries: 0);
        fx.Write(Checks("lint.json"), Lint.Replace("Blocking", "Advisory"));

        var events = await fx.RunAsync(orchestrator, "nothing to do");

        Assert.Null(Restored(events));
        Assert.Contains("Advisory", fx.Read(Checks("lint.json")), StringComparison.Ordinal);
    }

    private sealed class MemoryCheckpoints : Enactive.Core.History.IRunCheckpointStore
    {
        private readonly Dictionary<Guid, Enactive.Core.History.RunCheckpoint> _kept = new();

        public Task SaveAsync(Enactive.Core.History.RunCheckpoint checkpoint, CancellationToken ct)
        {
            lock (_kept) _kept[checkpoint.RunId] = checkpoint;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Enactive.Core.History.RunCheckpoint>> LoadAllAsync(CancellationToken ct)
        {
            lock (_kept) return Task.FromResult<IReadOnlyList<Enactive.Core.History.RunCheckpoint>>(_kept.Values.ToArray());
        }

        public Task<Enactive.Core.History.RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct)
        {
            lock (_kept) return Task.FromResult(_kept.GetValueOrDefault(runId));
        }

        public Task DeleteAsync(Guid runId, CancellationToken ct)
        {
            lock (_kept) _kept.Remove(runId);
            return Task.CompletedTask;
        }
    }
}

/// <summary>The guard on its own: what it finds, what it puts back, and that it is idempotent.</summary>
public sealed class CheckManifestGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-tests", Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(_root, ".enactive", "checks");

    public CheckManifestGuardTests() => Directory.CreateDirectory(Folder);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Nothing_changed_is_nothing_to_say_and_a_second_restore_finds_nothing()
    {
        File.WriteAllText(Path.Combine(Folder, "lint.json"), "{}");
        var guard = CheckManifestGuard.Take(_root);

        Assert.Null(guard.Restore().Describe());

        File.Delete(Path.Combine(Folder, "lint.json"));
        var once = guard.Restore();
        Assert.Equal([new CheckManifestChange("lint.json", CheckManifestChangeKind.Deleted)], once.Changes);
        Assert.Empty(once.Failures);
        Assert.Empty(guard.Restore().Changes);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(Folder, "lint.json")));
    }

    /// <summary>
    /// The state folder replaced by a junction that leads OUT of the workspace: restoring would delete and write files
    /// somewhere else entirely. Nothing is touched there; the change is still found and said, as not restored.
    /// </summary>
    [Fact]
    public void A_state_folder_that_leads_outside_the_workspace_is_never_written_through()
    {
        if (!OperatingSystem.IsWindows()) return;           // a junction is how a shell command would make one here
        var outside = _root + "-outside";
        var state = Path.Combine(_root, ".enactive");
        Directory.Delete(state, recursive: true);
        Directory.CreateDirectory(Path.Combine(outside, "checks"));
        File.WriteAllText(Path.Combine(outside, "checks", "lint.json"), "{}");
        try
        {
            Junction(state, outside);
            var guard = CheckManifestGuard.Take(_root);
            File.WriteAllText(Path.Combine(outside, "checks", "theirs.json"), "{}");

            var restore = guard.Restore(Guid.NewGuid());

            Assert.False(restore.Restored);
            Assert.Equal([new CheckManifestChange("theirs.json", CheckManifestChangeKind.Added)], restore.Changes);
            Assert.True(File.Exists(Path.Combine(outside, "checks", "theirs.json")));     // not deleted out there
            Assert.False(Directory.Exists(Path.Combine(outside, "checks", "runs")));     // nothing kept out there either
            Assert.Contains("NOT restored", restore.Describe(), StringComparison.Ordinal);
            Assert.Contains("outside the workspace", restore.Describe(), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(state); } catch { }                              // the junction, not what it points at
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    private static void Junction(string link, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    [Fact]
    public void A_whole_folder_deleted_is_made_again()
    {
        File.WriteAllText(Path.Combine(Folder, "lint.json"), "{\"id\":\"lint\"}");
        var guard = CheckManifestGuard.Take(_root);

        Directory.Delete(Folder, recursive: true);
        guard.Restore();

        Assert.Equal("{\"id\":\"lint\"}", File.ReadAllText(Path.Combine(Folder, "lint.json")));
    }
}
