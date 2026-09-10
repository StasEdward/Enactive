using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Diagnostics;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
using Enactive.Core.Schedules;
using Enactive.Core.Templates;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using Enactive.Providers;
using Enactive.Tools;
using Enactive.Workspace;

// Enactive — vertical slice host.
// Runs one command end to end:
//   Command -> Intent -> Context -> Orchestrator -> Worker -> Ollama/Qwen -> Tool -> ToolResult -> Artifact -> Event
//
// Usage:
//   dotnet run --project src/Enactive.App.Console -- "<command>" "<workspace path>"
//     [--autonomy observe|suggest|execute|autonomous]  the slider, same mapping as the window
//     [--role developer|reviewer|ops|writer]           which role, and so which tools at all
//     [--approve allow|deny]                           one fixed answer to every permission
//
// Those three exist so a behaviour can be checked from a command line rather than by driving the
// window by hand: a tier, a role and an answer are what a question about permissions is made of,
// and the last line of output names all three next to the outcome.
// Environment overrides:
//   ENACTIVE_MODEL       (default: qwen2.5-coder)
//   ENACTIVE_OLLAMA_URL  (default: http://localhost:11434/v1)

// "timeline" as the first argument shows the project's run history instead of running an intent:
//   dotnet run --project src/Enactive.App.Console -- timeline "<workspace path>"
var isTimeline = args.Length > 0 && string.Equals(args[0], "timeline", StringComparison.OrdinalIgnoreCase);

// ── Scheduled mode ────────────────────────────────────────────────────────────
//   Enactive.App.Console --template release-check --workspace c:\repos\Enactive [--report run.txt]
//                        [--param test_command="dotnet test" --param area=Parser]
//
// This is what makes a saved task a DAILY task: Windows Task Scheduler, cron or a pipeline step
// drives it. Nothing here is interactive, and that is enforced rather than assumed - see
// UnattendedDecisionHandler.
//
// --template names ONE task and runs it now. --due asks the saved schedules what is owed and runs
// what is: same machinery, and the difference is only who decides which task. See the --due block
// below and Docs/SCHEDULER_PLAN.md.
string? Option(string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}

// Every "--param id=value" on the command line. A template parameter with no default is a
// question, and unattended there is nobody to ask - so the scheduler answers it here, once, in the
// job definition. Without this a required parameter made a template un-schedulable, and the only
// way out was a default that was wrong for most projects (FIX_PLAN.md 9l, 9q).
Dictionary<string, string> Params()
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (!string.Equals(args[i], "--param", StringComparison.OrdinalIgnoreCase))
            continue;
        var pair = args[i + 1];
        var eq = pair.IndexOf('=');
        if (eq <= 0)
        {
            Console.Error.WriteLine($"--param expects id=value, got '{pair}'.");
            continue;
        }
        values[pair[..eq].Trim()] = pair[(eq + 1)..];
    }
    return values;
}

var templateId = Option("--template");
var reportPath = Option("--report");
var parameters = Params();

var baseUrl = Environment.GetEnvironmentVariable("ENACTIVE_OLLAMA_URL") ?? "http://localhost:11434/v1";
var model = Environment.GetEnvironmentVariable("ENACTIVE_MODEL") ?? "qwen2.5-coder";
var command = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
    ? args[0]
    : "Create a Python script named list_files.py in the current workspace that prints the list of files in the current directory.";
var workspaceRoot = Path.GetFullPath(
    Option("--workspace")
    ?? (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) && !args[1].StartsWith("--", StringComparison.Ordinal)
        ? args[1]
        : Directory.GetCurrentDirectory()));

Directory.CreateDirectory(workspaceRoot);

// ── Composition root (manual wiring — zero external NuGet packages) ──────────
// Adopt, not For: this host exists to RUN in the folder, so it is where the workspace's id gets
// written down beside it. The id written is the one the folder already had, so a workspace with a
// history keeps it - and from here on the folder can be renamed or moved without detaching it.
var workspace = WorkspaceInfo.Adopt(workspaceRoot);
var runStore = RunStoreFactory.Create(workspace);
var memoryStore = MemoryStoreFactory.Create(workspace);

