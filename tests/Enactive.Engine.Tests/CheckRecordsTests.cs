namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Agents;
using Enactive.Core;
using Enactive.Core.Checks;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Remote.Host;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// Plan Phase 3.3: what a declared check leaves behind. Its criterion event says, as values, which check it was, where it
/// fired, the boundary it belongs to, why it waited when it did, and where its whole output is kept - the summary keeps
/// the head and tail of what it printed, the detail file under <c>.enactive/checks/runs/&lt;runId&gt;/</c> keeps all of it,
/// and that folder goes with the run when the run is deleted. Old events read as they did.
/// </summary>
public sealed class CheckRecordsTests
{
    private const string OneStep = """{"disposition":"task","title":"app","steps":[{"title":"Write the app","dependsOn":[]}]}""";

    /// <summary>
    /// A failing command whose output is longer than the summary keeps: the middle of it is only in the detail file. It prints
    /// a file, so the marker is in what it printed and not in the command the summary also names.
    /// </summary>
    private static readonly string LongFailing = (OperatingSystem.IsWindows() ? "type" : "cat") + " long.txt && exit 1";

    private static void WriteLongOutput(EngineFixture fx)
        => fx.Write("long.txt", "START-" + new string('a', 900) + "-MIDDLE-MARKER-" + new string('b', 900) + "-END\n");

    private static ResolvedCheck Check(string id, string command, CheckFire fire, CheckSeverity severity, params string[] matches)
        => new(new CheckDefinition(id, null, command, matches, fire, severity, CheckLimits.DefaultTimeoutSeconds, null), CheckOrigin.Workspace);

    private static CheckSnapshot Snapshot(params ResolvedCheck[] checks) => new(checks, []);

    private static Turn Writes(string path, string content, string id)
        => Turn.Calls1("write_file", JsonSerializer.Serialize(new { path, content }), id);

    private static WorkEvent Criterion(IEnumerable<WorkEvent> events, string label)
        => Assert.Single(events, e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains(label, StringComparison.Ordinal));

    private static string RunFolder(Guid runId) => $".enactive/checks/runs/{runId:D}";

