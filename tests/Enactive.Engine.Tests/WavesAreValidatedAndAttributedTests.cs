namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Builds;
using Enactive.Core.Events;

using Xunit;

/// <summary>
/// Phase 6: a plan's steps are validated once per wave - where nothing is running - and a regression
/// found there is put on the step that made it, or said to be ambiguous, and why.
///
/// <para>Deliberately not .NET. The "build" here finds the word BROKEN in the wiki's <c>.page</c> files,
/// and every line it prints is a diagnostic - which is all an ecosystem is to the engine. Each run of it
/// also leaves a line in <c>builds.log</c>, which no ecosystem owns, so a test can count the builds.</para>
/// </summary>
public sealed class WavesAreValidatedAndAttributedTests
{
    private sealed class Pages(string extra = "") : IEcosystem
    {
        public string Name => "pages";

        public EcosystemTargets? Detect(string workspaceRoot)
            => File.Exists(Path.Combine(workspaceRoot, "pages.lint")) ? new(Name, ["pages.lint"], []) : null;

        public bool Owns(string relativePath) => relativePath.EndsWith(".page", StringComparison.OrdinalIgnoreCase);

        public string BuildCommand(string target)
            => "echo x>>builds.log & " + (extra.Length > 0 ? $"({extra}) & " : "") + "findstr /s /n /c:\"BROKEN\" *.page";

        public string TestCommand(string target) => "echo none";