// ── Timeline view: print the project's run history and exit ───────────────────
if (isTimeline)
{
    var runs = await runStore.LoadSummariesAsync(CancellationToken.None);
    var entries = await memoryStore.LoadAllAsync(CancellationToken.None);
    Console.WriteLine(ProjectMemory.Render(runs, entries, workspace.RootPath));
    return 0;
}

// "inbox" as the first argument prints the AI Inbox (background-task outcomes) and exits.
if (args.Length > 0 && string.Equals(args[0], "inbox", StringComparison.OrdinalIgnoreCase))
{
    var items = await InboxStoreFactory.Create(workspace).LoadAllAsync(CancellationToken.None);
    Console.WriteLine($"INBOX — {workspace.RootPath}");
    Console.WriteLine();
    if (items.Count == 0)
        Console.WriteLine("(empty)");
    else
        foreach (var item in items.OrderByDescending(x => x.At))
        {
            Console.WriteLine($"  {item.At.ToLocalTime():yyyy-MM-dd HH:mm}  [{item.Kind}] {item.Title}");
            Console.WriteLine($"        {item.Summary}");
        }
    return 0;
}

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

var descriptor = new ProviderDescriptor(
    Id: "ollama",
    DisplayName: "Ollama (local)",
    Kind: ProviderKind.OpenAiCompatible,
    BaseUrl: baseUrl,
    ApiKey: null,
    Models: new[] { model });

// ── Global log ────────────────────────────────────────────────────────────────
// Readable summaries + raw wire, mirrored to a daily file under %APPDATA%/Enactive/logs.
// ENACTIVE_LOG_LEVEL (Trace|Debug|Info|Warn|Error, default Debug) controls verbosity;
// set it to Trace to capture the raw HTTP request/response bodies.
var logLevel = Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("ENACTIVE_LOG_LEVEL"), ignoreCase: true, out var lv)
    ? lv : LogLevel.Debug;
var logFile = new FileLogSink();
using var logHub = new LogHub(minLevel: logLevel, downstream: new ILogSink[] { logFile });
logHub.Info(LogSource.System, $"Enactive console starting — provider={descriptor.DisplayName}, model={model}, log dir={FileLogSink.DefaultDirectory()}");

var providerFactory = new ChatProviderFactory(new[] { descriptor }, http, logHub);
var artifactStore = new DiskArtifactStore(workspace);
IToolRegistry toolRegistry = new LoggingToolRegistry(new ToolRegistry(new ITool[]
{
    new WriteFileTool(),
    new EditFileTool(),
    new ReadFileTool(),
    // Registered here because the roles name them. A tool a role names but the host does not
    // register is the same defect as a tool the host registers and no role names, seen from the
    // other side: the model is told about a capability that is not there.
    new SearchFilesTool(),
    new ListDirectoryTool(),
    new CreateDirectoryTool(),
    new MoveFileTool(),
    new CopyFileTool(),
    new DeleteFileTool(),
    new RunCommandTool(),
    new RunPowerShellTool(),
    new GitTool(),
    new DockerTool()
}), logHub);
var contextProvider = new ContextProvider(workspace, new EnvironmentProbe(), memoryStore);
var modelResolver = new ModelResolver();

var workers = DefaultWorkers.Build(new ModelRef("ollama", model));
var workerProvider = new StaticWorkerProvider(workers, DefaultWorkers.DefaultId);

// ── The role this run is given ────────────────────────────────────────────────
//   --role developer | reviewer | ops | writer
//
// Which role runs a task decides which tools it may call at all, and that is half of what there is
// to check about permissions: a task that reaches for a shell because the tool it needed was never
// granted looks exactly like a task the policy refused, and they are different facts.
var roleId = Option("--role");

if (roleId is { Length: > 0 }
    && !workers.Any(w => string.Equals(w.Id, roleId, StringComparison.OrdinalIgnoreCase)))
{
    // Named and refused, with the list. An unknown role silently falling back to the default would
    // produce a run under a role nobody asked for, reported as though it had been honoured.
    Console.Error.WriteLine($"There is no role '{roleId}'.");
    Console.Error.WriteLine($"  Roles: {string.Join(", ", workers.Select(w => w.Id))}");
    return 64;
}
var planner = new Planner();
var permissionEngine = new PermissionEngine();