    /// <summary>
    /// THE ONE THAT MATTERS: a step check's event says its id, that it fired on the step, which step, and where its whole
    /// output is - and the file holds all of it, while the summary keeps only the start and the end.
    /// </summary>
    [Fact]
    public async Task A_step_check_says_its_id_where_it_fired_and_where_its_whole_output_is()
    {
        using var fx = new EngineFixture { Checks = Snapshot(Check("lint", LongFailing, CheckFire.Step, CheckSeverity.Advisory, "src/**")) };
        WriteLongOutput(fx);
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("src/app.txt", "v1", "w1"), Turn.Says("Wrote it."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "write the app");

        var ev = Criterion(events, "lint (step 1)");
        var check = ev.CriterionCheck();
        Assert.NotNull(check);
        Assert.Equal("lint", check!.CheckId);
        Assert.Equal(CheckBoundary.Step, check.FiredOn);
        Assert.Equal(1, check.Step);
        Assert.Null(check.Wave);
        Assert.Null(check.Deferred);
        Assert.Equal(RunFolder(ev.RunId) + "/step-1-lint.txt", check.DetailPath);
        // Said as values in the payload, under names a reader can rely on.
        Assert.Contains("\"firedOn\":\"step\"", ev.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"checkId\":\"lint\"", ev.PayloadJson, StringComparison.Ordinal);

        var detail = fx.Read(check.DetailPath!);
        Assert.Contains("START-", detail, StringComparison.Ordinal);
        Assert.Contains("-MIDDLE-MARKER-", detail, StringComparison.Ordinal);
        Assert.Contains("-END", detail, StringComparison.Ordinal);
        // The summary keeps its head-and-tail trim: the middle is not in it.
        Assert.Contains("START-", ev.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("-MIDDLE-MARKER-", ev.Summary, StringComparison.Ordinal);
    }

    /// <summary>A check tried again on the same step's second attempt keeps both outputs: the second does not overwrite the first.</summary>
    [Fact]
    public async Task A_check_run_again_on_a_second_attempt_keeps_each_attempts_output()
    {
        using var fx = new EngineFixture { Checks = Snapshot(Check("lint", "echo RUN-OUTPUT && exit 1", CheckFire.Step, CheckSeverity.Blocking, "src/**")) };
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("src/app.txt", "v1", "w1"), Turn.Says("Wrote it."),
            Writes("src/app.txt", "v2", "w2"), Turn.Says("Fixed it."));
        var reviewer = new FakeChatProvider(Verdicts.Pass());

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), router: Routers.WithReviewer(),
            reviewProvider: reviewer), "write the app");

        var paths = events.Where(e => e.Kind == EventKind.CriterionEvaluated && e.Summary.Contains("lint (step 1)", StringComparison.Ordinal))
            .Select(e => e.CriterionCheck()!.DetailPath!).ToArray();
        Assert.Equal(2, paths.Length);
        Assert.Equal(2, paths.Distinct().Count());
        Assert.All(paths, p => Assert.Contains("RUN-OUTPUT", fx.Read(p), StringComparison.Ordinal));
    }

    /// <summary>A check owed at a wave's end says it fired on the wave, which wave, and why it waited for it.</summary>
    [Fact]
    public async Task A_check_owed_at_a_wave_says_the_wave_and_why_it_waited()
    {
        using var fx = WavesAreValidatedAndAttributedTests.Wiki();
        fx.Checks = Snapshot(Check("lint", "echo WAVE-SAID-NO && exit 1", CheckFire.Step, CheckSeverity.Blocking, "pages/**"));
        var provider = new ByStepChatProvider(WavesAreValidatedAndAttributedTests.Plan(("Alpha writes page a", []), ("Beta writes page b", [])));
        provider.Step("Alpha writes page a", WavesAreValidatedAndAttributedTests.Writes("pages/a.page", "# A", "a1"), Turn.Says("Wrote a."));
        provider.Step("Beta writes page b", WavesAreValidatedAndAttributedTests.Writes("pages/b.page", "# B", "b1"), Turn.Says("Wrote b."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), maxParallelSteps: 2), "write the pages");

        var ev = Criterion(events, "lint (wave 1");
        var check = ev.CriterionCheck()!;
        Assert.Equal(CheckBoundary.Wave, check.FiredOn);
        Assert.Equal(1, check.Wave);
        Assert.Contains("owed at the end of wave 1", check.Deferred, StringComparison.Ordinal);
        Assert.Equal(RunFolder(ev.RunId) + "/wave-1-lint.txt", check.DetailPath);
        Assert.Contains("WAVE-SAID-NO", fx.Read(check.DetailPath!), StringComparison.Ordinal);
    }

    /// <summary>A run check says it fired on the run, and keeps its output in the run's own file.</summary>
    [Fact]
    public async Task A_run_check_says_it_fired_on_the_run()
    {
        using var fx = new EngineFixture { Checks = Snapshot(Check("audit", "echo AUDIT-OUTPUT && exit 0", CheckFire.Run, CheckSeverity.Advisory)) };
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("src/app.txt", "v1", "w1"), Turn.Says("Wrote it."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "write the app");

        var ev = Criterion(events, "audit (the run)");
        var check = ev.CriterionCheck()!;
        Assert.Equal("audit", check.CheckId);
        Assert.Equal(CheckBoundary.Run, check.FiredOn);
        Assert.Null(check.Step);
        Assert.Equal(RunFolder(ev.RunId) + "/run-audit.txt", check.DetailPath);
        Assert.Contains("AUDIT-OUTPUT", fx.Read(check.DetailPath!), StringComparison.Ordinal);
    }

    /// <summary>
    /// A payload written before checks had records - and any criterion that is not a declared check - reads as it always
    /// did: the same bytes, the same values, and no check.
    /// </summary>
    [Fact]
    public void A_criterion_payload_from_before_check_records_reads_as_before()
    {
        const string old = """{"criterion":"Builds","outcome":"Passed","required":true,"exitCode":0,"origin":"Declared"}""";
        Assert.Equal(old, WorkEventPayload.CriterionPayload("Builds", "Passed", true, 0));

        var ev = new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            EventKind.CriterionEvaluated, "PASS (exit 0) — Builds: dotnet build", old);
        Assert.Equal("Builds", ev.CriterionName());
        Assert.Equal("Passed", ev.CriterionOutcomeName());
        Assert.Null(ev.CriterionCheck());
        Assert.Null(WorkEventPayload.CheckIn(old));
        Assert.Null(WorkEventPayload.CheckIn(null));
        Assert.Null(WorkEventPayload.CheckIn("not json"));
    }

    /// <summary>
    /// A write-time check is a check like the others: a criterion event fired on write, with its step. The run's report
    /// shows it once per check and step - the latest, and how many times it ran - not a line for every turn; and a phone is
    /// not sent one every turn of an edit.
    /// </summary>
    [Fact]
    public async Task A_write_time_check_is_a_criterion_fired_on_write_and_the_report_folds_it()
    {
        using var fx = new EngineFixture
        {
            Checks = Snapshot(new ResolvedCheck(new CheckDefinition("spell", null, "echo SPELL-SAID && exit 1", ["notes/**"],
                CheckFire.Write, CheckSeverity.Advisory, 10, null), CheckOrigin.Workspace))
        };
        var worker = new FakeChatProvider(Turn.Says(OneStep), Writes("notes/a.md", "teh", "w1"), Writes("notes/a.md", "the", "w2"),
            Turn.Says("Wrote it."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "write the notes");

        var written = events.Where(e => e.Kind == EventKind.CriterionEvaluated && e.CriterionCheck()?.FiredOn == CheckBoundary.Write).ToArray();
        Assert.Equal(2, written.Length);
        Assert.All(written, e =>
        {
            Assert.StartsWith("Write-time check 'spell': FAIL", e.Summary, StringComparison.Ordinal);
            Assert.Equal("spell", e.CriterionCheck()!.CheckId);
            Assert.Equal(1, e.CriterionCheck()!.Step);
            Assert.Contains("SPELL-SAID", fx.Read(e.CriterionCheck()!.DetailPath!), StringComparison.Ordinal);
        });
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ContextAssembled && e.Summary.StartsWith("Write-time check 'spell'", StringComparison.Ordinal));
        Assert.All(written, e => Assert.Null(EventMapping.Progress(e)));

        var record = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "notes", null, DateTimeOffset.Now, DateTimeOffset.Now, "Completed",
            events.Select(e => new RunEventRecord(e.At, e.Kind.ToString(), e.Summary, null, e.PayloadJson)).ToArray(), [], []);
        var report = RunReport.Render(record, fx.Root);
        var lines = report.Split('\n').Where(l => l.Contains("Write-time check 'spell'", StringComparison.Ordinal)).ToArray();
        var line = Assert.Single(lines);
        Assert.Contains("the latest of 2 times it ran at write time in step 1", line, StringComparison.Ordinal);
    }

    /// <summary>A step or run check is still carried to a phone as before; only the write-time ones, every turn, are not.</summary>
    [Fact]
    public void A_step_check_is_still_carried_to_a_phone()
    {
        var ev = new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, EventKind.CriterionEvaluated,
            "FAIL (exit 1) — lint (step 1): lint", WorkEventPayload.CriterionPayload("lint (step 1)", "Failed", true, 1,
                check: new CheckFiring("lint", CheckBoundary.Step) { Step = 1 }));
        Assert.Equal(ev.Summary, EventMapping.Progress(ev));
    }

    // ── removed with the run ─────────────────────────────────────────────────────────────

    private static RunRecord Record() => new(
        Guid.NewGuid(), Guid.NewGuid(), "a run", "fake-model", DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now,
        "Completed", [], [], []);

    /// <summary>Deleting a run from a store that keeps it removes what its checks left behind - and nothing of another run's.</summary>
    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public async Task Deleting_a_run_removes_its_check_records_and_leaves_the_others(string kind)
    {
        using var fx = new EngineFixture();
        IRunStore store = kind == "json" ? new JsonRunStore(fx.Workspace) : new SqliteRunStore(fx.Workspace);
        var doomed = Record();
        var kept = Record();
        await store.SaveAsync(doomed, default);
        await store.SaveAsync(kept, default);
        fx.Write(Path.Combine(RunFolder(doomed.RunId), "step-1-lint.txt"), "doomed output");
        fx.Write(Path.Combine(RunFolder(doomed.RunId), "reverted", "lint.json"), "{}");
        fx.Write(Path.Combine(RunFolder(kept.RunId), "step-1-lint.txt"), "kept output");

        await store.DeleteAsync(doomed.RunId, default);

        Assert.False(Directory.Exists(fx.PathOf(RunFolder(doomed.RunId))));
        Assert.Equal("kept output", fx.Read(Path.Combine(RunFolder(kept.RunId), "step-1-lint.txt")));
        Assert.Null(await store.LoadAsync(doomed.RunId, default));
        Enactive.Tests.SqlitePools.ClearUnder(fx.Root);
    }

    /// <summary>A run with no record left behind still has its folder removed by its id; a run with no folder deletes cleanly.</summary>
    [Fact]
    public async Task Deleting_a_run_that_kept_no_check_records_is_fine()
    {
        using var fx = new EngineFixture();
        var store = new JsonRunStore(fx.Workspace);
        var run = Record();
        await store.SaveAsync(run, default);

        await store.DeleteAsync(run.RunId, default);

        Assert.Null(await store.LoadAsync(run.RunId, default));
    }

    /// <summary>
    /// CheckRecords.MaxDetailChars: a detail file is a check's whole output, but a command that prints without end would
    /// fill the disk under .enactive. Past the cap it keeps the start and the end, and says the middle was cut.
    /// </summary>
    [Fact]
    public void A_check_record_past_its_size_is_cut_and_says_so()
    {
        using var fx = new EngineFixture();
        var runId = Guid.NewGuid();
        var text = "HEAD-OF-OUTPUT\n" + new string('x', CheckRecords.MaxDetailChars + 1000) + "\nTAIL-OF-OUTPUT";

        var path = CheckRecords.WriteDetail(fx.Root, runId, "step-1", "lint", text);

        Assert.Equal(RunFolder(runId) + "/step-1-lint.txt", path);
        var kept = fx.Read(path!);
        Assert.True(kept.Length <= CheckRecords.MaxDetailChars + 200, $"kept {kept.Length}");
        Assert.StartsWith("HEAD-OF-OUTPUT", kept, StringComparison.Ordinal);
        Assert.EndsWith("TAIL-OF-OUTPUT", kept, StringComparison.Ordinal);
        Assert.Contains("characters", kept, StringComparison.Ordinal);
    }

    /// <summary>A state folder that leads outside the workspace is never written through, nor deleted through.</summary>
    [Fact]
    public void A_check_record_is_never_written_or_removed_through_a_link_leading_outside()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fx = new EngineFixture();
        var outside = fx.Root + "-outside";
        var runId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(outside, "runs", runId.ToString("D")));
        File.WriteAllText(Path.Combine(outside, "runs", runId.ToString("D"), "precious.txt"), "keep me");
        Directory.CreateDirectory(fx.PathOf(".enactive"));
        var link = fx.PathOf(Path.Combine(".enactive", "checks"));
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{outside}\"")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            p.WaitForExit();
        try
        {
            Assert.Null(CheckRecords.WriteDetail(fx.Root, runId, "step-1", "lint", "output"));
            CheckRecords.RemoveRun(fx.Root, runId);
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "runs", runId.ToString("D"), "precious.txt")));
            Assert.False(File.Exists(Path.Combine(outside, "runs", runId.ToString("D"), "step-1-lint.txt")));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }
}
