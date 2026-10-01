namespace Enactive.Remote.Gateway.Storage;

using Enactive.Remote.Contracts;

// The gateway's rows, one record per table read back.
//
// Deliberately not the same types as Enactive.Remote.Contracts. A row carries things the wire must
// never see - a host's token hash above all - and the wire carries things no row has. Sharing one type
// between them is how a token hash ends up in a JSON response because somebody added a field to a
// record and nothing said no.

internal sealed record RunRow(
    string Id,
    string OwnerId,
    string HostId,
    RemoteRunStatus Status,
    long AppliedSequence);