        // "pages\b.page:3:BROKEN link" - the line number is for a person, not part of what the error IS.
        public IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string output, string workspaceRoot)
            => output.Split('\n').Select(l => l.Trim()).Where(l => l.Contains(":BROKEN", StringComparison.Ordinal))
                .Select(l =>
                {
                    var parts = l.Split(':', 3);
                    return new BuildDiagnostic(Name, parts[0].Replace('\\', '/'), "P1", DiagnosticSeverity.Error, parts[2].Trim(),
                        int.TryParse(parts[1], out var line) ? line : null);
                }).ToArray();
    }

    private const string Alpha = "Alpha writes page a";
    private const string Beta = "Beta writes page b";

    private static string Plan(params (string Title, int[] After)[] steps)
        => "{\"disposition\":\"task\",\"title\":\"pages\",\"steps\":["
           + string.Join(",", steps.Select(s => $"{{\"title\":\"{s.Title}\",\"dependsOn\":[{string.Join(",", s.After)}]}}"))
           + "]}";

    private static EngineFixture Wiki(string extra = "")
    {
        var fx = new EngineFixture { EcosystemsOverride = [new Pages(extra)], ValidateWaves = true };
        fx.Write("pages.lint", "rules");
        fx.Write("pages/home.page", "# Home\n");
        return fx;
    }

    private static Turn Writes(string path, string content, string id)
        => Turn.Calls1("write_file", $$"""{"path":"{{path}}","content":"{{content}}"}""", id);

    private static int Builds(EngineFixture fx)
        => File.Exists(Path.Combine(fx.Root, "builds.log")) ? fx.Read("builds.log").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;

    private static IEnumerable<string> Lines(IEnumerable<WorkEvent> events, string text)
        => events.Where(e => e.Summary.Contains(text, StringComparison.Ordinal)).Select(e => e.Summary);

    /// <summary>THE DoD: a parallel wave is built once, not once per step, and the end of the run does not build it again.</summary>
    [Fact]
    public async Task A_parallel_wave_that_broke_nothing_is_built_once()
    {
        using var fx = Wiki();
        var provider = new ByStepChatProvider(Plan((Alpha, []), (Beta, [])));
        provider.Step(Alpha, Writes("pages/a.page", "# A", "a1"), Turn.Says("Wrote a."));
        provider.Step(Beta, Writes("pages/b.page", "# B", "b1"), Turn.Says("Wrote b."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), maxParallelSteps: 2), "write the pages");

        Assert.Single(Lines(events, "validating it once"));
        Assert.Contains(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.StartsWith("PASS", StringComparison.Ordinal)
                                     && e.Summary.Contains("wave 1", StringComparison.Ordinal));
        Assert.Equal(2, Builds(fx));                                             // the baseline, and the wave
        Assert.DoesNotContain(events, e => e.Summary.Contains("regression", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A step that builds on a wave starts after the wave is validated, not with it. A step marks itself done
    /// a moment before its task ends, and the dependent was dispatched in that moment - the wave then ran on
    /// into it, and was validated with the dependent's changes in it.
    /// </summary>
    [Fact]
    public async Task What_builds_on_a_wave_starts_after_it_is_validated()
    {
        const string Gamma = "Gamma builds on both";
        for (var run = 0; run < 5; run++)
        {
            using var fx = Wiki();
            var provider = new ByStepChatProvider(Plan((Alpha, []), (Beta, []), (Gamma, [0, 1])));
            provider.Step(Alpha, Writes("pages/a.page", "# A", "a1"), Turn.Says("Wrote a."));
            provider.Step(Beta, Writes("pages/b.page", "# B", "b1"), Turn.Says("Wrote b."));
            provider.Step(Gamma, Writes("pages/c.page", "# C", "c1"), Turn.Says("Wrote c."));

            var events = (await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), maxParallelSteps: 2), "write the pages")).ToList();

            var validated = events.FindIndex(e => e.Summary.StartsWith("Wave 1 (steps 1 and 2)", StringComparison.Ordinal));
            var started = events.FindIndex(e => e.Kind == EventKind.StepStarted && e.Summary.Contains(Gamma, StringComparison.Ordinal));
            Assert.True(validated >= 0 && validated < started, string.Join("\n", events.Select(e => e.Summary)));
            Assert.Single(Lines(events, "Wave 2 (step 3)"));
        }
    }

    /// <summary>THE ONE THAT MATTERS: two steps in one wave, one breaks the build, and the engine says which.</summary>
    [Fact]
    public async Task A_regression_in_a_parallel_wave_is_put_on_the_step_that_made_it()
    {
        using var fx = Wiki();
        var provider = new ByStepChatProvider(Plan((Alpha, []), (Beta, [])));
        provider.Step(Alpha, Writes("pages/a.page", "# A", "a1"), Turn.Says("Wrote a."));
        provider.Step(Beta, Writes("pages/b.page", "BROKEN link", "b1"), Turn.Says("Wrote b."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), maxParallelSteps: 2), "write the pages");

        var verdict = Assert.Single(Lines(events, "Wave 1 regression"));
        Assert.Contains($"is step 2 (\"{Beta}\")'s", verdict, StringComparison.Ordinal);
        Assert.Contains("alone reproduce it; no other step's do (2 trial build(s))", verdict, StringComparison.Ordinal);
        // Tried, and put back exactly as the wave left it.
        Assert.Equal("BROKEN link", fx.Read("pages/b.page"));
        Assert.Equal("# A", fx.Read("pages/a.page"));
    }

    /// <summary>
    /// One step at a time: each step is a wave, a step alone in its wave is the cause without a trial,
    /// and the wave after it is compared with where it left things - not blamed for its error again.
    /// </summary>
    [Fact]
    public async Task Each_wave_is_compared_with_the_one_before_it()
    {
        using var fx = Wiki();
        const string Gamma = "Gamma writes page c";
        var provider = new ByStepChatProvider(Plan((Alpha, []), (Beta, [0]), (Gamma, [1])));
        provider.Step(Alpha, Writes("pages/a.page", "# A", "a1"), Turn.Says("Wrote a."));
        provider.Step(Beta, Writes("pages/b.page", "BROKEN link", "b1"), Turn.Says("Wrote b."));
        provider.Step(Gamma, Writes("pages/c.page", "# C", "c1"), Turn.Says("Wrote c."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write the pages");

        Assert.Empty(Lines(events, "Wave 1 regression"));
        var verdict = Assert.Single(Lines(events, "Wave 2 regression"));
        Assert.Contains("made the only changes to what the build reads in this wave", verdict, StringComparison.Ordinal);
        Assert.DoesNotContain("trial", verdict, StringComparison.Ordinal);
        Assert.Empty(Lines(events, "Wave 3 regression"));
        Assert.Contains(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.StartsWith("PASS", StringComparison.Ordinal)
                                     && e.Summary.Contains("wave 3", StringComparison.Ordinal));
        Assert.Equal(4, Builds(fx));                                             // baseline + one per wave, none at the end
    }

    [Fact]
    public async Task A_wave_that_wrote_nothing_a_build_reads_is_not_built()
    {
        using var fx = Wiki();
        var provider = new ByStepChatProvider(Plan((Alpha, [])));
        provider.Step(Alpha, Writes("notes.txt", "a note", "a1"), Turn.Says("Wrote a note."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write a note");

        Assert.Single(Lines(events, "changed nothing a build reads: no validation needed"));
        Assert.Equal(1, Builds(fx));                                             // the baseline only
    }

    /// <summary>An error that has only moved down the file is the error it was: no regression, no attribution.</summary>
    [Fact]
    public async Task A_diagnostic_that_only_moved_is_not_new()
    {
        using var fx = Wiki();
        fx.Write("pages/home.page", "BROKEN link");
        var provider = new ByStepChatProvider(Plan((Alpha, [])));
        provider.Step(Alpha, Writes("pages/home.page", "# Home\\n\\nintro\\nBROKEN link", "a1"), Turn.Says("Added an intro."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "add an intro");

        Assert.Contains(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.StartsWith("PASS", StringComparison.Ordinal)
                                     && e.Summary.Contains("wave 1", StringComparison.Ordinal));
        Assert.Empty(Lines(events, "regression"));
    }

    [Fact]
    public async Task Without_the_switch_there_are_no_waves()
    {
        using var fx = Wiki();
        fx.ValidateWaves = false;
        var provider = new ByStepChatProvider(Plan((Alpha, [])));
        provider.Step(Alpha, Writes("pages/a.page", "BROKEN link", "a1"), Turn.Says("Wrote a."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "write a page");

        Assert.Empty(Lines(events, "Wave "));
        Assert.Equal(2, Builds(fx));                                             // the baseline and the run's end, as before
    }

    // ── the attribution on its own ──────────────────────────────────────────────────────

    private static WaveStep Step(int no, params string[] wrote) => new(Guid.NewGuid(), no, $"s{no}", wrote, false);

    /// <summary>
    /// A file two steps both wrote is not one step's alone: the store keeps them from overwriting each other,
    /// but edits of one file by two steps leave content neither made alone, and a trial cannot split it.
    /// </summary>
    /// <summary>
    /// Review finding P1: a trial build that wrote a file the wave had not changed left it behind. Here
    /// every build appends to builds.log, and the trial with page a and without page b writes a page of
    /// its own - one the build READS. Neither outlives the trials, and neither reaches the next trial.
    /// </summary>
    [Fact]
    public async Task What_a_trial_build_writes_anywhere_does_not_outlive_it()
    {
        using var fx = Wiki(@"if exist pages\a.page if not exist pages\b.page (echo # gen>pages\gen.page)");
        var provider = new ByStepChatProvider(Plan((Alpha, []), (Beta, [])));
        provider.Step(Alpha, Writes("pages/a.page", "# A", "a1"), Turn.Says("Wrote a."));
        provider.Step(Beta, Writes("pages/b.page", "BROKEN link", "b1"), Turn.Says("Wrote b."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), maxParallelSteps: 2), "write the pages");

        Assert.Contains("(2 trial build(s))", Assert.Single(Lines(events, "Wave 1 regression")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fx.Root, "pages", "gen.page")));
        Assert.Equal(2, Builds(fx));                                             // the trials' lines are gone with them
        Assert.Empty(Lines(events, "could not be put back"));
    }

    /// <summary>What cannot be put back stops the run: no step builds on a workspace the engine's trials changed.</summary>
    [Fact]
    public async Task A_workspace_that_cannot_be_put_back_stops_the_run()
    {
        const string After = "Gamma builds on both";
        using var fx = Wiki(@"if exist pages\a.page if not exist pages\b.page (echo x>stuck.txt & attrib +r stuck.txt)");
        try
        {
            var provider = new ByStepChatProvider(Plan((Alpha, []), (Beta, []), (After, [0, 1])));
            provider.Step(Alpha, Writes("pages/a.page", "# A", "a1"), Turn.Says("Wrote a."));
            provider.Step(Beta, Writes("pages/b.page", "BROKEN link", "b1"), Turn.Says("Wrote b."));
            provider.Step(After, Turn.Says("Built on them."));

            var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), maxParallelSteps: 2), "write the pages");

            Assert.Contains(events, e => e.Kind == EventKind.ErrorObserved
                                         && e.Summary.Contains("could not be put back as the wave left it (stuck.txt)", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Kind == EventKind.StepCompleted && e.Summary.Contains(After, StringComparison.Ordinal)
                                         && e.Summary.Contains("skipped", StringComparison.Ordinal));
            Assert.DoesNotContain(provider.Requests, r => r.Messages.Any(m => m.Content?.Contains(After, StringComparison.Ordinal) == true
                                                                               && m.Content.Contains("Proceed with this step", StringComparison.Ordinal)));
        }
        finally
        {
            var stuck = Path.Combine(fx.Root, "stuck.txt");
            if (File.Exists(stuck)) File.SetAttributes(stuck, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Two_steps_that_wrote_the_same_file_are_an_ambiguous_attribution()
    {
        var steps = new[] { Step(1, "s.page"), Step(2, "s.page", "b.page") };
        var result = await WaveLedger.AttributeAsync(steps, new HashSet<string> { "s.page", "b.page" },
            files => Task.FromResult<bool?>(files.Contains("s.page")));
        Assert.True(result.Ambiguous);
        Assert.Equal(2, result.Suspects.Count);
    }

    [Fact]
    public async Task One_step_whose_changes_alone_reproduce_it_is_the_cause()
    {
        var steps = new[] { Step(1, "a.page"), Step(2, "b.page"), Step(3, "c.page") };
        var result = await WaveLedger.AttributeAsync(steps, new HashSet<string> { "a.page", "b.page", "c.page" },
            files => Task.FromResult<bool?>(files.Contains("c.page")));
        Assert.Equal(3, result.Culprit!.No);
        Assert.Equal(3, result.Trials);
    }

    [Fact]
    public async Task Changes_that_only_break_together_are_ambiguous()
    {
        var steps = new[] { Step(1, "a.page"), Step(2, "b.page") };
        var result = await WaveLedger.AttributeAsync(steps, new HashSet<string> { "a.page", "b.page" }, _ => Task.FromResult<bool?>(false));
        Assert.True(result.Ambiguous);
        Assert.Contains("it takes them together", result.Explanation, StringComparison.Ordinal);
        Assert.Equal(2, result.Trials);
    }

    [Fact]
    public async Task Changes_no_step_recorded_are_tried_as_their_own_and_never_blamed_on_a_step()
    {
        var steps = new[] { Step(1, "a.page"), new WaveStep(Guid.NewGuid(), 2, "s2", [], UnrecordedWrites: true) };
        var result = await WaveLedger.AttributeAsync(steps, new HashSet<string> { "a.page", "gen.page" },
            files => Task.FromResult<bool?>(files.Contains("gen.page")));
        Assert.True(result.Ambiguous);
        Assert.Contains("no step recorded writing (gen.page) - a command of step 2", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trial_that_cannot_run_decides_nothing()
    {
        var steps = new[] { Step(1, "a.page"), Step(2, "b.page") };
        var result = await WaveLedger.AttributeAsync(steps, new HashSet<string> { "a.page", "b.page" }, _ => Task.FromResult<bool?>(null));
        Assert.True(result.Ambiguous);
        Assert.Contains("could not run", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_the_build_reads_changed_puts_it_outside_the_wave()
    {
        var result = await WaveLedger.AttributeAsync([Step(1, "notes.txt")], new HashSet<string>(), _ => throw new InvalidOperationException());
        Assert.True(result.Ambiguous);
        Assert.Contains("outside it", result.Explanation, StringComparison.Ordinal);
    }
}
