using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Server;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;

// ServerHarness --data <folder> --port <n>
//
// The real service on https://localhost:<port>, answering 127.0.0.0/8, with four accounts (ada the Admin, alice and bob
// Operators, vera a Viewer, all with the password below) and a folder "project" in the data folder to run in. A run asks once
// for write_file - allowed, it writes digest.md - and ends when answered; run in the background, it stops at the question for
// the inbox. Prints "ready" when it listens.

const string Password = "e2e password 1234";

string Arg(string name) => args[Array.IndexOf(args, name) + 1];
var data = Path.GetFullPath(Arg("--data"));
var port = int.Parse(Arg("--port"));
Directory.CreateDirectory(Path.Combine(data, "project"));
Directory.CreateDirectory(Path.Combine(data, "reports"));

var users = new UserStore(data);
foreach (var (name, role) in new[] { ("ada", Role.Admin), ("alice", Role.Operator), ("bob", Role.Operator), ("vera", Role.Viewer) })
    if (users.Find(name) is null)
        users.Add(name, Password, role);

using var key = RSA.Create(2048);
var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var names = new SubjectAlternativeNameBuilder();
names.AddDnsName("localhost");
request.CertificateExtensions.Add(names.Build());
using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
// Through a .pfx round trip: SChannel cannot use a key that lives only in the process (ServerCertificate).
using var certificate = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.UserKeySet);

var options = new ServerOptions
{
    Listen = [$"127.0.0.1:{port}"],
    AllowedSubnets = ["127.0.0.0/8"],
    HostNames = ["localhost"],
    WorkspaceRoots = [data],
    Certificate = new CertificateOptions(Path: "in-memory"),
    MaxActiveRuns = 2
};

var app = ServerApp.Build([], options, data, certificate,
    b => b.Services.AddSingleton<IRunExecutor>(new AskingOnce(new ApprovalStore(Path.Combine(data, "permissions.json")))));
// The folder is one the service has run in, as the engine's executor would have recorded it on a first run: the
// workspace and history pages list it.
var project = Path.Combine(data, "project");
app.Services.GetRequiredService<ServerEngine>().Workspaces.Touch(project);

// A staged run of alice's that ended with one proposal waiting - proposal.md's second draft - kept as EngineExecutor
// keeps a browser run's (ServerStaging), for the proposals page to apply.
File.WriteAllText(Path.Combine(project, "proposal.md"), "first draft\n");
{
    var staging = app.Services.GetRequiredService<ServerStaging>();
    var folder = staging.Begin(project, Guid.NewGuid(), "alice", "redraft the proposal");
    using var store = new Enactive.Workspace.StagingArtifactStore(project, folder);
    await store.CreateAsync("proposal.md", Enactive.Core.Artifacts.ArtifactKind.FileSet, "proposal",
        s => s.WriteAsync("second draft\n"u8.ToArray()).AsTask(), CancellationToken.None);
    await staging.EndAsync(project, folder, "redraft the proposal", null, store);
}

// A background run of "mail the digest" that stopped at a question nobody was there to answer, filed in the inbox as the
// engine's BackgroundRunner files one: the question kept for its task (DecisionLedger), the run in the history, the item.
{
    var taskId = Guid.NewGuid();
    new Enactive.Agents.DecisionLedger(project).Park(new DecisionRequest(taskId, "Approve send_email", "to the team",
        [new("allow", "Allow"), new("deny", "Deny")], "allow", Subject: "send_email"));
    var workspace = Enactive.Core.Context.WorkspaceInfo.For(project);
    var at = DateTimeOffset.Now.AddMinutes(-5);
    var runId = Guid.NewGuid();
    await Enactive.Workspace.RunStoreFactory.Create(workspace).SaveAsync(new Enactive.Core.History.RunRecord(runId, taskId,
        "mail the digest", "scripted", at, at.AddSeconds(3), "NeedsUser", [], [], []), CancellationToken.None);
    await Enactive.Workspace.InboxStoreFactory.Create(workspace).AppendAsync(new Enactive.Core.Inbox.InboxItem(Guid.NewGuid(),
        workspace.Id, "decision", "mail the digest", "Waiting for your decision", runId, "unread", at), CancellationToken.None);
}
await app.StartAsync();
Console.WriteLine("ready");
await app.WaitForShutdownAsync();