// ── The tier this run acts under ──────────────────────────────────────────────
//   --autonomy observe | suggest | execute | autonomous   (or 0-3)
//
// From AutonomyTiers, which is the window's own mapping - not a policy written again here. This
// file used to carry a hard-coded one, roughly tier 2 and belonging to no slider position, so a
// run started from a command line was not running under any tier the app can be set to. Checking
// behaviour here then said something about the console rather than about the product.
//
// Execute by default, which is what the hard-coded policy was and what the app opens on.
var autonomyText = Option("--autonomy");
var autonomyTier = AutonomyTiers.Parse(autonomyText ?? "execute");

if (autonomyTier is null)
{
    // Refused rather than rounded: a typo must not become a tier in either direction.
    Console.Error.WriteLine($"'{autonomyText}' is not an autonomy tier.");
    Console.Error.WriteLine($"  Use one of: {string.Join(", ", AutonomyTiers.Names)} (or 0-{AutonomyTiers.Names.Count - 1}).");
    return 64;   // EX_USAGE: the invocation is wrong, not the work
}

var permissionPolicy = AutonomyTiers.PolicyFor(autonomyTier.Value);

// ── A saved task, run without a prompt ────────────────────────────────────────
ResolvedTaskSpec? spec = null;

// ── Whatever is due now ───────────────────────────────────────────────────────
//   Enactive.App.Console --due --workspace c:\repos\Enactive
//
// This is the tick. Something wakes it every few minutes - a Windows scheduled task registered
// once - and it asks the saved schedules what is due. See Docs/SCHEDULER_PLAN.md.
//
// It runs AT MOST ONE schedule per invocation, and that is a real limitation rather than an
// oversight: everything below this point is written for one run, and threading a second through it
// would be a larger change than the value at this stage. Two schedules due in the same minute means
// the second one runs on the next tick, a few minutes later. It is reported as such rather than
// silently deferred.
IDisposable? scheduleClaim = null;

// Set only when a SCHEDULE drove this invocation. It is what decides whether an Inbox item is filed
// at the end: a command somebody typed has somebody reading its output, and filing an item for it
// would fill the Inbox with things its owner has already seen.
string? scheduleName = null;

if (args.Contains("--due", StringComparer.OrdinalIgnoreCase))
{
    var schedules = ScheduleStore.Default.For(workspace.RootPath);
    var decisions = ScheduleTick.Decide(schedules, DateTimeOffset.Now, RunMarkers.Default.IsRunning);

    Console.WriteLine($"SCHEDULES — {workspace.RootPath}");
    foreach (var decision in decisions.Where(d => d.WorthReporting || d.ShouldRun))
        Console.WriteLine($"  [{decision.Verdict}] {decision.Schedule.Name}: {decision.Why}");

    // The most overdue first: if only one can go this tick, it should be the one that has been
    // waiting longest, not whichever the file happened to list first.
    var due = decisions.Where(d => d.ShouldRun).OrderBy(d => d.Occurrence).ToList();
    if (due.Count == 0)
    {
        Console.WriteLine(decisions.Count == 0 ? "  (no schedules)" : "  nothing due");
        return 0;
    }

    if (due.Count > 1)
        Console.WriteLine($"  {due.Count - 1} more due; they run on the next tick.");

    var chosen = due[0];

    // Claimed BEFORE anything else, so a second tick that overlaps this one sees it as busy. Null
    // means somebody claimed it between the decision and here, which is not an error - it is the
    // overlap rule working.
    scheduleClaim = RunMarkers.Default.Claim(chosen.Schedule.Id);
    if (scheduleClaim is null)
    {
        Console.WriteLine($"  [Busy] {chosen.Schedule.Name}: claimed by another run just now");
        return 0;
    }

    var resolved = ScheduledSpec.For(
        chosen.Schedule, workspace, id => new TemplateStore(workspaceRoot).Find(id));

    if (resolved.Spec is null)
    {
        // Reported and NOT retried in a loop: the occurrence is marked as fired below, so a
        // template that cannot resolve produces one report a day rather than one every tick.
        Console.Error.WriteLine($"  [Failed] {chosen.Schedule.Name}: {resolved.Why}");
        ScheduleStore.Default.Save(chosen.Schedule with { LastFiredAt = chosen.Occurrence });

        // Into the Inbox as well as onto stderr. Exit code 70 goes to a scheduler's log, and a
        // schedule that has quietly stopped working is precisely the thing whose owner should be
        // told rather than left to notice the work is not being done.
        await InboxStoreFactory.Create(workspace).AppendAsync(
            ScheduledOutcome.CouldNotStart(workspace.Id, chosen.Schedule.Name, resolved.Why, DateTimeOffset.UtcNow),
            CancellationToken.None);

        return 70;   // EX_SOFTWARE: the invocation was fine, the task could not be built
    }

    // Marked as fired NOW rather than when the run ends. An occurrence is considered exactly once,
    // and a machine that loses power mid-run must not repeat the same occurrence on the way back
    // up - the run is in the history either way, which is where a person looks.
    ScheduleStore.Default.Save(chosen.Schedule with { LastFiredAt = chosen.Occurrence });

    spec = resolved.Spec;
    command = spec.Goal;
    scheduleName = chosen.Schedule.Name;
    Console.WriteLine($"  running '{chosen.Schedule.Name}' — {chosen.Why}");
}

