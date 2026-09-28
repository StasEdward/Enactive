namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Tasks;
using Xunit;
using static WavesAreValidatedAndAttributedTests;

/// <summary>
/// The two parts of Phase 6 left after the first cut: the waves carry on through a restart, and a
/// critical step runs alone and is validated the moment it ends.
/// </summary>
public sealed class WavesSurviveARestartAndCriticalStepsRunAloneTests
{
    private const string Gamma = "Gamma writes page c";

    /// <summary>Keeps every checkpoint, and - when told to - a copy of the wave folder as it was at that save.</summary>
    private sealed class Store(EngineFixture fx) : IRunCheckpointStore
    {
        public List<RunCheckpoint> Saved { get; } = [];
        public Dictionary<RunCheckpoint, string> Kept { get; } = new();

        public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken ct)
        {
            lock (Saved)
            {
                Saved.Add(checkpoint);
                var dir = WaveCapture.DirFor(fx.WaveStore, fx.Root, checkpoint.TaskId);
                if (checkpoint.Waves is { BeforeKept: true } && Directory.Exists(dir))
                {
                    var copy = Path.Combine(Path.GetTempPath(), "enactive-wave-kept-" + Guid.NewGuid().ToString("N"));
                    Copy(dir, copy);
                    Kept[checkpoint] = copy;
                }
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunCheckpoint>> LoadAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RunCheckpoint>>(Saved.ToArray());
        public Task<RunCheckpoint?> LoadAsync(Guid runId, CancellationToken ct) => Task.FromResult(Saved.LastOrDefault(c => c.RunId == runId));
        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static RunCheckpoint RoundTrip(RunCheckpoint checkpoint)
        => JsonSerializer.Deserialize<RunCheckpoint>(JsonSerializer.Serialize(checkpoint))!;

    private static ByStepChatProvider ThreePages()
    {
        var provider = new ByStepChatProvider(Plan((Alpha, []), (Beta, [0]), (Gamma, [1])));
        provider.Step(Alpha, Writes("pages/a.page", "# A", "a1"), Turn.Says("Wrote a."));
        provider.Step(Beta, Writes("pages/b.page", "BROKEN link", "b1"), Turn.Says("Wrote b."));
        provider.Step(Gamma, Writes("pages/c.page", "# C", "c1"), Turn.Says("Wrote c."));
        return provider;
    }

    private static ByStepChatProvider GammaOnly()
    {
        var provider = new ByStepChatProvider(Plan((Alpha, []), (Beta, [0]), (Gamma, [1]))) { Resumed = true };
        provider.Step(Gamma, Writes("pages/c.page", "# C", "c1"), Turn.Says("Wrote c."));
        return provider;
    }

    /// <summary>
    /// THE ONE THAT MATTERS for a restart: the wave after it is compared with where the last validated
    /// wave left things. Compared with the run's baseline instead, the error step 2 made would be put on
    /// step 3, the only step of its wave - a wrong culprit, named with confidence.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_compares_its_next_wave_with_the_last_validated_one()
    {
        using var fx = Wiki();
        var store = new Store(fx);
        await fx.RunAsync(fx.Build(ThreePages(), EngineFixture.Role("developer"), checkpoints: store), "write the pages");
        var afterTwo = RoundTrip(store.Saved.First(c => c.Finished == 2 && c.Waves is { Closed: 2, Open.Count: 0 }));

        File.Delete(Path.Combine(fx.Root, "pages", "c.page"));                 // as it was when the process died
        var events = await fx.ResumeAsync(fx.Build(GammaOnly(), EngineFixture.Role("developer"), checkpoints: store), afterTwo);

        Assert.Single(Lines(events, "Waves: carried on from the interrupted run - 2 wave(s) closed, 0 step(s) in the open one"));
        Assert.Empty(Lines(events, "regression"));
        Assert.Contains(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.StartsWith("PASS", StringComparison.Ordinal)
                                     && e.Summary.Contains("wave 3", StringComparison.Ordinal));
    }

    /// <summary>
    /// A wave that was open when the process died is validated first - before anything else runs - and
    /// with the files as it began, kept beside the checkpoint, its regression still has an owner.
    /// </summary>
    [Fact]
    public async Task The_open_wave_is_validated_first_and_its_starting_point_is_kept()
    {
        using var fx = Wiki();
        var store = new Store(fx);
        await fx.RunAsync(fx.Build(ThreePages(), EngineFixture.Role("developer"), checkpoints: store), "write the pages");
        var open = store.Saved.First(c => c.Finished == 2 && c.Waves is { Closed: 1, Open.Count: 1 });
        var kept = store.Kept[open];

        File.Delete(Path.Combine(fx.Root, "pages", "c.page"));
        Copy(kept, WaveCapture.DirFor(fx.WaveStore, fx.Root, open.TaskId));
        var events = await fx.ResumeAsync(fx.Build(GammaOnly(), EngineFixture.Role("developer"), checkpoints: store), RoundTrip(open));

        Assert.Single(Lines(events, "1 wave(s) closed, 1 step(s) in the open one"));
        var verdict = Assert.Single(Lines(events, "Wave 2 regression"));
        Assert.Contains($"is step 2 (\"{Beta}\")'s", verdict, StringComparison.Ordinal);
        var validated = events.FindIndex(e => e.Summary.StartsWith("Wave 2 (step 2)", StringComparison.Ordinal));
        var gamma = events.FindIndex(e => e.Kind == EventKind.StepStarted && e.Summary.Contains(Gamma, StringComparison.Ordinal));
        Assert.True(validated >= 0 && validated < gamma);
        Assert.Empty(Lines(events, "Wave 3 regression"));
        Assert.False(Directory.Exists(WaveCapture.DirFor(fx.WaveStore, fx.Root, open.TaskId)));   // an ending is not resumable
    }

    /// <summary>Without the kept files the open wave cannot be taken back, and the run says so instead of guessing.</summary>
    [Fact]
    public async Task Without_the_kept_files_the_open_wave_says_it_cannot_be_taken_back()
    {
        using var fx = Wiki();
        var store = new Store(fx);
        await fx.RunAsync(fx.Build(ThreePages(), EngineFixture.Role("developer"), checkpoints: store), "write the pages");
        var open = RoundTrip(store.Saved.First(c => c.Finished == 2 && c.Waves is { Closed: 1, Open.Count: 1 }));

        File.Delete(Path.Combine(fx.Root, "pages", "c.page"));
        var events = await fx.ResumeAsync(fx.Build(GammaOnly(), EngineFixture.Role("developer"), checkpoints: store), open);

        Assert.Single(Lines(events, "the files as the open wave began were not kept"));
        var verdict = Assert.Single(Lines(events, "Wave 2 regression"));
        Assert.Contains("attribution ambiguous", verdict, StringComparison.Ordinal);
    }

    /// <summary>A process that died in the middle of a trial build: the files a build reads are put back as the wave left them.</summary>
    [Fact]
    public async Task A_run_that_died_mid_trial_is_put_back_before_anything_else()
    {
        using var fx = Wiki();
        var store = new Store(fx);
        await fx.RunAsync(fx.Build(ThreePages(), EngineFixture.Role("developer"), checkpoints: store), "write the pages");
        var afterTwo = RoundTrip(store.Saved.First(c => c.Finished == 2 && c.Waves is { Closed: 2, Open.Count: 0 }));

        // As a trial left it: page b as it was before its wave, page c not yet written.
        File.Delete(Path.Combine(fx.Root, "pages", "c.page"));
        var asTheWaveLeftIt = WaveCapture.Take(fx.Root, [new Pages()]);
        asTheWaveLeftIt.Save(WaveCapture.TrialDirFor(fx.WaveStore, fx.Root, afterTwo.TaskId));
        File.Delete(Path.Combine(fx.Root, "pages", "b.page"));

        var events = await fx.ResumeAsync(fx.Build(GammaOnly(), EngineFixture.Role("developer"), checkpoints: store), afterTwo);

        Assert.Single(Lines(events, "stopped in the middle of a trial build: 1 file(s) the build reads were put back as the wave had left them (pages/b.page)"));
        Assert.Equal("BROKEN link", fx.Read("pages/b.page"));
        Assert.Empty(Lines(events, "regression"));
    }

    // ── a critical step ─────────────────────────────────────────────────────────────

    /// <summary>A critical step runs with nothing beside it and is built the moment it ends; what is independent of it waits.</summary>
    [Fact]
    public async Task A_critical_step_runs_alone_and_is_validated_as_soon_as_it_ends()
    {
        using var fx = Wiki();
        const string Plan3 = """
            {"disposition":"task","title":"pages","steps":[
              {"title":"Alpha writes page a","dependsOn":[]},
              {"title":"Beta writes page b","dependsOn":[],"critical":true},
              {"title":"Gamma writes page c","dependsOn":[]}]}
            """;
        var provider = new ByStepChatProvider(Plan3);
        provider.Step(Alpha, Writes("pages/a.page", "# A", "a1"), Turn.Says("Wrote a."));
        provider.Step(Beta, Writes("pages/b.page", "BROKEN link", "b1"), Turn.Says("Wrote b."));
        provider.Step(Gamma, Writes("pages/c.page", "# C", "c1"), Turn.Says("Wrote c."));

        var events = (await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), maxParallelSteps: 3), "write the pages")).ToList();

