namespace Enactive.Server;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class GatewayService(GatewayStore store)
{
    public static string Id() => Guid.NewGuid().ToString("N");
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Required(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new ApiError(400, $"Invalid {field} (1–{max} characters required).");
        return value.Trim();
    }
    private static bool Terminal(string status) => status is "Completed" or "Failed" or "Incomplete" or "Cancelled" or "Interrupted";
    private static HostDevice Host(GatewayState s, string id) => s.Hosts.Find(h => h.Id == id && !h.Revoked) ?? throw new ApiError(404, "Host unavailable.");
    private static RemoteRun Run(GatewayState s, string host, string id) => s.Runs.Find(r => r.Id == id && r.HostId == host) ?? throw new ApiError(404, "Run not found.");

    public object Snapshot() => store.Read(s => new
    {
        hosts = s.Hosts.Select(h => new { h.Id, h.Name, h.Revoked, h.LastSeenAt, h.Workspaces,
            online = !h.Revoked && h.LastSeenAt > DateTimeOffset.UtcNow.AddSeconds(-45) }),
        tasks = s.Tasks, runs = s.Runs, approvals = s.Approvals,
        notices = s.Notices.OrderByDescending(n => n.At).Take(100),
        events = s.Events.TakeLast(200),
        commands = s.Commands.TakeLast(100).Select(c => new { c.Id, c.Kind, c.Status, c.CreatedAt })
    });

    public object Register(string name)
    {
        name = Required(name, 80, "host name");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var host = new HostDevice(Id(), name, Hash(token), false, null, []);
        store.Change(s => { s.Hosts.Add(host); return true; });
        return new { host.Id, host.Name, token };
    }

    public void Revoke(string id) => store.Change(s =>
    {
        var host = Host(s, id);
        s.Hosts[s.Hosts.IndexOf(host)] = host with { Revoked = true, LastSeenAt = null };
        foreach (var command in s.Commands.Where(c => c.HostId == id && c.Status == "PendingDelivery").ToArray())
            s.Commands[s.Commands.IndexOf(command)] = command with { Status = "Rejected" };
        return true;
    });

    public RemoteTask Create(CreateTaskInput input) => store.Change(s =>
    {
        var host = Host(s, input.HostId);
        if (!host.Workspaces.Any(w => w.Id == input.WorkspaceId)) throw new ApiError(400, "Select a workspace registered by this Host.");
        var task = new RemoteTask(Id(), host.Id, input.WorkspaceId, Required(input.Title, 140, "title"), Required(input.Prompt, 16000, "prompt"), DateTimeOffset.UtcNow);
        s.Tasks.Add(task);
        return task;
    });

    private static RemoteCommand? Existing(GatewayState s, string id, string fingerprint)
    {
        if (!Guid.TryParse(id, out _)) throw new ApiError(400, "CommandId must be a UUID.");
        var previous = s.Commands.Find(c => c.Id == id);
        if (previous is not null && previous.Fingerprint != fingerprint) throw new ApiError(409, "CommandId already used for another action.");
        return previous;
    }