using var _scheduleClaim = scheduleClaim;

if (templateId is { Length: > 0 })
{
    var template = new TemplateStore(workspaceRoot).Find(templateId);
    if (template is null)
    {
        Console.Error.WriteLine($"No template '{templateId}' in {workspaceRoot} or the global library.");
        return 64;   // EX_USAGE: the invocation is wrong, not the work
    }

    var resolution = TemplateResolution.Resolve(template, workspace, permissionPolicy, parameters);
    if (resolution.Spec is null)
    {
        // Almost always a required parameter with no default. Unattended there is nobody to ask, so
        // the honest answer is to refuse the invocation rather than run a half-filled task.
        Console.Error.WriteLine($"'{template.Name}' cannot run unattended as it stands:");
        foreach (var problem in resolution.Problems)
            Console.Error.WriteLine("  - " + problem);
        Console.Error.WriteLine("Supply the missing parameters with --param id=value, give them a default in the template, or run it from the app.");
        return 64;
    }

    spec = resolution.Spec;
    command = spec.Goal;
}

// Unattended when a template drove it: nobody is at this console, and an approval nobody can give
// must not default to yes. See UnattendedDecisionHandler.
// ── How permission questions are answered ─────────────────────────────────────
//   --approve allow | deny
//
// A fixed answer, for a run nobody is sitting in front of. Without it a scenario that reaches an
// approval either blocks on a prompt or - worse - is refused by the end-of-input rule and reported
// as a run that could not do its work, when what was being checked was whether it CAN.
//
// Both directions matter and neither is a default. "deny" is how you check that a refusal really
// stops the action rather than being recorded after it; "allow" is how you get past a gate to
// check what is behind it. Left unset, this behaves exactly as before.
var approveText = Option("--approve");
var approve = approveText?.Trim().ToLowerInvariant();

if (approve is not (null or "allow" or "deny"))
{
    Console.Error.WriteLine($"'{approveText}' is not an answer. Use --approve allow or --approve deny.");
    return 64;
}

IDecisionHandler decisionHandler = approve switch
{
    "allow" or "deny" => new FixedDecisionHandler(approve),
    _ => spec is null ? new ConsoleDecisionHandler() : new UnattendedDecisionHandler()
};

var checkpointStore = new JsonCheckpointStore(workspace);