        var started = events.Where(e => e.Kind == EventKind.StepStarted).Select(e => events.IndexOf(e)).ToArray();
        var beta = events.FindIndex(e => e.Kind == EventKind.StepStarted && e.Summary.Contains(Beta, StringComparison.Ordinal));
        var betaDone = events.FindIndex(e => e.Kind == EventKind.StepCompleted && e.Summary.Contains(Beta, StringComparison.Ordinal));
        var validated = events.FindIndex(e => e.Summary.StartsWith("Wave 1 (step 2, critical - validated as soon as it ended)", StringComparison.Ordinal));
        Assert.Equal(beta, started[0]);                                           // before anything else
        Assert.True(betaDone < validated, string.Join("\n", events.Select(e => e.Summary)));
        Assert.All(started.Skip(1), i => Assert.True(i > validated));             // nothing beside it, nothing before its build
        Assert.Contains($"is step 2 (\"{Beta}\")'s", Assert.Single(Lines(events, "Wave 1 regression")), StringComparison.Ordinal);
        Assert.Single(Lines(events, "Wave 2 (steps 1 and 3)"));                   // the rest run together after it
    }

    [Fact]
    public void A_critical_step_waits_for_what_is_running_and_then_goes_alone()
    {
        var a = new PlanStep(Guid.NewGuid(), "a", StepStatus.Pending, []);
        var b = new PlanStep(Guid.NewGuid(), "b", StepStatus.Pending, [a.Id]) { Critical = true };
        var c = new PlanStep(Guid.NewGuid(), "c", StepStatus.Pending, []);
        var d = new PlanStep(Guid.NewGuid(), "d", StepStatus.Pending, [a.Id]);
        var scheduler = new DagScheduler(new Plan(Guid.NewGuid(), [a, b, c, d]));

        Assert.Equal([a, c], scheduler.NextReadyBatch(4));
        scheduler.MarkDone(a.Id);
        Assert.Empty(scheduler.NextReadyBatch(4, running: 1));                   // b is ready, c still runs: nothing new, d included
        scheduler.MarkDone(c.Id);
        Assert.Equal([b], scheduler.NextReadyBatch(4, running: 0));              // alone
        Assert.Empty(scheduler.NextReadyBatch(4, running: 1));
        scheduler.MarkDone(b.Id);
        Assert.Equal([d], scheduler.NextReadyBatch(4, running: 0));
    }

    [Fact]
    public void The_planner_hears_of_critical_steps_only_when_waves_are_validated()
    {
        Assert.Contains("\"critical\":true", Planner.SystemPromptFor(null, validateWaves: true), StringComparison.Ordinal);
        Assert.DoesNotContain("critical", Planner.SystemPromptFor(null), StringComparison.Ordinal);
    }
}
