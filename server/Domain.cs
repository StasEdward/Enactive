namespace Enactive.Server;

public sealed class GatewayState
{
    public int SchemaVersion { get; set; } = 1;
    public List<HostDevice> Hosts { get; set; } = [];
    public List<RemoteTask> Tasks { get; set; } = [];
    public List<RemoteRun> Runs { get; set; } = [];
    public List<RemoteCommand> Commands { get; set; } = [];
    public List<Approval> Approvals { get; set; } = [];
    public List<Notice> Notices { get; set; } = [];
    public List<RecordedEvent> Events { get; set; } = [];
}

public sealed record Workspace(string Id, string Name);
public sealed record HostDevice(string Id, string Name, string TokenHash, bool Revoked,
    DateTimeOffset? LastSeenAt, List<Workspace> Workspaces);
public sealed record RemoteTask(string Id, string HostId, string WorkspaceId, string Title,
    string Prompt, DateTimeOffset CreatedAt);
public sealed record RemoteRun(string Id, string TaskId, string HostId, string Status,
    DateTimeOffset CreatedAt, string? Summary = null);
public sealed record RemoteCommand(string Id, string HostId, string Kind, string Payload,
    string Fingerprint, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record Approval(string Id, string HostId, string RunId, string ToolCallId,
    string Tool, string Arguments, string WorkingDirectory, string Reason, string ActionHash,
    string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? RequestedDecision = null);
public sealed record Notice(string Id, string RunId, string Title, string Detail, DateTimeOffset At, bool Read = false);
public sealed record RecordedEvent(string Id, string HostId, string RunId, string Kind, string Detail, DateTimeOffset At);
public sealed record CreateTaskInput(string HostId, string WorkspaceId, string Title, string Prompt);
public sealed record CommandInput(string CommandId);
public sealed record DecisionInput(string CommandId, string Decision, string ActionHash);
public sealed record HostEvent(string EventId, string RunId, string Kind, string? Detail,
    string? ApprovalId = null, string? ToolCallId = null, string? Tool = null,
    string? Arguments = null, string? WorkingDirectory = null, string? ActionHash = null);
public sealed record LoginInput(string Key);
public sealed record RegisterHostInput(string Name);
public sealed class ApiError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