// ── Resume ───────────────────────────────────────────────────────────────────
//   Enactive.App.Console --resume [<run id>] --workspace c:\repos\Enactive
//
// A scheduled job that was killed - a machine restart, a build agent reclaimed mid-run - leaves a
// checkpoint. Without a way to pick it up from a command line the feature would exist only for
// somebody sitting in front of the window, which is the opposite of who needs it.
RunCheckpoint? resumeFrom = null;
if (args.Contains("--resume", StringComparer.OrdinalIgnoreCase))
{
    var wanted = Option("--resume");
    var open = (await checkpointStore.LoadAllAsync(CancellationToken.None))
        .Where(c => c.IsResumable)
        .ToArray();

    resumeFrom = Guid.TryParse(wanted, out var wantedId)
        ? open.FirstOrDefault(c => c.RunId == wantedId)
        // Newest first out of the store, so this is the one that stopped most recently.
        : open.FirstOrDefault();

    if (resumeFrom is null)
    {
        // Not an error: "there is nothing unfinished here" is the answer, and a scheduler that runs
        // this after every reboot should see 0 rather than a failure most of the time.
        Console.WriteLine(open.Length == 0
            ? $"Nothing unfinished in {workspace.RootPath}."
            : $"No unfinished run with id '{wanted}' in {workspace.RootPath}.");
        return 0;
    }

    command = resumeFrom.Request;
    Console.WriteLine(
        $"Resuming a run stopped on {resumeFrom.At.ToLocalTime():yyyy-MM-dd HH:mm}: "
        + $"{resumeFrom.Finished} of {resumeFrom.Steps.Count} step(s) were done.");
}

var orchestrator = new Orchestrator(
    providerFactory, modelResolver, workerProvider, toolRegistry,
    artifactStore, workspace, planner, permissionEngine, decisionHandler,
    spec?.Permissions ?? permissionPolicy, new EmptyServiceProvider(),
    successCriteria: spec?.SuccessCriteria, limits: spec?.Limits,
    checkpoints: checkpointStore,
    settings: resumeFrom?.Settings,
    // On, like the window. A scheduled run is exactly where a step that passed review on a report
    // nobody checked the reasoning of goes unnoticed - there is no one reading the transcript.
    checkSoundness: true);
var runRecorder = new RunRecorder(runStore, memoryStore, workspace.Id, spec: spec?.Snapshot());

// ── Run ──────────────────────────────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("Enactive — vertical slice");
Console.WriteLine($"  Workspace : {workspace.RootPath}");
Console.WriteLine($"  Provider  : {descriptor.DisplayName} ({baseUrl})");
Console.WriteLine($"  Model     : {model}");
Console.WriteLine($"  Command   : {command}");
Console.WriteLine($"  Autonomy  : {AutonomyTiers.Names[autonomyTier.Value]}");
Console.WriteLine($"  Role      : {roleId ?? DefaultWorkers.DefaultId}");
Console.WriteLine($"  Approvals : {approve ?? (spec is null ? "asked at this console" : "refused, unattended")}");
Console.WriteLine(new string('-', 72));

var focus = new IntentFocus(workspace.Id);
var workContext = await contextProvider.BuildAsync(focus, cts.Token);
var intent = new Intent(
    Guid.NewGuid(), command,
    // A scheduled run says so about itself. IntentSource.Schedule existed from the first version
    // and had never been used by anything.
    spec is null ? IntentSource.CommandBar : IntentSource.Schedule,
    // --role wins over a template's own worker: it is the more specific instruction, typed for
    // this invocation.
    workContext, DateTimeOffset.UtcNow, roleId ?? spec?.WorkerId);

