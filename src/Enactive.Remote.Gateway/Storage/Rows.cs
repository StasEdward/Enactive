namespace Enactive.Remote.Gateway.Storage;

using Enactive.Remote.Contracts;

/// <summary>
/// The gateway's rows, one record per table.
///
/// <para>Deliberately not the same types as <c>Enactive.Remote.Contracts</c>. A row carries things
/// the wire must never see - <see cref="HostRow.TokenHash"/> above all - and the wire carries
/// things no row has. Sharing one type between them is how a token hash ends up in a JSON response
/// because somebody added a field to a record and nothing said no.</para>
/// </summary>
internal static class Rows;

internal sealed record HostRow(
    string Id,
    string Name,
    string TokenHash,
    bool Revoked,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt);

internal sealed record TaskRow(
    string Id,
    string HostId,
    string WorkspaceId,
    string Title,
    string Prompt,
    DateTimeOffset CreatedAt);

internal sealed record RunRow(
    string Id,
    string TaskId,
    string HostId,
    RemoteRunStatus Status,
    long AppliedSequence,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt,
    string? Summary);

internal sealed record CommandRow(
    string Id,
    string HostId,
    CommandKind Kind,
    string Payload,
    string Fingerprint,
    CommandStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

internal sealed record ApprovalRow(
    string Id,
    string HostId,
    string RunId,
    string ToolCallId,
    string Tool,
    string Arguments,
    string WorkingDirectory,
    string Reason,
    string ActionHash,
    bool RemoteDecidable,
    ApprovalStatus Status,
    RemoteDecision? RequestedDecision,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
