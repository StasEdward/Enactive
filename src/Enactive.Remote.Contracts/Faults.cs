namespace Enactive.Remote.Contracts;

/// <summary>
/// What a Host should do with an item the gateway refused.
///
/// <para>This is the piece the preview had no answer for: every rejection arrived as one exception
/// carrying a sentence, so a Host holding a durable outbox could not tell "the network dropped it"
/// from "this run ended an hour ago". Both look like a failed call, and the two correct responses
/// are opposites - keep it forever, or never send it again.</para>
/// </summary>
public enum FaultDisposition
{
    /// <summary>The condition is transient. Keep the item and send it again, unchanged.</summary>
    Retry,

    /// <summary>Already settled at the other end. Remove the item; sending it again cannot help.</summary>
    Drop,

    /// <summary>This connection is over. Stop, keep the queue, and tell the user.</summary>
    Fatal
}

/// <summary>
/// The codes a gateway rejection can carry. Strings on the wire so a newer gateway can name a
/// condition an older Host has never heard of - see <see cref="RemoteFaults.DispositionOf"/> for
/// what happens then.
/// </summary>
public static class FaultCode
{
    public const string RunEnded = "run-ended";
    public const string InvalidTransition = "invalid-transition";
    public const string SequenceAlreadyApplied = "sequence-already-applied";
    public const string ApprovalAlreadyResolved = "approval-already-resolved";
    public const string ApprovalNotRemotelyDecidable = "approval-not-remotely-decidable";
    public const string ActionHashMismatch = "action-hash-mismatch";
    public const string CommandExpired = "command-expired";
    public const string MalformedEvent = "malformed-event";
    public const string UnknownEventKind = "unknown-event-kind";
    public const string UnknownRun = "unknown-run";
    public const string UnknownApproval = "unknown-approval";
    public const string UnknownHost = "unknown-host";
    public const string HostRevoked = "host-revoked";
}

/// <summary>
/// One refusal. <see cref="Disposition"/> is what the gateway believes; a Host classifies the
/// <see cref="Code"/> itself with <see cref="RemoteFaults.DispositionOf"/> and uses that, because
/// what a Host does with its own durable queue is not a decision the far end gets to make.
/// </summary>
public sealed record RemoteFault(string Code, FaultDisposition Disposition, string Message);

/// <summary>
/// What a hub method answers with: a value, or a refusal.
///
/// <para><b>A refusal is a RETURN VALUE and not an exception,</b> and that is the whole point of
/// this type. The first version put the fault code at the front of a <c>HubException</c>'s message,
/// which is the only channel an exception has - and it does not survive the trip. SignalR prefixes
/// the text with "An unexpected error occurred invoking 'X' on the server", so the code is no
/// longer at the front; and outside Development the server does not send the exception's text at
/// all, so the code is not there in any position. Every refusal would have reached the Host as an
/// unrecognised failure, which its own rules correctly treat as a transport problem - and retry,
/// for ever.</para>
///
/// <para>Nothing before the end-to-end test could have found that: both sides were tested against a
/// stand-in for the other, and a stand-in raises the exception the code expects.</para>
/// </summary>
public sealed record HostReply<T>(T? Value = default, RemoteFault? Fault = null)
{
    public static HostReply<T> Ok(T value) => new(value);

    public static HostReply<T> Refused(RemoteFault fault) => new(Fault: fault);
}

/// <summary>The classification table, and the rule for a code that is not in it.</summary>
public static class RemoteFaults
{
    private static readonly Dictionary<string, FaultDisposition> Table = new(StringComparer.Ordinal)
    {
        // Settled at the other end. The run is over, the sequence already landed, the request was
        // already answered, or the action no longer matches the one that was approved. Every one of
        // these is a fact about the past, and the past does not change on the second attempt.
        [FaultCode.RunEnded] = FaultDisposition.Drop,
        [FaultCode.InvalidTransition] = FaultDisposition.Drop,
        [FaultCode.SequenceAlreadyApplied] = FaultDisposition.Drop,
        [FaultCode.ApprovalAlreadyResolved] = FaultDisposition.Drop,
        [FaultCode.ApprovalNotRemotelyDecidable] = FaultDisposition.Drop,
        [FaultCode.ActionHashMismatch] = FaultDisposition.Drop,
        [FaultCode.CommandExpired] = FaultDisposition.Drop,
        [FaultCode.UnknownRun] = FaultDisposition.Drop,
        [FaultCode.UnknownApproval] = FaultDisposition.Drop,

        // Our own bug. Dropped rather than retried - a message the gateway cannot read will not
        // become readable - and logged loudly, because a silent drop here is a lost event nobody
        // ever hears about.
        [FaultCode.MalformedEvent] = FaultDisposition.Drop,
        [FaultCode.UnknownEventKind] = FaultDisposition.Drop,

        // The credential is gone. Reconnecting with it is the definition of pointless.
        [FaultCode.UnknownHost] = FaultDisposition.Fatal,
        [FaultCode.HostRevoked] = FaultDisposition.Fatal
    };

    /// <summary>
    /// How to treat a refusal.
    ///
    /// <para>An unknown code is <see cref="FaultDisposition.Retry"/>. That is the choice that never
    /// silently loses an event - and it is safe only because the Host caps attempts and parks an
    /// item it cannot get rid of, telling the user, instead of jamming the run's queue behind it
    /// forever. Dropping the unknown would be the other failure: quiet, and undiagnosable.</para>
    /// </summary>
    public static FaultDisposition DispositionOf(string? code)
        => code is not null && Table.TryGetValue(code, out var disposition)
            ? disposition
            : FaultDisposition.Retry;

    /// <summary>Every code this build knows, for the test that asserts none was left unclassified.</summary>
    public static IReadOnlyCollection<string> KnownCodes => Table.Keys;
}