// ── The scheduled run's copy of the result ────────────────────────────────────
// Exactly one Inbox item per scheduled run, whatever ending it reaches - including the ones that
// return early below, which are the endings a person is least likely to hear about otherwise. A
// scheduled run's stdout is a window nobody opened and a scheduler's log is gone by morning.
//
// Best-effort, and last: the run happened, and failing to file the paperwork does not unhappen it.
async Task FileScheduledOutcome(RunRecord? known = null)
{
    if (scheduleName is null)
        return;   // typed at a console - somebody is reading this

    try
    {
        var record = known;
        if (record is null)
        {
            // By header first, then the one record: reading every run whole to find the newest is
            // how a workspace pays for its history on every invocation.
            var header = (await runStore.LoadSummariesAsync(CancellationToken.None))
                .Where(r => r.TaskId == (resumeFrom?.TaskId ?? intent.Id))
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefault();

            if (header is not null)
                record = await runStore.LoadAsync(header.RunId, CancellationToken.None);
        }

        await InboxStoreFactory.Create(workspace).AppendAsync(
            ScheduledOutcome.For(workspace.Id, scheduleName, record, DateTimeOffset.UtcNow),
            CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not file the Inbox item for '{scheduleName}': {ex.Message}");
    }
}

var streaming = false;
RunOutcomeKind? outcome = null;
try
{
    var runStream = resumeFrom is null
        ? orchestrator.SubmitIntentAsync(intent, cts.Token)
        // A fresh context on purpose: what is on this machine is a fact about now, not about the
        // run that stopped.
        : orchestrator.ResumeRunAsync(resumeFrom, workContext, cts.Token);

    await foreach (var ev in runRecorder.RecordAsync(runStream.TeeToLog(logHub, cts.Token), cts.Token))
    {
        // Assistant text arrives token by token — print it inline as a live stream.
        if (ev.Kind == EventKind.AssistantDelta)
        {
            if (!streaming)
            {
                Console.Write("           assistant> ");
                streaming = true;
            }
            Console.Write(ev.Summary);
            continue;
        }

        if (streaming)
        {
            Console.WriteLine();
            streaming = false;
        }
        if (ev.Kind is EventKind.TaskCompleted or EventKind.TaskFailed)
            outcome = ev.Outcome();

        Console.WriteLine($"[{ev.At.ToLocalTime():HH:mm:ss}] {ev.Kind,-16} {ev.Summary}");
    }
    if (streaming)
        Console.WriteLine();
}
catch (HttpRequestException ex)
{
    Console.WriteLine();
    Console.WriteLine("x Could not reach the model provider.");
    Console.WriteLine($"  {ex.Message}");

    // The Ollama advice only when nothing better was said. It used to print on every provider
    // failure, so an Anthropic key rejected by Anthropic was answered with "Is Ollama running?" -
    // advice about the wrong program entirely, which is worse than none.
    if (ex is not ProviderUnreachableException)
        Console.WriteLine($"  Is Ollama running? Try:  ollama serve   and   ollama pull {model}");

    await FileScheduledOutcome();
    return 1;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
    await FileScheduledOutcome();
    return 130;
}
catch (Exception ex)
{
    // The backstop. A scheduled run's whole point is an exit code somebody can read, and an
    // unhandled exception gives a stack trace and whatever exit code the runtime picks - which to a
    // scheduler is indistinguishable from the machine losing power mid-job.
    //
    // Found on 2026-09-08 while verifying --param: a provider URL with a bad port threw
    // UriFormatException out of the provider during PLANNING, straight past the HttpRequestException
    // catch above, and the process died with a stack trace and no report. The two catches above stay
    // because each says something useful and specific; this one exists so that nothing gets out.
    Console.WriteLine();
    Console.Error.WriteLine("x The run stopped on an error nothing handled.");
    Console.Error.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine("  Check the provider settings and the log; nothing further was run.");
    await FileScheduledOutcome();
    return RunReport.ExitCodeFor(RunOutcomeKind.Failed);
}

Console.WriteLine(new string('-', 72));

// ── The report, and an exit code a scheduler can read ─────────────────────────
// A run nobody watched has to be able to say what happened, and the scheduler's own log is usually
// gone by the time anyone looks. So: the report to stdout always, to a file when asked.
// The intent id IS the task id, and this invocation made exactly one run under it.
//
// Found by header and then read whole: the report needs every event of THIS run, and none of any
// other. Reading them all to pick one was how a workspace with a long history paid for its history
// on every headless invocation.
var reportTaskId = resumeFrom?.TaskId ?? intent.Id;
var latest = (await runStore.LoadSummariesAsync(CancellationToken.None))
    .Where(r => r.TaskId == reportTaskId)
    .OrderByDescending(r => r.StartedAt)
    .FirstOrDefault();

var finished = latest is null
    ? null
    : await runStore.LoadAsync(latest.RunId, CancellationToken.None);

if (finished is not null)
{
    var report = RunReport.Render(finished, workspace.RootPath);
    Console.WriteLine(report);

    if (reportPath is { Length: > 0 })
    {
        try
        {
            var full = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, report, CancellationToken.None);
            Console.WriteLine($"Report written to {full}");
        }
        catch (Exception ex)
        {
            // The run happened; failing to file the paperwork does not unhappen it. Say so and let
            // the exit code still describe the WORK.
            Console.Error.WriteLine($"Could not write the report to '{reportPath}': {ex.Message}");
        }
    }

    outcome ??= RunReport.OutcomeOf(finished);
}

