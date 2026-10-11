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
// for write_file and ends when answered. Prints "ready" when it listens.

const string Password = "e2e password 1234";

string Arg(string name) => args[Array.IndexOf(args, name) + 1];
var data = Path.GetFullPath(Arg("--data"));
var port = int.Parse(Arg("--port"));
Directory.CreateDirectory(Path.Combine(data, "project"));

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
    Certificate = new CertificateOptions(Path: "in-memory"),
    MaxActiveRuns = 2
};

var app = ServerApp.Build([], options, data, certificate,
    b => b.Services.AddSingleton<IRunExecutor>(new AskingOnce(new ApprovalStore(Path.Combine(data, "permissions.json")))));
// The folder is one the service has run in, as the engine's executor would have recorded it on a first run: the
// workspace and history pages list it.
app.Services.GetRequiredService<ServerEngine>().Workspaces.Touch(Path.Combine(data, "project"));
await app.StartAsync();
Console.WriteLine("ready");
await app.WaitForShutdownAsync();

/// <summary>A run that starts a step, asks once to write a file, and ends with what it was told.</summary>
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
        WorkEvent Ev(EventKind kind, string summary, string? payload = null)
            => new(Guid.NewGuid(), stepId, runId, DateTimeOffset.UtcNow, kind, summary, payload);

        events.Writer.TryWrite(Ev(EventKind.PlanCreated, "Plan: write the digest", """{"title":"Write the digest","steps":[{"title":"write the digest"}]}"""));
        events.Writer.TryWrite(Ev(EventKind.StepStarted, "step 1: write the digest", """{"step":1}"""));
        try
        {
            var answer = await decisions.RequestAsync(new DecisionRequest(Guid.NewGuid(), "Approve write_file", "digest.md",
                [new("allow", "Allow"), new("deny", "Deny")], "allow", Subject: "write_file",
                Action: new(runId, Guid.NewGuid().ToString(), "write_file", """{"path":"digest.md"}""", run.Workspace)), ct);
            events.Writer.TryWrite(Ev(EventKind.DecisionResolved, "write_file: " + answer.OptionId));
            events.Writer.TryWrite(Ev(EventKind.TaskCompleted, "Completed", WorkEventPayload.OutcomePayload(RunOutcomeKind.Completed)));

            // Into the folder's history, as the engine's recorder would: the history pages have something to read.
            var at = DateTimeOffset.Now;
            await Enactive.Workspace.RunStoreFactory.Create(Enactive.Core.Context.WorkspaceInfo.For(run.Workspace)).SaveAsync(
                new Enactive.Core.History.RunRecord(runId, run.TaskId ?? Guid.NewGuid(), run.Prompt, "scripted", at, at, "Completed",
                    [new Enactive.Core.History.RunEventRecord(at, nameof(EventKind.IntentReceived), "Intent",
                        Payload: WorkEventPayload.RequestPayload(run.Prompt))], [], []), CancellationToken.None);
        }
        finally
        {
            events.Writer.TryComplete();
            await drained;
        }
    }
}
