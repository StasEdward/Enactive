using AIClient.Agents;
using AIClient.Core.Context;
using AIClient.Core.Events;
using AIClient.Core.History;
using AIClient.Core.Intents;
using AIClient.Core.Permissions;
using AIClient.Core.Providers;
using AIClient.Core.Tools;
using AIClient.Core.Workers;
using AIClient.Providers;
using AIClient.Tools;
using AIClient.Workspace;

// AIClient — vertical slice host.
// Runs one command end to end:
//   Command -> Intent -> Context -> Orchestrator -> Worker -> Ollama/Qwen -> Tool -> ToolResult -> Artifact -> Event
//
// Usage:
//   dotnet run --project src/AIClient.App.Console -- "<command>" "<workspace path>"
// Environment overrides:
//   AICLIENT_MODEL       (default: qwen2.5-coder)
//   AICLIENT_OLLAMA_URL  (default: http://localhost:11434/v1)

// "timeline" as the first argument shows the project's run history instead of running an intent:
//   dotnet run --project src/AIClient.App.Console -- timeline "<workspace path>"
var isTimeline = args.Length > 0 && string.Equals(args[0], "timeline", StringComparison.OrdinalIgnoreCase);

var baseUrl = Environment.GetEnvironmentVariable("AICLIENT_OLLAMA_URL") ?? "http://localhost:11434/v1";
var model = Environment.GetEnvironmentVariable("AICLIENT_MODEL") ?? "qwen2.5-coder";
var command = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
    ? args[0]
    : "Create a Python script named list_files.py in the current workspace that prints the list of files in the current directory.";
var workspaceRoot = Path.GetFullPath(args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
    ? args[1]
    : Directory.GetCurrentDirectory());

Directory.CreateDirectory(workspaceRoot);

// ── Composition root (manual wiring — zero external NuGet packages) ──────────
var workspaceName = Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
if (string.IsNullOrEmpty(workspaceName))
    workspaceName = "workspace";

var workspace = new WorkspaceInfo(Guid.NewGuid(), workspaceName, workspaceRoot);
var runStore = RunStoreFactory.Create(workspace);

// ── Timeline view: print the project's run history and exit ───────────────────
if (isTimeline)
{
    var runs = await runStore.LoadAllAsync(CancellationToken.None);
    Console.WriteLine($"PROJECT TIMELINE — {workspace.RootPath}");
    Console.WriteLine();
    if (runs.Count == 0)
    {
        Console.WriteLine("(no runs yet)");
        return 0;
    }

    string? currentDay = null;
    foreach (var run in runs)
    {
        var day = run.StartedAt.ToLocalTime().ToString("yyyy-MM-dd");
        if (day != currentDay)
        {
            Console.WriteLine(day);
            currentDay = day;
        }
        Console.WriteLine($"  {run.StartedAt.ToLocalTime():HH:mm}  {run.Status,-10} {run.Title}");
        if (run.Model is not null)
            Console.WriteLine($"           model: {run.Model}");
        if (run.Artifacts.Count > 0)
            Console.WriteLine($"           artifacts: {string.Join(", ", run.Artifacts)}");
        if (run.Decisions.Count > 0)
            Console.WriteLine($"           decisions: {string.Join("; ", run.Decisions)}");
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

var providerFactory = new ChatProviderFactory(new[] { descriptor }, http);
var artifactStore = new DiskArtifactStore(workspace);
var toolRegistry = new ToolRegistry(new ITool[]
{
    new WriteFileTool(),
    new ReadFileTool(),
    new ListDirectoryTool(),
    new RunCommandTool()
});
var contextProvider = new ContextProvider(workspace);
var modelResolver = new ModelResolver();

var developer = new Worker(
    Id: "developer",
    Role: "Developer",
    Instructions:
        "You are a developer agent working inside the user's workspace. You have these tools: "
        + "write_file (create/overwrite a file), read_file (read a file), list_dir (list a directory), "
        + "run_command (run a shell command in the workspace). All paths are relative to the workspace root. "
        + "Use the tools to accomplish the request, then reply with a short confirmation of what you did.",
    ToolAllowlist: new[] { "write_file", "read_file", "list_dir", "run_command" },
    DefaultLevel: PermissionLevel.Execute,
    ModelPolicy: new ModelPolicy(new ModelRef("ollama", model)));

var workerProvider = new StaticWorkerProvider(developer);
var planner = new Planner();
var permissionEngine = new PermissionEngine();
var decisionHandler = new ConsoleDecisionHandler();

// Workspace autonomy policy: everything is allowed at Execute level, but run_command always asks first.
var permissionPolicy = new PermissionPolicy(
    PermissionLevel.Execute,
    Allow: new[] { "*" },
    AskBefore: new[] { "run_command" });

var orchestrator = new Orchestrator(
    providerFactory, modelResolver, workerProvider, toolRegistry,
    artifactStore, workspace, planner, permissionEngine, decisionHandler,
    permissionPolicy, new EmptyServiceProvider());
var runRecorder = new RunRecorder(runStore);

// ── Run ──────────────────────────────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("AIClient — vertical slice");
Console.WriteLine($"  Workspace : {workspace.RootPath}");
Console.WriteLine($"  Provider  : {descriptor.DisplayName} ({baseUrl})");
Console.WriteLine($"  Model     : {model}");
Console.WriteLine($"  Command   : {command}");
Console.WriteLine(new string('-', 72));

var focus = new IntentFocus(workspace.Id);
var workContext = await contextProvider.BuildAsync(focus, cts.Token);
var intent = new Intent(Guid.NewGuid(), command, IntentSource.CommandBar, workContext, DateTimeOffset.UtcNow);

var streaming = false;
try
{
    await foreach (var ev in runRecorder.RecordAsync(orchestrator.SubmitIntentAsync(intent, cts.Token), cts.Token))
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

Console.WriteLine(new string('-', 72));
Console.WriteLine("Done.");
return 0;

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
        if (!string.IsNullOrEmpty(request.Detail))
            Console.WriteLine($"  {request.Detail}");
        foreach (var option in request.Options)
            Console.WriteLine($"    [{option.Id}] {option.Label}");
        Console.Write($"  Choose (default {request.RecommendedOptionId}): ");

        var line = Console.ReadLine();
        var choice = string.IsNullOrWhiteSpace(line)
            ? request.RecommendedOptionId ?? request.Options[0].Id
            : line.Trim();

        var match = request.Options.FirstOrDefault(o => string.Equals(o.Id, choice, StringComparison.OrdinalIgnoreCase));
        var chosen = match?.Id ?? request.RecommendedOptionId ?? request.Options[0].Id;
        return Task.FromResult(new DecisionOutcome(chosen));
    }
}
