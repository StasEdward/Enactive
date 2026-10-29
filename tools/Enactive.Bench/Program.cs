// Enactive live benchmark - plan phase 0, amendment A (0b).
//
// Runs each scenario in bench/scenarios on a fresh copy of its fixture, with the engine as the person configured it
// (the console host, the same settings.json as the window), then judges the run by the scenario's truth - what is on
// disk afterwards - and compares the engine's outcome with it: a run the engine called done whose truth fails is a
// FALSE PASS, a run it did not whose truth holds is a FALSE FAIL.
//
//   dotnet run --project tools/Enactive.Bench -- [--only a,b] [--timeout 40] [--console path\enactive.exe]
//                                                 [--save-baseline] [--fixtures]
//
// --fixtures runs no model: it checks every scenario's truth against its untouched fixture, which must FAIL - a
// scenario whose truth already holds before the work measures nothing. --solutions checks it against the fixture with
// the scenario's reference solution laid over it, which must HOLD - a truth that turns down right work would call a
// right run a false one. Both on a copy in the temp folder, never in the scenario's own. A live run asks the second
// first, scenario by scenario, and does not run one whose truth turns its solution down here (Readiness).

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Enactive.Bench;
using Enactive.Core.Context;
using Enactive.Workspace;

string? Option(string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
    return null;
}
bool Flag(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

var repo = FindRepo(Directory.GetCurrentDirectory())
    ?? throw new InvalidOperationException("Run from inside the Enactive repository: no bench/scenarios found above here.");
var scenariosRoot = Option("--scenarios") ?? Path.Combine(repo, "bench", "scenarios");
var resultsRoot = Option("--out") ?? Path.Combine(repo, "bench", "results");
var only = Option("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var timeout = TimeSpan.FromMinutes(double.TryParse(Option("--timeout"), out var minutes) ? minutes : 40);
// Confirmed rules every run is given (Plan Phase 5 measurement): a JSON array of rule texts, kept in each run's workspace.
var rulesFile = Option("--rules") is { } rf ? Path.GetFullPath(rf) : null;

var scenarios = Directory.GetDirectories(scenariosRoot)
    .Where(d => File.Exists(Path.Combine(d, "scenario.json")))
    .Select(Scenario.Load)
    .Where(s => only is null || only.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
    .OrderBy(s => s.Name, StringComparer.Ordinal)
    .ToArray();
if (scenarios.Length == 0) { Console.Error.WriteLine("No scenarios to run."); return 2; }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
// Where a truth is tried on a fixture or a solution: in the temp folder, never in the scenario's own.
var truthCheck = Path.Combine(Path.GetTempPath(), "enactive-bench", "truth-check");

// --judge <workspace>, with --only naming its scenario: the truth checked again on a workspace a run left - after the
// truth itself was corrected, say - without running anything again.
if (Option("--judge") is { } judged)
{
    if (scenarios.Length != 1) { Console.Error.WriteLine("--judge needs --only with exactly one scenario."); return 2; }
    var checks = await Truth.CheckAsync(scenarios[0], judged, cts.Token);
    var holds = Truth.Holds(checks);
    Console.WriteLine($"{scenarios[0].Name}: truth {(holds ? "holds" : "fails")} on {judged}");
    foreach (var c in checks) Console.WriteLine($"   {(c.Passed ? "pass" : "FAIL")}  {c.Id}: {c.Detail}");
    return holds ? 0 : 1;
}

if (Flag("--fixtures") || Flag("--solutions"))
{
    var solved = Flag("--solutions");
    var bad = 0;
    foreach (var scenario in scenarios)
    {
        IReadOnlyList<CheckResult> checks;
        if (solved)
        {
            if (await Readiness.SolutionChecksAsync(scenario, truthCheck, cts.Token) is not { } onSolution)
            {
                Console.WriteLine($"{scenario.Name}: no reference solution, skipped");
                continue;
            }
            checks = onSolution;
        }
        else
        {
            var copy = Path.Combine(truthCheck, scenario.Name);
            Readiness.CopyTree(scenario.FixtureFolder, copy);
            checks = await Truth.CheckAsync(scenario, copy, cts.Token);
        }
        var holds = Truth.Holds(checks);
        var right = holds == solved;
        if (!right) bad++;
        Console.WriteLine($"{scenario.Name}: truth {(holds ? "holds" : "fails")}"
            + (right ? ", as it should" : solved ? " - it TURNS DOWN the reference solution" : " - it ALREADY HOLDS, this scenario measures nothing"));
        foreach (var c in checks) Console.WriteLine($"   {(c.Passed ? "pass" : "FAIL")}  {c.Id}: {c.Detail}");
    }
    return bad == 0 ? 0 : 1;
}

var commit = (await Truth.RunAsync("cmd", "git rev-parse --short HEAD", repo, cts.Token)).Output.Trim();
var dirty = (await Truth.RunAsync("cmd", "git status --porcelain -- src", repo, cts.Token)).Output.Trim().Length > 0;
var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss}-{commit}{(dirty ? "-dirty" : "")}";
var outDir = Path.Combine(resultsRoot, stamp);
Directory.CreateDirectory(outDir);

var console = Option("--console") ?? await BuildConsoleAsync(repo, cts.Token);
Console.WriteLine($"Engine: {console}  (commit {commit}{(dirty ? ", with uncommitted changes in src" : "")})");

var results = new List<ScenarioResult>();
foreach (var scenario in scenarios)
{
    if (cts.IsCancellationRequested) break;
    Console.WriteLine($"\n== {scenario.Name} ==");
    if (await Readiness.NotRunAsync(scenario, truthCheck, cts.Token) is { } notRun)
    {
        results.Add(notRun);
        Console.WriteLine($"   {notRun.Verdict} - {notRun.Note}");
        foreach (var c in notRun.Checks.Where(c => !c.Passed)) Console.WriteLine($"   FAIL  {c.Id}: {c.Detail}");
        continue;
    }
    results.Add(await RunScenarioAsync(scenario));
    var last = results[^1];
    Console.WriteLine($"   {last.Outcome}, truth {(last.TruthHolds ? "holds" : "fails")} -> {last.Verdict}  ({last.Metrics?.Seconds ?? last.WallSeconds:F0} s)");
}

var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var summary = new BenchSummary(stamp, commit, dirty, DateTimeOffset.Now, results);
await File.WriteAllTextAsync(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(summary, json));

var baselinePath = Path.Combine(resultsRoot, "baseline.json");
BenchSummary? baseline = null;
if (File.Exists(baselinePath))
    baseline = JsonSerializer.Deserialize<BenchSummary>(await File.ReadAllTextAsync(baselinePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
var markdown = Summary.Render(summary, baseline);
await File.WriteAllTextAsync(Path.Combine(outDir, "summary.md"), markdown);
Console.WriteLine("\n" + markdown);
Console.WriteLine($"Results: {outDir}");

if (Flag("--save-baseline"))
{
    File.Copy(Path.Combine(outDir, "results.json"), baselinePath, overwrite: true);
    Console.WriteLine($"Saved as the baseline: {baselinePath}");
}
// A scenario not run fails the bench as a false verdict does: a script that reads only the exit code must not take a
// machine that could judge nothing for an engine that got everything right.
return results.Any(r => r.Verdict.StartsWith("FALSE", StringComparison.Ordinal) || r.Verdict == Readiness.NotRun) ? 1 : 0;

async Task<ScenarioResult> RunScenarioAsync(Scenario scenario)
{
    var scenarioOut = Path.Combine(outDir, scenario.Name);
    Directory.CreateDirectory(scenarioOut);
    var work = Path.Combine(Path.GetTempPath(), "enactive-bench", stamp, scenario.Name);
    Readiness.CopyTree(scenario.FixtureFolder, work);

    // Under git, as a project usually is: the engine measures what a step changed by it.
    foreach (var git in new[] { "git init -q", "git config user.email bench@enactive.local", "git config user.name bench",
                 "git config core.autocrlf false", "git add -A", "git commit -q -m fixture" })
        await Truth.RunAsync("cmd", git, work, cts.Token);
    if (rulesFile is not null)
        Console.WriteLine($"   given {await Rules.SeedAsync(work, rulesFile, cts.Token)} rule(s) from {rulesFile}");

    var clock = Stopwatch.StartNew();
    var psi = new ProcessStartInfo(console)
    {
        ArgumentList = { scenario.Request, work, "--autonomy", scenario.EffectiveAutonomy, "--approve", scenario.Approve,
                         "--report", Path.Combine(scenarioOut, "report.txt") },
        WorkingDirectory = work,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };
    string? note = null;
    int? exit = null;
    await using (var log = new StreamWriter(Path.Combine(scenarioOut, "console.log"), false, Encoding.UTF8))
    {
        using var process = Process.Start(psi)!;
        var gate = new object();
        void Write(string? line) { if (line is not null) lock (gate) log.WriteLine(line); }
        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        limit.CancelAfter(timeout);
        try { await process.WaitForExitAsync(limit.Token); exit = process.ExitCode; }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            note = cts.IsCancellationRequested ? "stopped by hand" : $"timed out after {timeout.TotalMinutes:F0} min";
        }
    }
    var wall = clock.Elapsed.TotalSeconds;

    RunMetrics? metrics = null;
    try
    {
        var store = RunStoreFactory.Create(WorkspaceInfo.Adopt(work));
        var latest = (await store.LoadSummariesAsync(cts.Token)).OrderByDescending(r => r.StartedAt).FirstOrDefault();
        if (latest is not null && await store.LoadAsync(latest.RunId, cts.Token) is { } record) metrics = RunMetrics.Of(record);
    }
    catch (Exception ex) when (ex is not OperationCanceledException) { note = (note is null ? "" : note + "; ") + $"no run record: {ex.Message}"; }

    var checks = await Truth.CheckAsync(scenario, work, cts.Token);
    var holds = Truth.Holds(checks);
    var outcome = metrics?.Outcome ?? "none";
    var saidDone = string.Equals(outcome, scenario.Expect.Outcome, StringComparison.OrdinalIgnoreCase);
    var verdict = (holds, saidDone) switch
    {
        (true, true) => "right PASS",
        (true, false) => "FALSE FAIL",
        (false, true) => "FALSE PASS",
        (false, false) => "right FAIL"
    };
    // Where the workspace was left, from the temp folder on: results.json is what a baseline is made of, and a baseline
    // is kept in git - with no user's name in a path.
    var left = Path.Combine("%TEMP%", Path.GetRelativePath(Path.GetTempPath(), work));
    return new ScenarioResult(scenario.Name, outcome, scenario.Expect.Outcome, holds, verdict, exit, wall, note, metrics, checks, left);
}

static async Task<string> BuildConsoleAsync(string repo, CancellationToken ct)
{
    // Outside the repository, so a scenario that builds a .NET project never meets this build's locked files.
    var target = Path.Combine(Path.GetTempPath(), "enactive-bench", "console");
    Console.WriteLine($"Building the engine's console host into {target} ...");
    var (exit, output) = await Truth.RunAsync("cmd", $"dotnet build \"{Path.Combine(repo, "src", "Enactive.App.Console")}\" -c Release -nologo -o \"{target}\"", repo, ct);
    if (exit != 0) throw new InvalidOperationException("The console host did not build:\n" + output);
    return Path.Combine(target, "enactive.exe");
}

static string? FindRepo(string from)
{
    for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, "bench", "scenarios"))) return dir.FullName;
    return null;
}
