namespace Enactive.Remote.Gateway.Services;

/// <summary>
/// How much one account may hold or start. A public service shared by many people needs a ceiling on
/// each, or one account's runaway script fills the database and the queue for everybody.
///
/// <para>Only the shape exists so far: the services take a <see cref="Limits"/> now so their
/// signatures do not change again when the limits are enforced. Until then they are given
/// <see cref="Unlimited"/>.</para>
/// </summary>
public sealed record Limits(
    int HostsPerUser,
    int DevicesPerUser,
    int ActiveRunsPerUser,
    int QueuedCommandsPerHost,
    int TasksPerDay,
    int OpenInvitesPerUser,
    long SealedBytesPerUser)
{
    /// <summary>No ceiling on anything: what the services are given until limits are enforced.</summary>
    public static Limits Unlimited { get; } = new(
        int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, long.MaxValue);
}