// The record is already in hand here, so it is handed over rather than looked up again.
await FileScheduledOutcome(finished);

// One line, last, in a shape something other than a person can read.
//
// The report above is for reading; this is for a scenario runner deciding what happened. The exit
// code already carries the outcome, but a code is a number in a shell variable and says nothing
// about which tier or role produced it - and a scenario's whole claim is usually about that pair.
Console.WriteLine(
    $"RESULT outcome={outcome ?? RunOutcomeKind.Incomplete} "
    + $"autonomy={AutonomyTiers.Names[autonomyTier.Value]} "
    + $"role={roleId ?? DefaultWorkers.DefaultId}");

Console.WriteLine("Done.");
return RunReport.ExitCodeFor(outcome ?? RunOutcomeKind.Incomplete);

// A no-op service provider: the slice's tools do not resolve anything from DI yet.
sealed class EmptyServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

/// <summary>
/// One answer, to every question, decided before the run started.
///
/// <para>For a run nobody is watching but somebody is checking. It is not the unattended handler:
/// that one files the question and refuses, which is right for a scheduled job and useless for a
/// scenario, because "the run could not do it" and "the run was not allowed to do it" then look the
/// same from outside.</para>
///
/// <para>It says WHY in the outcome, so the reason reaches the timeline and the report rather than
/// only the exit code.</para>
/// </summary>
sealed class FixedDecisionHandler(string answer) : IDecisionHandler
{
    public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
    {
        // The option that IS this answer, never a guess at one. A request whose options are not
        // allow/deny - a fork with named branches - gets the last option, which for every request
        // this engine builds is the conservative one.
        var chosen = request.Options.FirstOrDefault(
                         o => string.Equals(o.Id, answer, StringComparison.OrdinalIgnoreCase))
                     ?? request.Options[^1];

        Console.WriteLine();
        Console.WriteLine($"  !! USER DECISION REQUIRED — answering '{chosen.Id}' (--approve {answer})");
        Console.WriteLine($"  {request.Topic}");
        if (!string.IsNullOrEmpty(request.FullText))
            Console.WriteLine($"  {request.FullText}");

        return Task.FromResult(new DecisionOutcome(chosen.Id, $"--approve {answer}"));
    }
}

// Console approver for decision forks. A future Avalonia UI implements IDecisionHandler with a card.
sealed class ConsoleDecisionHandler : IDecisionHandler
{
    public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("  !! USER DECISION REQUIRED");
        Console.WriteLine($"  {request.Topic}");
        // FullText, not Detail: the console is approving the same action the UI is, and it must not
        // be asked to consent to a summary either.
        if (!string.IsNullOrEmpty(request.FullText))
            Console.WriteLine($"  {request.FullText}");
        foreach (var option in request.Options)
            Console.WriteLine($"    [{option.Id}] {option.Label}");
        Console.Write($"  Choose (default {request.RecommendedOptionId}): ");

        var line = Console.ReadLine();

        // NULL is end of input - there is no console, or nothing is attached to it. That is not
        // "the user pressed enter", and it must not become the RECOMMENDED option, which for a tool
        // approval is "allow": a run with nobody watching would then approve every command it was
        // asked about, silently, on the grounds that nobody objected. Nobody was there to object.
        if (line is null)
        {
            Console.WriteLine();
            Console.WriteLine("  (no input available — refusing, because nobody is here to approve it)");
            var refuse = request.Options.FirstOrDefault(
                             o => string.Equals(o.Id, "deny", StringComparison.OrdinalIgnoreCase))
                         ?? request.Options[^1];
            return Task.FromResult(new DecisionOutcome(refuse.Id));
        }

        // An empty line IS an answer: the person pressed enter at a prompt showing a default.
        var choice = string.IsNullOrWhiteSpace(line)
            ? request.RecommendedOptionId ?? request.Options[0].Id
            : line.Trim();

        var match = request.Options.FirstOrDefault(o => string.Equals(o.Id, choice, StringComparison.OrdinalIgnoreCase));
        var chosen = match?.Id ?? request.RecommendedOptionId ?? request.Options[0].Id;
        return Task.FromResult(new DecisionOutcome(chosen));
    }
}