/// <summary>
/// A run that starts a step, asks once to write a file, and ends with what it was told. Asked as the engine asks
/// (LedgeredDecisions, Orchestrator.StopsAtQuestions): a question its task was already answered in the inbox is given that
/// answer without asking again, and a question nobody is there for - a background run's - is kept for its task and the run
/// stops at it, NeedsUser, for the inbox to carry on from.
/// </summary>
internal sealed class AskingOnce(ApprovalStore approvals) : IRunExecutor
{
    public string? Problem => null;
    public SessionApprovals Session { get; } = new();
    public ApprovalStore Approvals => approvals;

    public async Task ExecuteAsync(ServerRun run, IDecisionHandler decisions, CancellationToken ct)
    {
        var events = Channel.CreateUnbounded<WorkEvent>();
        var drained = run.Session.DrainAsync(events.Reader.ReadAllAsync(ct), ct);
        var runId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        // The task the run is of: a run carried on from the inbox is a new run under its task, and the answer is the task's.
        var taskId = run.TaskId ?? Guid.NewGuid();
        var ledger = new Enactive.Agents.DecisionLedger(run.Workspace);
        WorkEvent Ev(EventKind kind, string summary, string? payload = null)
            => new(Guid.NewGuid(), stepId, runId, DateTimeOffset.UtcNow, kind, summary, payload);

        // Titled as its task was asked: each run reads as its own in the inbox and on its page.
        events.Writer.TryWrite(Ev(EventKind.PlanCreated, "Plan: " + run.Prompt, WorkEventPayload.PlanPayload(run.Prompt, ["write the digest"])));
        events.Writer.TryWrite(Ev(EventKind.StepStarted, "step 1: write the digest", """{"step":1}"""));
        try
        {
            var request = new DecisionRequest(taskId, "Approve write_file", "digest.md",
                [new("allow", "Allow"), new("deny", "Deny")], "allow", Subject: "write_file",
                Action: new(runId, Guid.NewGuid().ToString(), "write_file", """{"path":"digest.md"}""", run.Workspace));
            DecisionOutcome answer;
            try
            {
                answer = ledger.Answered(request) ?? await decisions.RequestAsync(request, ct);
            }
            catch (Enactive.Agents.DecisionPendingException)
            {
                ledger.Park(request);
                var reason = "waiting for your decision: Approve write_file digest.md - answer it, and the run goes on from where it stopped.";
                events.Writer.TryWrite(Ev(EventKind.TaskFailed, $"{RunOutcomeKind.NeedsUser}: {reason}",
                    WorkEventPayload.OutcomePayload(RunOutcomeKind.NeedsUser, reason)));
                await RecordAsync(run, runId, taskId, nameof(RunOutcomeKind.NeedsUser), []);
                return;
            }
            events.Writer.TryWrite(Ev(EventKind.DecisionResolved, "write_file: " + answer.OptionId));
            // Allowed, the file is written and the run says so, as the engine's would: the run's Artifacts have a file to
            // review and delete.
            string[] wrote = answer.OptionId == "allow" ? ["digest.md"] : [];
            foreach (var file in wrote)
            {
                await File.WriteAllTextAsync(Path.Combine(run.Workspace, file), "the digest", CancellationToken.None);
                events.Writer.TryWrite(Ev(EventKind.ArtifactProduced, "Wrote: " + file, WorkEventPayload.ArtifactPayload("document", file)));
            }
            events.Writer.TryWrite(Ev(EventKind.TaskCompleted, "Completed", WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed)));
            // An end: the answers its task was given are of no more use (Orchestrator).
            ledger.Forget(taskId);
            await RecordAsync(run, runId, taskId, "Completed", wrote);
        }
        finally
        {
            events.Writer.TryComplete();
            await drained;
        }
    }

    /// <summary>Into the folder's history, as the engine's recorder would: the history and the inbox have the run to read.</summary>
    private static Task RecordAsync(ServerRun run, Guid runId, Guid taskId, string status, string[] wrote)
    {
        var at = DateTimeOffset.Now;
        return Enactive.Workspace.RunStoreFactory.Create(Enactive.Core.Context.WorkspaceInfo.For(run.Workspace)).SaveAsync(
            new Enactive.Core.History.RunRecord(runId, taskId, run.Prompt, "scripted", at, at, status,
                [new Enactive.Core.History.RunEventRecord(at, nameof(EventKind.IntentReceived), "Intent",
                    Payload: WorkEventPayload.RequestPayload(run.Prompt))], wrote, []), CancellationToken.None);
    }
}