    private static RemoteCommand Queue(GatewayState s, string id, string host, string kind, object payload, string fingerprint)
    {
        var command = new RemoteCommand(id, host, kind, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), fingerprint,
            "PendingDelivery", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24));
        s.Commands.Add(command);
        return command;
    }

    public RemoteCommand Start(string taskId, string commandId) => store.Change(s =>
    {
        var fingerprint = Hash("StartTask:" + taskId);
        if (Existing(s, commandId, fingerprint) is { } previous) return previous;
        var task = s.Tasks.Find(t => t.Id == taskId) ?? throw new ApiError(404, "Task not found.");
        Host(s, task.HostId);
        if (s.Runs.Any(r => r.TaskId == taskId && !Terminal(r.Status))) throw new ApiError(409, "This task already has an active run.");
        var run = new RemoteRun(Id(), task.Id, task.HostId, "Queued", DateTimeOffset.UtcNow);
        s.Runs.Add(run);
        return Queue(s, commandId, task.HostId, "StartTask", new { runId = run.Id, task }, fingerprint);
    });

    public RemoteCommand Cancel(string runId, string commandId) => store.Change(s =>
    {
        var fingerprint = Hash("CancelRun:" + runId);
        if (Existing(s, commandId, fingerprint) is { } previous) return previous;
        var run = s.Runs.Find(r => r.Id == runId) ?? throw new ApiError(404, "Run not found.");
        Host(s, run.HostId);
        if (Terminal(run.Status)) throw new ApiError(409, "Run has already ended.");
        s.Runs[s.Runs.IndexOf(run)] = run with { Status = "CancelRequested" };
        return Queue(s, commandId, run.HostId, "CancelRun", new { runId }, fingerprint);
    });

    public RemoteCommand Decide(string approvalId, DecisionInput input) => store.Change(s =>
    {
        if (input.Decision is not ("allow" or "deny")) throw new ApiError(400, "Choose allow or deny.");
        var fingerprint = Hash($"ResolveApproval:{approvalId}:{input.Decision}:{input.ActionHash}");
        if (Existing(s, input.CommandId, fingerprint) is { } previous) return previous;
        var approval = s.Approvals.Find(a => a.Id == approvalId) ?? throw new ApiError(404, "Approval not found.");
        Host(s, approval.HostId);
        if (approval.Status != "Pending" || approval.ExpiresAt <= DateTimeOffset.UtcNow || approval.ActionHash != input.ActionHash)
            throw new ApiError(409, "Approval is no longer available. Refresh the task.");
        var run = Run(s, approval.HostId, approval.RunId);
        if (Terminal(run.Status) || run.Status == "CancelRequested") throw new ApiError(409, "Run is no longer accepting decisions.");
        s.Approvals[s.Approvals.IndexOf(approval)] = approval with { Status = "DecisionQueued", RequestedDecision = input.Decision };
        return Queue(s, input.CommandId, approval.HostId, "ResolveApproval", new { approvalId, approval.RunId, approval.ToolCallId, approval.ActionHash, decision = input.Decision }, fingerprint);
    });

    public List<RemoteCommand> Sync(string hostId, List<Workspace> workspaces) => store.Change(s =>
    {
        var host = Host(s, hostId);
        if (workspaces.Count > 100 || workspaces.Select(w => w.Id).Distinct().Count() != workspaces.Count) throw new ApiError(400, "Invalid workspaces.");
        foreach (var w in workspaces) { Required(w.Id, 100, "workspace id"); Required(w.Name, 100, "workspace name"); }
        s.Hosts[s.Hosts.IndexOf(host)] = host with { LastSeenAt = DateTimeOffset.UtcNow, Workspaces = workspaces };
        Expire(s);
        return s.Commands.Where(c => c.HostId == hostId && c.Status == "PendingDelivery").ToList();
    });

    public void Acknowledge(string hostId, string commandId) => store.Change(s =>
    {
        Host(s, hostId);
        Expire(s);
        var c = s.Commands.Find(c => c.Id == commandId && c.HostId == hostId) ?? throw new ApiError(404, "Command not found.");
        if (c.Status == "AcceptedByHost") return true;
        if (c.Status != "PendingDelivery") throw new ApiError(409, "Command no longer deliverable.");
        s.Commands[s.Commands.IndexOf(c)] = c with { Status = "AcceptedByHost" };
        return true;
    });

    public void Publish(string hostId, HostEvent ev) => store.Change(s =>
    {
        Host(s, hostId);
        Required(ev.EventId, 100, "event id");
        if (s.Events.Any(e => e.Id == ev.EventId && e.HostId == hostId)) return true;
        var run = Run(s, hostId, ev.RunId);
        if (Terminal(run.Status)) throw new ApiError(409, "Run has ended.");
        var detail = ev.Detail ?? "";
        if (detail.Length > 16000) throw new ApiError(400, "Event detail too long.");
        var now = DateTimeOffset.UtcNow;
        var status = run.Status;
        switch (ev.Kind)
        {
            case "Running":
                if (status != "Queued") throw new ApiError(409, "Run cannot be started in this state.");
                status = "Running";
                break;
            case "Progress": break;
            case "ApprovalRequested":
                if (status is not ("Running" or "WaitingForUser")) throw new ApiError(409, "Run cannot request approval in this state.");
                var approvalId = Required(ev.ApprovalId, 100, "approval id");
                if (s.Approvals.Any(a => a.Id == approvalId)) throw new ApiError(409, "ApprovalId already exists.");
                s.Approvals.Add(new Approval(approvalId, hostId, run.Id,
                    Required(ev.ToolCallId, 100, "tool call id"), Required(ev.Tool, 200, "tool"),
                    Required(ev.Arguments, 24000, "arguments"), Required(ev.WorkingDirectory, 1000, "working directory"),
                    detail, Required(ev.ActionHash, 128, "action hash"), "Pending", now, now.AddHours(24)));
                status = "WaitingForUser";
                s.Notices.Add(new Notice(Id(), run.Id, "Permission required", detail, now));
                break;
            case "ApprovalResolved":
                var approval = s.Approvals.Find(a => a.Id == ev.ApprovalId && a.RunId == run.Id && a.HostId == hostId)
                    ?? throw new ApiError(404, "Approval not found.");
                if (approval.Status is not ("Pending" or "DecisionQueued")) throw new ApiError(409, "Approval already resolved.");
                if (detail is not ("Allowed" or "Denied" or "Expired" or "Invalidated")) throw new ApiError(400, "Invalid approval outcome.");
                if (ev.ActionHash != approval.ActionHash) throw new ApiError(409, "Action hash mismatch.");
                s.Approvals[s.Approvals.IndexOf(approval)] = approval with { Status = detail };
                if (status != "CancelRequested") status = s.Approvals.Any(a => a.RunId == run.Id && a.Status is "Pending" or "DecisionQueued") ? "WaitingForUser" : "Running";
                break;
            case "Completed": case "Failed": case "Incomplete": case "Cancelled": case "Interrupted":
                if (status == "Queued" && ev.Kind == "Completed") throw new ApiError(409, "Run has not started.");
                status = ev.Kind;
                foreach (var a in s.Approvals.Where(a => a.RunId == run.Id && a.Status is "Pending" or "DecisionQueued").ToArray())
                    s.Approvals[s.Approvals.IndexOf(a)] = a with { Status = "Invalidated" };
                s.Notices.Add(new Notice(Id(), run.Id, status, detail, now));
                break;
            default: throw new ApiError(400, "Unknown event kind.");
        }
        s.Runs[s.Runs.IndexOf(run)] = run with { Status = status, Summary = Terminal(status) ? detail : run.Summary };
        s.Events.Add(new RecordedEvent(ev.EventId, hostId, run.Id, ev.Kind, detail, now));
        return true;
    });

    public void MarkRead() => store.Change(s => { s.Notices = s.Notices.Select(n => n with { Read = true }).ToList(); return true; });

    private static void Expire(GatewayState s)
    {
        foreach (var command in s.Commands.Where(c => c.Status == "PendingDelivery" && c.ExpiresAt <= DateTimeOffset.UtcNow).ToArray())
        {
            s.Commands[s.Commands.IndexOf(command)] = command with { Status = "Expired" };
            if (command.Kind != "StartTask") continue;
            var runId = JsonDocument.Parse(command.Payload).RootElement.GetProperty("runId").GetString();
            var run = s.Runs.Find(r => r.Id == runId);
            if (run is null || Terminal(run.Status)) continue;
            s.Runs[s.Runs.IndexOf(run)] = run with { Status = "Incomplete", Summary = "Start command expired before delivery." };
            s.Notices.Add(new Notice(Id(), run.Id, "Incomplete", "Start command expired before delivery.", DateTimeOffset.UtcNow));
        }
    }
}
