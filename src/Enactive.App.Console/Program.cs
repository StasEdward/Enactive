using Enactive.Agents;
using Enactive.Core.Context;
using Enactive.Core.Diagnostics;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Intents;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;
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
var workspaceName = Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
if (string.IsNullOrEmpty(workspaceName))
    workspaceName = "workspace";

var workspace = new WorkspaceInfo(WorkspaceInfo.IdFor(workspaceRoot), workspaceName, workspaceRoot);
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
    new RunCommandTool(),
    new RunPowerShellTool(),
    new GitTool(),
    new DockerTool()
}), logHub);
var contextProvider = new ContextProvider(workspace, new EnvironmentProbe(), memoryStore);
var modelResolver = new ModelResolver();

var workerProvider = new StaticWorkerProvider(
    DefaultWorkers.Build(new ModelRef("ollama", model)),
    DefaultWorkers.DefaultId);
var planner = new Planner();
var permissionEngine = new PermissionEngine();

// Workspace autonomy policy: everything is allowed at Execute level, but run_command always asks first.
var permissionPolicy = new PermissionPolicy(
    PermissionLevel.Execute,
    Allow: new[] { "*" },
    AskBefore: new[] { "run_command", "run_powershell", "git", "docker" });

// ── A saved task, run without a prompt ────────────────────────────────────────
ResolvedTaskSpec? spec = null;
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
IDecisionHandler decisionHandler = spec is null
    ? new ConsoleDecisionHandler()
    : new UnattendedDecisionHandler();

var orchestrator = new Orchestrator(
    providerFactory, modelResolver, workerProvider, toolRegistry,
    artifactStore, workspace, planner, permissionEngine, decisionHandler,
    spec?.Permissions ?? permissionPolicy, new EmptyServiceProvider(),
    successCriteria: spec?.SuccessCriteria, limits: spec?.Limits);
var runRecorder = new RunRecorder(runStore, memoryStore, workspace.Id, spec: spec?.Snapshot());

// ── Run ──────────────────────────────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("Enactive — vertical slice");
Console.WriteLine($"  Workspace : {workspace.RootPath}");
Console.WriteLine($"  Provider  : {descriptor.DisplayName} ({baseUrl})");
Console.WriteLine($"  Model     : {model}");
Console.WriteLine($"  Command   : {command}");
Console.WriteLine(new string('-', 72));

var focus = new IntentFocus(workspace.Id);
var workContext = await contextProvider.BuildAsync(focus, cts.Token);
var intent = new Intent(
    Guid.NewGuid(), command,
    // A scheduled run says so about itself. IntentSource.Schedule existed from the first version
    // and had never been used by anything.
    spec is null ? IntentSource.CommandBar : IntentSource.Schedule,
    workContext, DateTimeOffset.UtcNow, spec?.WorkerId);

var streaming = false;
RunOutcomeKind? outcome = null;
try
{
    await foreach (var ev in runRecorder.RecordAsync(orchestrator.SubmitIntentAsync(intent, cts.Token).TeeToLog(logHub, cts.Token), cts.Token))
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
    Console.WriteLine($"  Is Ollama running? Try:  ollama serve   and   ollama pull {model}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
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
var latest = (await runStore.LoadSummariesAsync(CancellationToken.None))
    .Where(r => r.TaskId == intent.Id)
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

Console.WriteLine("Done.");
return RunReport.ExitCodeFor(outcome ?? RunOutcomeKind.Incomplete);

// A no-op service provider: the slice's tools do not resolve anything from DI yet.
sealed class EmptyServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
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
